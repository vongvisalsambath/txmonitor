using System.Text;
using TxMonitor.Cli;
using Xunit;

namespace TxMonitor.Tests;

public sealed class RuleSetTests
{
    [Fact]
    public void TheExampleIsValid_AndEveryTypeIsDocumented()
    {
        var set = RuleSet.Parse(RuleSet.Example);
        Assert.Equal("USD", set.BaseCurrency);
        Assert.Equal(9, set.Rules.Count);
        Assert.Equal(0.000245m, set.Rates["KHR"]);
        Assert.Equal(1m, set.Rates["USD"]);
        Assert.Contains("MM", set.HighRiskCountries);
        Assert.All(RuleSet.Types.Values, t => Assert.All(t.Settings.Values, s => Assert.False(string.IsNullOrWhiteSpace(s.Meaning))));
    }

    [Theory]
    [InlineData("", "valid JSON")]
    [InlineData("[]", "JSON object")]
    [InlineData("""{ "rules": [] }""", "non-empty")]
    [InlineData("""{ "rules": [ { "id": "a", "type": "large", "amount": 1 } ], "extra": 1 }""", "Unknown setting \"extra\"")]
    [InlineData("""{ "baseCurrency": "usd", "rules": [ { "id": "a", "type": "large", "amount": 1 } ] }""", "baseCurrency")]
    [InlineData("""{ "rates": { "KHR": 0 }, "rules": [ { "id": "a", "type": "large", "amount": 1 } ] }""", "above 0")]
    [InlineData("""{ "rates": { "KHR": "4100" }, "rules": [ { "id": "a", "type": "large", "amount": 1 } ] }""", "above 0")]
    [InlineData("""{ "rates": { "__proto__": 1 }, "rules": [ { "id": "a", "type": "large", "amount": 1 } ] }""", "isn't a currency code")]
    [InlineData("""{ "highRiskCountries": ["North Korea"], "rules": [ { "id": "a", "type": "large", "amount": 1 } ] }""", "two-letter")]
    [InlineData("""{ "rules": [ { "id": "a", "type": "high-risk-country" } ] }""", "needs highRiskCountries")]
    [InlineData("""{ "rules": [ { "id": "a b", "type": "large", "amount": 1 } ] }""", "needs an id")]
    [InlineData("""{ "rules": [ { "type": "large", "amount": 1 } ] }""", "needs an id")]
    [InlineData("""{ "rules": [ { "id": "a", "type": "large", "amount": 1 }, { "id": "a", "type": "large", "amount": 2 } ] }""", "used twice")]
    [InlineData("""{ "rules": [ { "id": "a", "type": "script", "code": "rm -rf" } ] }""", "isn't one of")]
    [InlineData("""{ "rules": [ { "id": "a", "type": "large" } ] }""", "needs amount")]
    [InlineData("""{ "rules": [ { "id": "a", "type": "large", "amount": 0 } ] }""", "must be from 1")]
    [InlineData("""{ "rules": [ { "id": "a", "type": "large", "amount": 1e30 } ] }""", "must be")]
    [InlineData("""{ "rules": [ { "id": "a", "type": "large", "amount": "10000" } ] }""", "must be a number")]
    [InlineData("""{ "rules": [ { "id": "a", "type": "large", "amount": 1, "hours": 3 } ] }""", "has no setting \"hours\"")]
    [InlineData("""{ "rules": [ { "id": "a", "type": "large", "amount": 1, "severity": "urgent" } ] }""", "severity")]
    [InlineData("""{ "rules": [ { "id": "a", "type": "large", "amount": 1, "severity": "2" } ] }""", "severity")]
    [InlineData("""{ "rules": [ { "id": "a", "type": "large", "amount": 1, "channels": ["crypto"] } ] }""", "isn't a channel")]
    [InlineData("""{ "rules": [ { "id": "a", "type": "large", "amount": 1, "channels": ["1"] } ] }""", "isn't a channel")]
    [InlineData("""{ "rules": [ { "id": "a", "type": "large", "amount": 1, "direction": "sideways" } ] }""", "direction is in or out")]
    [InlineData("""{ "rules": [ { "id": "a", "type": "fan-in", "counterparties": 5, "direction": "out" } ] }""", "sets its own direction")]
    [InlineData("""{ "rules": [ { "id": "a", "type": "velocity", "count": 1, "hours": 1 } ] }""", "count must be from 2")]
    [InlineData("""{ "rules": [ { "id": "a", "type": "structuring", "threshold": 10000, "near": 1 } ] }""", "near must be")]
    [InlineData("""{ "rules": [ [ [ [ [ [ [ [ [ [ 1 ] ] ] ] ] ] ] ] ] ] }""", "valid JSON")]
    public void ABadRuleSetIsRefused_SayingWhy(string json, string reason)
    {
        var e = Assert.Throws<MonitorException>(() => RuleSet.Parse(json));
        Assert.Contains(e.Problems, p => p.Contains(reason, StringComparison.Ordinal));
    }

    [Fact]
    public void EveryProblemIsListed_AtOnce()
    {
        var e = Assert.Throws<MonitorException>(() => RuleSet.Parse("""{ "baseCurrency": "x", "rules": [ { "id": "a", "type": "large" }, { "id": "b", "type": "nope" } ] }"""));
        Assert.Equal(3, e.Problems.Count);
    }

    [Fact]
    public void HostileRuleSetsAreBounded()
    {
        Assert.Contains("longer than", Assert.Throws<MonitorException>(() => RuleSet.Parse(new string(' ', RuleSet.MaxBytes + 1) + "{}")).Message, StringComparison.Ordinal);
        var many = "{ \"rules\": [" + string.Join(",", Enumerable.Range(0, 201).Select(i => $$"""{ "id": "r{{i}}", "type": "large", "amount": 1 }""")) + "] }";
        Assert.Contains("at most 200", Assert.Throws<MonitorException>(() => RuleSet.Parse(many)).Message, StringComparison.Ordinal);
        // names in messages are cut short and stripped of control characters
        var e = Assert.Throws<MonitorException>(() => RuleSet.Parse("{ \"\\u001b[2J" + new string('x', 500) + "\": 1, \"rules\": [ { \"id\": \"a\", \"type\": \"large\", \"amount\": 1 } ] }"));
        Assert.DoesNotContain('\u001b', e.Message);
        Assert.True(e.Message.Length < 300);
    }

    [Fact]
    public void CommentsAndTrailingCommasAreAllowed() =>
        Assert.Single(RuleSet.Parse("""
            // a comment
            { "rules": [ { "id": "a", "type": "large", "amount": 1, }, ], }
            """).Rules);
}

public sealed class CsvTests
{
    private const string Header = "id,account,time,amount,currency,direction,channel,counterparty,counterparty_country,description\r\n";

    [Fact]
    public void TransactionsAreRead_InAnyColumnOrder()
    {
        var txs = TransactionCsv.Parse("channel,direction,currency,amount,time,account,id,counterparty\n" +
            "cash,in,usd,9500.50,2026-05-04T09:00:00+07:00,A1,t1,\"Smith, J\"\n" +
            "Cheque,OUT,KHR,4100000,2026-05-04,A1,t2,\n");
        Assert.Equal(2, txs.Count);
        Assert.Equal(new Transaction("t1", "A1", new DateTimeOffset(2026, 5, 4, 9, 0, 0, TimeSpan.FromHours(7)), 9500.50m, "USD", Direction.In, Channel.Cash, "Smith, J"), txs[0]);
        Assert.Equal((Channel.Cheque, Direction.Out, TimeSpan.Zero, (string?)null), (txs[1].Channel, txs[1].Direction, txs[1].Time.Offset, txs[1].Counterparty));
    }

    [Fact]
    public void WriteAndReadRoundTrip()
    {
        var txs = Sample.Generate(7).Take(500).ToList();
        var w = new StringWriter();
        TransactionCsv.Write(txs, w);
        Assert.Equal(txs, TransactionCsv.Parse(w.ToString()));
    }

    [Theory]
    [InlineData("t1,A1,2026-05-04T09:00:00,10,USD,in,cash,,,", "the time must")]          // no offset: which time zone?
    [InlineData("t1,A1,04/05/2026,10,USD,in,cash,,,", "the time must")]
    [InlineData("t1,A1,2026-05-04T09:00:00Z,-10,USD,in,cash,,,", "amount must")]
    [InlineData("t1,A1,2026-05-04T09:00:00Z,0,USD,in,cash,,,", "amount must")]
    [InlineData("t1,A1,2026-05-04T09:00:00Z,\"1,000\",USD,in,cash,,,", "amount must")]
    [InlineData("t1,A1,2026-05-04T09:00:00Z,1e5,USD,in,cash,,,", "amount must")]
    [InlineData("t1,A1,2026-05-04T09:00:00Z,NaN,USD,in,cash,,,", "amount must")]
    [InlineData("t1,A1,2026-05-04T09:00:00Z,10,US$,in,cash,,,", "currency must")]
    [InlineData("t1,A1,2026-05-04T09:00:00Z,10,USD,up,cash,,,", "direction must")]
    [InlineData("t1,A1,2026-05-04T09:00:00Z,10,USD,in,crypto,,,", "channel must")]
    [InlineData("t1,A1,2026-05-04T09:00:00Z,10,USD,in,1,,,", "channel must")]
    [InlineData(",A1,2026-05-04T09:00:00Z,10,USD,in,cash,,,", "no id")]
    [InlineData("t1,A1,2026-05-04T09:00:00Z,10,USD,in", "has 6 cells")]
    [InlineData("t1,A1,2026-05-04T09:00:00Z,10,USD,in,cash,\"open,,", "isn't closed")]
    public void ABadRowIsRefused_ByLine(string row, string reason)
    {
        var e = Assert.Throws<MonitorException>(() => TransactionCsv.Parse(Header + row + "\r\n"));
        Assert.Contains(e.Problems, p => p.Contains(reason, StringComparison.Ordinal) && p.StartsWith("Line 2", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("", "empty")]
    [InlineData("id,account\n", "needs a time column")]
    [InlineData("id,account,time,amount,currency,direction,channel,id\n", "appears twice")]
    [InlineData("id,account,time,amount,currency,direction,channel,secret\n", "isn't a column")]
    public void ABadHeaderIsRefused(string csv, string reason) =>
        Assert.Contains(reason, Assert.Throws<MonitorException>(() => TransactionCsv.Parse(csv)).Message, StringComparison.Ordinal);

    [Fact]
    public void HostileFilesAreBounded()
    {
        Assert.Contains("longer than", Assert.Throws<MonitorException>(() => TransactionCsv.Parse(Header + new string('x', 5000))).Message, StringComparison.Ordinal);
        Assert.Contains("more than 50 cells", Assert.Throws<MonitorException>(() => TransactionCsv.Parse(new string(',', 100))).Message, StringComparison.Ordinal);
        Assert.Contains("larger than", Assert.Throws<MonitorException>(() => TransactionCsv.Read(new EndlessStream())).Message, StringComparison.Ordinal);
        Assert.Contains("UTF-8", Assert.Throws<MonitorException>(() => TransactionCsv.Read(new MemoryStream([0x69, 0x64, 0xFF, 0xFE]))).Message, StringComparison.Ordinal);
        // a thousand bad rows stop at twenty problems
        var bad = Header + string.Concat(Enumerable.Repeat("x,x,x,x,x,x,x,,,\r\n", 1000));
        var e = Assert.Throws<MonitorException>(() => TransactionCsv.Parse(bad));
        Assert.True(e.Problems.Count <= 21);
    }

    [Fact]
    public void ABomIsSkipped() =>
        Assert.Single(TransactionCsv.Read(new MemoryStream([.. "\uFEFF"u8, .. Encoding.UTF8.GetBytes(Header + "t1,A1,2026-05-04T09:00:00Z,10,USD,in,cash,,,\r\n")])));

    [Theory]
    [InlineData("=HYPERLINK(\"http://x\")", "\"'=HYPERLINK(\"\"http://x\"\")\"")]
    [InlineData("+1+2", "'+1+2")]
    [InlineData("@SUM(A1)", "'@SUM(A1)")]
    [InlineData("-2+3", "'-2+3")]
    [InlineData("-12.50", "-12.50")]
    [InlineData("plain", "plain")]
    [InlineData("a,b", "\"a,b\"")]
    [InlineData(" padded", "\" padded\"")]
    public void CellsCantRunInASpreadsheet(string value, string cell) => Assert.Equal(cell, TransactionCsv.Cell(value));

    [Fact]
    public void AlertCsvIsSafe()
    {
        var w = new StringWriter();
        AlertCsv.Write([new Alert("r", "large", "=cmd|' /C calc'!A0", DateTimeOffset.UnixEpoch, Severity.High, "line\nbreak\u202Eflip", ["t1", "t2"])], w);
        var text = w.ToString();
        Assert.Contains(",'=cmd|' /C calc'!A0,", text, StringComparison.Ordinal);
        Assert.Contains("line breakflip", text.Replace("\"", "", StringComparison.Ordinal), StringComparison.Ordinal);
        Assert.DoesNotContain('\u202E', text);
        Assert.Equal(2, text.Split("\r\n").Length - 1);
    }

    private sealed class EndlessStream : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => 0; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) { buffer.AsSpan(offset, count).Fill((byte)'a'); return count; }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}

public sealed class TransactionCheckTests
{
    private static readonly Engine E = new(RuleSet.Parse("""{ "rules": [ { "id": "a", "type": "large", "amount": 1 } ] }"""));
    private static Transaction Ok => new("t1", "A1", DateTimeOffset.UnixEpoch, 10m, "USD", Direction.In, Channel.Cash);

    [Fact]
    public void BadTransactionsAreRefused_SayingWhy()
    {
        string Why(params Transaction[] txs) => Assert.Throws<MonitorException>(() => E.Run(txs)).Message;
        Assert.Contains("no rate for the currency \"KHR\"", Why(Ok with { Currency = "KHR" }), StringComparison.Ordinal);
        Assert.Contains("appears twice", Why(Ok, Ok), StringComparison.Ordinal);
        Assert.Contains("amount must be above 0", Why(Ok with { Amount = -5 }), StringComparison.Ordinal);
        Assert.Contains("amount must be above 0", Why(Ok with { Amount = decimal.MaxValue }), StringComparison.Ordinal);
        Assert.Contains("needs an id", Why(Ok with { Id = "" }), StringComparison.Ordinal);
        Assert.Contains("needs an id", Why(Ok with { Id = "t\u0000" }), StringComparison.Ordinal);
        Assert.Contains("needs an id", Why(Ok with { Id = new string('x', 65) }), StringComparison.Ordinal);
        Assert.Contains("needs an account", Why(Ok with { Account = "A\u202E1" }), StringComparison.Ordinal);
        Assert.Contains("needs an account", Why(Ok with { Account = "   " }), StringComparison.Ordinal);
        Assert.Contains("needs an account", Why(Ok with { Account = "A\uD800" }), StringComparison.Ordinal);
        Assert.Equal("ok \U0001F600", Text.Safe("ok \uDC00\U0001F600\uD800"));
        Assert.Contains("unknown direction", Why(Ok with { Direction = (Direction)9 }), StringComparison.Ordinal);
        Assert.Contains("two-letter code", Why(Ok with { CounterpartyCountry = "Cambodia" }), StringComparison.Ordinal);
        Assert.Contains("is missing", Why([null!]), StringComparison.Ordinal);
        var many = Assert.Throws<MonitorException>(() => E.Run(Enumerable.Range(0, 100).Select(i => Ok with { Id = $"t{i}", Currency = "XXX" })));
        Assert.Equal(21, many.Problems.Count);
    }
}

public sealed class CliTests
{
    private static readonly string Dir = Directory.CreateTempSubdirectory("txmonitor-test").FullName;

    private static (int Code, string Out, string Err) Cli(params string[] args)
    {
        var o = new StringWriter();
        var e = new StringWriter();
        return (TxMonitor.Cli.Cli.Execute(args, o, e), o.ToString(), e.ToString());
    }

    private static string File(string name, string text)
    {
        var p = Path.Combine(Dir, name);
        System.IO.File.WriteAllText(p, text);
        return p;
    }

    [Fact]
    public void RunFindsTheSamplesPatterns_InEveryFormat()
    {
        var rules = Path.Combine(Dir, "rules.json");
        var txs = Path.Combine(Dir, "tx.csv");
        Assert.Equal(0, Cli("example-rules", "--out", rules).Code);
        Assert.Equal(0, Cli("sample", "--out", txs).Code);
        var text = Cli("run", "--rules", rules, txs);
        Assert.Equal(0, text.Code);
        Assert.Contains("[structuring]", text.Out, StringComparison.Ordinal);
        Assert.Contains("11 alerts (6 high, 3 medium, 2 low)", text.Err, StringComparison.Ordinal);
        var high = Cli("run", "--rules", rules, txs, "--min-severity", "high", "--format", "json");
        Assert.Equal(6, System.Text.Json.JsonDocument.Parse(high.Out).RootElement.GetArrayLength());
        var csv = Cli("run", "--rules", rules, txs, "--format", "csv");
        Assert.StartsWith("time,account,rule,type,severity,explanation,transactions\r\n", csv.Out, StringComparison.Ordinal);
        Assert.Equal(0, Cli("check-rules", rules).Code);
        Assert.Contains("structuring:", Cli("rules").Out, StringComparison.Ordinal);
    }

    [Fact]
    public void MistakesExitWith2_AndSayWhy()
    {
        var rules = File("r.json", RuleSet.Example);
        var bad = File("bad.json", """{ "rules": [ { "id": "a", "type": "large" } ] }""");
        var txs = File("t.csv", "id,account,time,amount,currency,direction,channel\nt1,A1,2026-05-04T09:00:00Z,10,XXX,in,cash\n");
        Assert.Equal(2, Cli("dance").Code);
        Assert.Equal(2, Cli("run", "--rules", rules).Code);
        Assert.Equal(2, Cli("run", Path.Combine(Dir, "t.csv")).Code);
        Assert.Equal(2, Cli("run", "--rules", rules, "--rules", rules, txs).Code);
        Assert.Equal(2, Cli("run", "--rules", rules, "--evil", "1", txs).Code);
        Assert.Equal(2, Cli("run", "--rules", rules, "--format", "xml", txs).Code);
        Assert.Equal(2, Cli("run", "--rules", rules, txs, "--out", txs).Code);       // won't overwrite its input
        Assert.Contains("needs amount", Cli("check-rules", bad).Err, StringComparison.Ordinal);
        Assert.Contains("no rate for the currency \"XXX\"", Cli("run", "--rules", rules, txs).Err, StringComparison.Ordinal);
        Assert.Contains("There's no", Cli("check-rules", Path.Combine(Dir, "missing.json")).Err, StringComparison.Ordinal);
        Assert.Equal(2, Cli("sample", "--seed", "-1").Code);
        Assert.Equal(2, Cli("rules", "extra").Code);
        var big = File("big.json", new string(' ', RuleSet.MaxBytes + 10));
        Assert.Contains("larger than", Cli("check-rules", big).Err, StringComparison.Ordinal);
        var binary = Path.Combine(Dir, "bin.json");
        System.IO.File.WriteAllBytes(binary, [0x7B, 0xFF, 0xFE, 0x7D]);
        Assert.Contains("isn't UTF-8", Cli("check-rules", binary).Err, StringComparison.Ordinal);
    }

    [Fact]
    public void HelpIsShown() => Assert.Contains("txmonitor run --rules", Cli("--help").Out, StringComparison.Ordinal);
}
