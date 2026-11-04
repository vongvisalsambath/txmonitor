using System.Globalization;
using System.Text;

namespace TxMonitor;

/// <summary>
/// Reads transactions from CSV with a header row: id, account, time, amount, currency, direction, channel, and optionally
/// counterparty, counterparty_country and description, in any order.
/// </summary>
public static class TransactionCsv
{
    /// <summary>The largest file read.</summary>
    public const int MaxBytes = 64 * 1024 * 1024;
    /// <summary>The most transactions read.</summary>
    public const int MaxRows = 1_000_000;
    /// <summary>The longest field.</summary>
    public const int MaxField = 1000;

    /// <summary>The columns, in the order <see cref="Write"/> uses.</summary>
    public static readonly IReadOnlyList<string> Columns = ["id", "account", "time", "amount", "currency", "direction", "channel", "counterparty", "counterparty_country", "description"];
    private static readonly string[] Required = ["id", "account", "time", "amount", "currency", "direction", "channel"];

    /// <summary>Reads a file without blocking: the form to use in a browser.</summary>
    public static async Task<IReadOnlyList<Transaction>> ReadAsync(Stream input, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        if (input.CanSeek && input.Length - input.Position > MaxBytes) throw TooLarge();
        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        int n;
        while ((n = await input.ReadAsync(chunk, ct).ConfigureAwait(false)) > 0)
        {
            if (buffer.Length + n > MaxBytes) throw TooLarge();
            buffer.Write(chunk, 0, n);
        }
        return Parse(Decode(buffer.GetBuffer().AsSpan(0, (int)buffer.Length)));
    }

    /// <summary>Reads a file.</summary>
    public static IReadOnlyList<Transaction> Read(Stream input)
    {
        ArgumentNullException.ThrowIfNull(input);
        return ReadAsync(input).GetAwaiter().GetResult();
    }

    private static MonitorException TooLarge() => new([$"The file is larger than {MaxBytes / 1024 / 1024} MB."]);

    private static string Decode(ReadOnlySpan<byte> bytes)
    {
        try
        {
            return new UTF8Encoding(false, true).GetString(bytes.StartsWith("\uFEFF"u8) ? bytes[3..] : bytes);
        }
        catch (DecoderFallbackException)
        {
            throw new MonitorException(["The file isn't UTF-8 text."]);
        }
    }

    /// <summary>Reads CSV text. Throws <see cref="MonitorException"/> listing the first problems, by line.</summary>
    public static IReadOnlyList<Transaction> Parse(string csv)
    {
        ArgumentNullException.ThrowIfNull(csv);
        if (csv.Length > MaxBytes) throw TooLarge();
        var problems = new List<string>();
        var result = new List<Transaction>();
        Dictionary<string, int>? at = null;
        foreach (var (line, cells) in Rows(csv))
        {
            if (cells.Count == 1 && cells[0].Length == 0) continue;
            if (at is null)
            {
                at = new Dictionary<string, int>(StringComparer.Ordinal);
                for (var i = 0; i < cells.Count; i++)
                {
                    var name = cells[i].Trim().ToLowerInvariant();
                    if (!Columns.Contains(name)) problems.Add($"Line {line}: \"{Text.Safe(name, 40)}\" isn't a column (the columns are {string.Join(", ", Columns)}).");
                    else if (!at.TryAdd(name, i)) problems.Add($"Line {line}: the column {name} appears twice.");
                }
                foreach (var r in Required)
                    if (!at.ContainsKey(r)) problems.Add($"The header needs a {r} column.");
                if (problems.Count > 0) throw new MonitorException(problems);
                continue;
            }
            if (result.Count >= MaxRows) throw new MonitorException([$"The file has more than {MaxRows:N0} transactions."]);
            if (cells.Count != at.Count) { Add(problems, $"Line {line} has {cells.Count} cells; the header has {at.Count}."); continue; }
            if (Row(cells, at, line, problems) is { } tx) result.Add(tx);
            if (problems.Count >= 20) break;
        }
        if (problems.Count > 0)
        {
            if (problems.Count >= 20) problems.Add("Further problems weren't listed.");
            throw new MonitorException(problems);
        }
        if (at is null) throw new MonitorException(["The file is empty: it needs a header row and transactions."]);
        return result;
    }

    private static void Add(List<string> problems, string p) { if (problems.Count < 20) problems.Add(p); }

    private static Transaction? Row(List<string> cells, Dictionary<string, int> at, int line, List<string> p)
    {
        string? Get(string name) => at.TryGetValue(name, out var i) && cells[i].Trim() is { Length: > 0 } v ? v : null;
        var before = p.Count;
        var id = Get("id");
        var account = Get("account");
        if (id is null) Add(p, $"Line {line} has no id.");
        if (account is null) Add(p, $"Line {line} has no account.");
        var time = ParseTime(Get("time"));
        if (time is null) Add(p, $"Line {line}: the time must look like 2026-03-01T09:30:00Z or 2026-03-01T16:30:00+07:00.");
        var amountText = Get("amount");
        decimal amount = 0;
        if (amountText is null || amountText.Length > 30 || !decimal.TryParse(amountText, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out amount) || amount <= 0)
            Add(p, $"Line {line}: the amount must be a number above 0, such as 1250.00, with no sign, spaces or thousands separators.");
        var currency = Get("currency")?.ToUpperInvariant();
        if (currency is null || currency.Length != 3 || !currency.All(char.IsAsciiLetterUpper)) Add(p, $"Line {line}: the currency must be a three-letter code such as USD.");
        var direction = Get("direction")?.ToLowerInvariant() switch { "in" => Direction.In, "out" => Direction.Out, _ => (Direction?)null };
        if (direction is null) Add(p, $"Line {line}: the direction must be in or out.");
        var channel = Get("channel")?.ToLowerInvariant() switch
        {
            "cash" => Channel.Cash, "card" => Channel.Card, "transfer" => Channel.Transfer, "wire" => Channel.Wire, "cheque" or "check" => Channel.Cheque, "other" => Channel.Other,
            _ => (Channel?)null,
        };
        if (channel is null) Add(p, $"Line {line}: the channel must be cash, card, transfer, wire, cheque or other.");
        if (p.Count > before) return null;
        return new Transaction(id!, account!, time!.Value, amount, currency!, direction!.Value, channel!.Value, Get("counterparty"), Get("counterparty_country"), Get("description"));
    }

    private static readonly string[] TimeFormats =
    [
        "yyyy-MM-dd'T'HH:mm:ssK", "yyyy-MM-dd'T'HH:mm:ss.FFFFFFFK", "yyyy-MM-dd'T'HH:mmK", "yyyy-MM-dd HH:mm:ssK", "yyyy-MM-dd HH:mmK",
    ];

    /// <summary>An ISO 8601 time with Z or an offset: a time without one is refused, since it could be any time zone. A date alone means midnight UTC.</summary>
    internal static DateTimeOffset? ParseTime(string? s)
    {
        if (s is null || s.Length > 40) return null;
        if (s.Length == 10)
            return DateTimeOffset.TryParseExact(s, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var d) ? d : null;
        if (!(s.EndsWith('Z') || s.Length > 6 && s[^6] is '+' or '-' && s[^3] == ':')) return null;
        return DateTimeOffset.TryParseExact(s, TimeFormats, CultureInfo.InvariantCulture, DateTimeStyles.None, out var t) ? t : null;
    }

    /// <summary>RFC 4180 rows: quoted fields may hold commas, quotes ("") and line breaks.</summary>
    private static IEnumerable<(int Line, List<string> Cells)> Rows(string s)
    {
        var line = 1;
        var i = 0;
        while (i < s.Length)
        {
            var start = line;
            var cells = new List<string>();
            var cell = new StringBuilder();
            var quoted = false;
            while (true)
            {
                if (i >= s.Length)
                {
                    if (quoted) throw new MonitorException([$"Line {start}: a quoted field isn't closed."]);
                    cells.Add(cell.ToString());
                    break;
                }
                var c = s[i++];
                if (quoted)
                {
                    if (c == '"')
                    {
                        if (i < s.Length && s[i] == '"') { cell.Append('"'); i++; }
                        else quoted = false;
                    }
                    else
                    {
                        if (c == '\n') line++;
                        cell.Append(c);
                    }
                }
                else if (c == '"' && cell.Length == 0) quoted = true;
                else if (c == ',')
                {
                    cells.Add(cell.ToString());
                    cell.Clear();
                    if (cells.Count > 50) throw new MonitorException([$"Line {start} has more than 50 cells."]);
                    continue;
                }
                else if (c == '\n' || c == '\r')
                {
                    if (c == '\r' && i < s.Length && s[i] == '\n') i++;
                    line++;
                    cells.Add(cell.ToString());
                    break;
                }
                else cell.Append(c);
                if (cell.Length > MaxField) throw new MonitorException([$"Line {start} has a field longer than {MaxField} characters."]);
            }
            yield return (start, cells);
        }
    }

    /// <summary>Writes transactions in the form <see cref="Parse"/> reads.</summary>
    public static void Write(IEnumerable<Transaction> transactions, TextWriter output)
    {
        ArgumentNullException.ThrowIfNull(transactions);
        ArgumentNullException.ThrowIfNull(output);
        output.Write(string.Join(",", Columns) + "\r\n");
        foreach (var t in transactions)
        {
            output.Write(string.Join(",", new[]
            {
                t.Id, t.Account, t.Time.ToString("yyyy-MM-dd'T'HH:mm:sszzz", CultureInfo.InvariantCulture), t.Amount.ToString(CultureInfo.InvariantCulture), t.Currency,
                t.Direction.ToString().ToLowerInvariant(), t.Channel.ToString().ToLowerInvariant(), t.Counterparty ?? "", t.CounterpartyCountry ?? "", t.Description ?? "",
            }.Select(Cell)));
            output.Write("\r\n");
        }
    }

    /// <summary>
    /// A CSV cell that a spreadsheet won't run: text starting with = + - @ or a tab or return gets a leading apostrophe,
    /// and anything with a comma, quote or line break is quoted.
    /// </summary>
    public static string Cell(string s)
    {
        ArgumentNullException.ThrowIfNull(s);
        if (s.Length > 0 && s[0] is '=' or '+' or '-' or '@' or '\t' or '\r' && !IsNumber(s)) s = "'" + s;
        return s.AsSpan().IndexOfAny(",\"\r\n") >= 0 || s.Length != s.Trim().Length ? "\"" + s.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"" : s;
    }

    private static bool IsNumber(string s) => s.Length <= 30 && decimal.TryParse(s, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out _);
}

/// <summary>Writes alerts as CSV, safe to open in a spreadsheet.</summary>
public static class AlertCsv
{
    /// <summary>Writes alerts with a header row.</summary>
    public static void Write(IEnumerable<Alert> alerts, TextWriter output)
    {
        ArgumentNullException.ThrowIfNull(alerts);
        ArgumentNullException.ThrowIfNull(output);
        output.Write("time,account,rule,type,severity,explanation,transactions\r\n");
        foreach (var a in alerts)
        {
            output.Write(string.Join(",", new[]
            {
                a.Time.ToString("yyyy-MM-dd'T'HH:mm:sszzz", CultureInfo.InvariantCulture), a.Account, a.Rule, a.Type, a.Severity.ToString().ToLowerInvariant(),
                a.Explanation, string.Join(' ', a.Transactions),
            }.Select(s => TransactionCsv.Cell(Text.Safe(s, 20_000)))));
            output.Write("\r\n");
        }
    }
}
