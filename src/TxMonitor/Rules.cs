using System.Globalization;
using System.Text.Json;

namespace TxMonitor;

/// <summary>One configured rule: its type and its settings, already checked.</summary>
public sealed record RuleConfig(string Id, string Type, Severity Severity, IReadOnlyDictionary<string, decimal> Numbers, IReadOnlySet<Channel>? Channels, Direction? Direction);

/// <summary>
/// A rule set, read from JSON. Rules are data, never code: each has a type from a fixed list and numeric settings
/// with checked ranges, so a configuration can't make the engine do anything but count and compare.
/// </summary>
public sealed class RuleSet
{
    /// <summary>The currency rule amounts are in.</summary>
    public string BaseCurrency { get; }
    /// <summary>How many units of the base currency one unit of each other currency is worth.</summary>
    public IReadOnlyDictionary<string, decimal> Rates { get; }
    /// <summary>Countries (ISO 3166 alpha-2) whose counterparties the high-risk-country rule flags.</summary>
    public IReadOnlySet<string> HighRiskCountries { get; }
    /// <summary>The rules, in order.</summary>
    public IReadOnlyList<RuleConfig> Rules { get; }

    private RuleSet(string baseCurrency, IReadOnlyDictionary<string, decimal> rates, IReadOnlySet<string> countries, IReadOnlyList<RuleConfig> rules)
    {
        BaseCurrency = baseCurrency;
        Rates = rates;
        HighRiskCountries = countries;
        Rules = rules;
    }

    /// <summary>Each rule type, what it looks for, and its settings: name → (required, minimum, maximum, meaning).</summary>
    public static readonly IReadOnlyDictionary<string, (string Description, IReadOnlyDictionary<string, (bool Required, decimal Min, decimal Max, string Meaning)> Settings)> Types =
        new Dictionary<string, (string, IReadOnlyDictionary<string, (bool, decimal, decimal, string)>)>
        {
            ["large"] = ("One transaction at or above an amount.", S(("amount", true, 1, 1e12m, "the amount, in the base currency"))),
            ["structuring"] = ("Several cash deposits just under a reporting threshold, together over it.", S(
                ("threshold", true, 1, 1e12m, "the reporting threshold"), ("near", false, 0.5m, 0.999m, "how close counts as just under (default 0.8: 80% to 100% of it)"),
                ("count", false, 2, 100, "how many deposits (default 3)"), ("days", false, 1, 90, "within how many days (default 7)"))),
            ["velocity"] = ("Many transactions in a short time.", S(("count", true, 2, 100000, "how many transactions"), ("hours", true, 1, 24 * 90, "within how many hours"))),
            ["pass-through"] = ("Money received and sent on quickly: a mule or layering pattern.", S(
                ("minimum", true, 1, 1e12m, "how much must come in"), ("share", false, 0.1m, 1, "what share of it must go out (default 0.8)"), ("hours", false, 1, 24 * 30, "within how many hours (default 48)"))),
            ["high-risk-country"] = ("A counterparty in a listed high-risk country.", S(("minimum", false, 0, 1e12m, "from what amount (default 0)"))),
            ["round-amounts"] = ("Repeated round amounts.", S(
                ("multiple", false, 1, 1e9m, "a multiple of what, in the transaction's own currency (default 1000)"), ("count", false, 2, 1000, "how many (default 3)"), ("days", false, 1, 365, "within how many days (default 30)"), ("minimum", false, 0, 1e12m, "from what amount in the base currency (default the multiple)"))),
            ["dormant"] = ("An account inactive for a long time that suddenly moves money.", S(("days", true, 30, 3650, "inactive for how many days"), ("minimum", false, 0, 1e12m, "from what amount (default 0)"))),
            ["fan-in"] = ("Money from many different senders.", S(("counterparties", true, 2, 100000, "how many different senders"), ("days", false, 1, 365, "within how many days (default 7)"))),
            ["fan-out"] = ("Money to many different recipients.", S(("counterparties", true, 2, 100000, "how many different recipients"), ("days", false, 1, 365, "within how many days (default 7)"))),
            ["new-counterparty"] = ("A large payment to someone the account has never paid before.", S(("minimum", true, 1, 1e12m, "from what amount"))),
        };

    private static IReadOnlyDictionary<string, (bool, decimal, decimal, string)> S(params (string Name, bool Required, decimal Min, decimal Max, string Meaning)[] s) =>
        s.ToDictionary(x => x.Name, x => (x.Required, x.Min, x.Max, x.Meaning), StringComparer.Ordinal);

    /// <summary>The longest rule-set file read.</summary>
    public const int MaxBytes = 1024 * 1024;

    /// <summary>Reads and checks a rule set. Throws <see cref="MonitorException"/> listing every problem.</summary>
    public static RuleSet Parse(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        if (json.Length > MaxBytes) throw new MonitorException([$"The rule set is longer than {MaxBytes / 1024} KB."]);
        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 8, CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
        }
        catch (JsonException e)
        {
            throw new MonitorException([$"The rule set isn't valid JSON: {e.Message}"]);
        }
        using (doc)
        {
            var p = new List<string>();
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) throw new MonitorException(["The rule set must be a JSON object."]);
            foreach (var prop in root.EnumerateObject())
                if (prop.Name is not ("baseCurrency" or "rates" or "highRiskCountries" or "rules"))
                    p.Add($"Unknown setting \"{Clean(prop.Name)}\" (the rule set has baseCurrency, rates, highRiskCountries and rules).");

            var baseCurrency = root.TryGetProperty("baseCurrency", out var bc) && bc.ValueKind == JsonValueKind.String ? bc.GetString()! : "USD";
            if (!IsCurrency(baseCurrency)) p.Add("baseCurrency must be a three-letter ISO 4217 code such as USD.");

            var rates = new Dictionary<string, decimal>(StringComparer.Ordinal) { [baseCurrency] = 1 };
            if (root.TryGetProperty("rates", out var r))
            {
                if (r.ValueKind != JsonValueKind.Object) p.Add("rates must be an object such as { \"KHR\": 0.000245 }.");
                else foreach (var c in r.EnumerateObject())
                {
                    if (!IsCurrency(c.Name)) { p.Add($"rates: \"{Clean(c.Name)}\" isn't a currency code."); continue; }
                    if (c.Name == baseCurrency) { p.Add($"rates: {c.Name} is the base currency, so its rate is 1."); continue; }
                    if (c.Value.ValueKind != JsonValueKind.Number || !c.Value.TryGetDecimal(out var v) || v < 0.000000001m || v > 1e9m) { p.Add($"rates: {c.Name} must be a number above 0 (from 0.000000001 to 1000000000)."); continue; }
                    if (rates.Count > 300) { p.Add("rates: at most 300 currencies."); break; }
                    rates[c.Name] = v;
                }
            }

            var countries = new HashSet<string>(StringComparer.Ordinal);
            if (root.TryGetProperty("highRiskCountries", out var hc))
            {
                if (hc.ValueKind != JsonValueKind.Array) p.Add("highRiskCountries must be a list of two-letter country codes.");
                else foreach (var c in hc.EnumerateArray())
                {
                    var code = c.ValueKind == JsonValueKind.String ? c.GetString()! : "";
                    if (code.Length == 2 && code.All(char.IsAsciiLetterUpper)) countries.Add(code);
                    else p.Add($"highRiskCountries: \"{Clean(c.ToString())}\" isn't a two-letter country code such as KP.");
                    if (countries.Count > 300) break;
                }
            }

            var rules = new List<RuleConfig>();
            if (!root.TryGetProperty("rules", out var rs) || rs.ValueKind != JsonValueKind.Array || rs.GetArrayLength() == 0) p.Add("rules must be a non-empty list.");
            else if (rs.GetArrayLength() > 200) p.Add("A rule set has at most 200 rules.");
            else
            {
                var ids = new HashSet<string>(StringComparer.Ordinal);
                var n = 0;
                foreach (var rule in rs.EnumerateArray())
                {
                    n++;
                    if (ParseRule(rule, n, p) is { } config)
                    {
                        if (!ids.Add(config.Id)) p.Add($"Rule {n}: the id \"{config.Id}\" is used twice.");
                        if (config.Type == "high-risk-country" && countries.Count == 0) p.Add($"Rule \"{config.Id}\" needs highRiskCountries.");
                        rules.Add(config);
                    }
                }
            }
            if (p.Count > 0) throw new MonitorException(p);
            return new RuleSet(baseCurrency, rates, countries, rules);
        }
    }

    private static RuleConfig? ParseRule(JsonElement rule, int n, List<string> p)
    {
        if (rule.ValueKind != JsonValueKind.Object) { p.Add($"Rule {n} must be an object."); return null; }
        var id = rule.TryGetProperty("id", out var i) && i.ValueKind == JsonValueKind.String ? i.GetString()! : null;
        if (id is null || id.Length is 0 or > 40 || !id.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_'))
        { p.Add($"Rule {n} needs an id of 1 to 40 letters, digits, - or _."); return null; }
        var type = rule.TryGetProperty("type", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString()! : "";
        if (!Types.TryGetValue(type, out var spec)) { p.Add($"Rule \"{id}\": the type \"{Clean(type)}\" isn't one of {string.Join(", ", Types.Keys)}."); return null; }

        var severity = Severity.Medium;
        var numbers = new Dictionary<string, decimal>(StringComparer.Ordinal);
        HashSet<Channel>? channels = null;
        Direction? direction = null;
        foreach (var prop in rule.EnumerateObject())
        {
            switch (prop.Name)
            {
                case "id" or "type":
                    continue;
                case "severity":
                    if (prop.Value.ValueKind != JsonValueKind.String || !Enum.TryParse(prop.Value.GetString(), ignoreCase: true, out severity) || !Enum.IsDefined(severity) || int.TryParse(prop.Value.GetString(), out _))
                        p.Add($"Rule \"{id}\": severity is low, medium or high.");
                    continue;
                case "channels":
                    channels = [];
                    if (prop.Value.ValueKind != JsonValueKind.Array) { p.Add($"Rule \"{id}\": channels is a list such as [\"cash\"]."); continue; }
                    foreach (var c in prop.Value.EnumerateArray())
                        if (c.ValueKind == JsonValueKind.String && Enum.TryParse<Channel>(c.GetString(), ignoreCase: true, out var ch) && Enum.IsDefined(ch) && !int.TryParse(c.GetString(), out _)) channels.Add(ch);
                        else p.Add($"Rule \"{id}\": \"{Clean(c.ToString())}\" isn't a channel ({string.Join(", ", Enum.GetNames<Channel>().Select(x => x.ToLowerInvariant()))}).");
                    continue;
                case "direction":
                    if (prop.Value.ValueKind == JsonValueKind.String && prop.Value.GetString() is "in" or "out") direction = prop.Value.GetString() == "in" ? TxMonitor.Direction.In : TxMonitor.Direction.Out;
                    else p.Add($"Rule \"{id}\": direction is in or out.");
                    continue;
            }
            if (!spec.Settings.TryGetValue(prop.Name, out var s)) { p.Add($"Rule \"{id}\": a {type} rule has no setting \"{Clean(prop.Name)}\" (it has {string.Join(", ", spec.Settings.Keys)})."); continue; }
            if (prop.Value.ValueKind != JsonValueKind.Number || !prop.Value.TryGetDecimal(out var v)) { p.Add($"Rule \"{id}\": {prop.Name} must be a number."); continue; }
            if (v < s.Min || v > s.Max) { p.Add($"Rule \"{id}\": {prop.Name} must be from {s.Min.ToString(CultureInfo.InvariantCulture)} to {s.Max.ToString("0.##", CultureInfo.InvariantCulture)}."); continue; }
            numbers[prop.Name] = v;
        }
        foreach (var (name, s) in spec.Settings)
            if (s.Required && !numbers.ContainsKey(name)) p.Add($"Rule \"{id}\": a {type} rule needs {name} ({s.Meaning}).");
        if (direction is not null && type is "structuring" or "pass-through" or "fan-in" or "fan-out")
            p.Add($"Rule \"{id}\": a {type} rule sets its own direction, so it takes no direction setting.");
        if (type == "structuring" && channels is null) channels = [Channel.Cash];
        return new RuleConfig(id, type, severity, numbers, channels, direction);
    }

    private static bool IsCurrency(string s) => s.Length == 3 && s.All(char.IsAsciiLetterUpper);

    internal static string Clean(string s) => new(s.Take(40).Select(c => c is >= ' ' and <= '~' ? c : '?').ToArray());

    /// <summary>A sensible starting rule set; its thresholds are examples, not regulatory values. Tune them to the business.</summary>
    public const string Example = """
        {
          // amounts are in the base currency; other currencies are converted with these rates (made-up examples)
          "baseCurrency": "USD",
          "rates": { "KHR": 0.000245, "EUR": 1.08, "THB": 0.028 },
          // the FATF "call for action" countries, as an example: check the current FATF list
          "highRiskCountries": ["KP", "IR", "MM"],
          "rules": [
            { "id": "large-cash", "type": "large", "amount": 10000, "channels": ["cash"], "severity": "high" },
            { "id": "structuring", "type": "structuring", "threshold": 10000, "near": 0.8, "count": 3, "days": 7, "severity": "high" },
            { "id": "pass-through", "type": "pass-through", "minimum": 5000, "share": 0.8, "hours": 48, "severity": "high" },
            { "id": "velocity", "type": "velocity", "count": 25, "hours": 24 },
            { "id": "high-risk", "type": "high-risk-country", "minimum": 1000, "severity": "high" },
            { "id": "round", "type": "round-amounts", "multiple": 1000, "count": 4, "days": 30, "severity": "low" },
            { "id": "dormant", "type": "dormant", "days": 180, "minimum": 2000 },
            { "id": "fan-in", "type": "fan-in", "counterparties": 10, "days": 7 },
            { "id": "new-payee", "type": "new-counterparty", "minimum": 8000, "severity": "low" }
          ]
        }
        """;
}
