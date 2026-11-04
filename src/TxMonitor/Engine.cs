using System.Globalization;

namespace TxMonitor;

/// <summary>
/// Runs a rule set over transactions and returns the alerts. Transactions are taken in time order, account by account;
/// each rule keeps only what its window needs, so a run is one pass whatever the rules.
/// </summary>
public sealed class Engine
{
    private readonly RuleSet rules;

    /// <summary>A run over these rules.</summary>
    public Engine(RuleSet rules) => this.rules = rules ?? throw new ArgumentNullException(nameof(rules));

    /// <summary>The most alerts a run raises; past this the rules are too loose to be useful, and the run stops.</summary>
    public const int MaxAlerts = 100_000;

    /// <summary>The most transaction ids an alert lists; its explanation still gives the full count.</summary>
    public const int MaxIdsPerAlert = 100;

    /// <summary>Checks the transactions and runs every rule over them. Throws <see cref="MonitorException"/> if a transaction can't be used.</summary>
    public IReadOnlyList<Alert> Run(IEnumerable<Transaction> transactions)
    {
        ArgumentNullException.ThrowIfNull(transactions);
        var list = Check(transactions);
        // One account at a time, in time order: only that account's memory is held, however many accounts there are.
        list.Sort((x, y) => string.CompareOrdinal(x.Tx.Account, y.Tx.Account) is var c && c != 0 ? c
            : x.Tx.Time.CompareTo(y.Tx.Time) is var d && d != 0 ? d : string.CompareOrdinal(x.Tx.Id, y.Tx.Id));
        var alerts = new List<(Alert Alert, int Rule)>();
        AccountState? a = null;
        foreach (var t in list)
        {
            if (a is null) a = new AccountState(t.Tx.Account, rules.Rules.Count);
            else if (a.Account != t.Tx.Account) a.Reset(t.Tx.Account);
            for (var i = 0; i < rules.Rules.Count; i++)
            {
                var rule = rules.Rules[i];
                if (!Applies(rule, t.Tx)) continue;
                if ((a.Evaluators[i] ??= Evaluator.For(rule, rules)).Next(t, a) is { } found)
                {
                    if (alerts.Count == MaxAlerts)
                        throw new MonitorException([string.Create(CultureInfo.InvariantCulture, $"The rules raised more than {MaxAlerts:N0} alerts, too many to review: they're too loose for this data. Raise their thresholds and run again.")]);
                    alerts.Add((new Alert(rule.Id, rule.Type, t.Tx.Account, t.Tx.Time, rule.Severity, found.Explanation, found.Ids), i));
                }
            }
            a.Count++;
            a.Last = t.Tx.Time;
            if (t.Tx.Direction == Direction.Out && t.Counterparty is { } cp) a.Paid.Add(cp);
        }
        return alerts.OrderBy(x => x.Alert.Time).ThenBy(x => x.Alert.Account, StringComparer.Ordinal).ThenBy(x => x.Rule).Select(x => x.Alert).ToList();
    }

    private static bool Applies(RuleConfig rule, Transaction tx) =>
        (rule.Channels is null || rule.Channels.Contains(tx.Channel)) && (rule.Direction is null || rule.Direction == tx.Direction);

    private const int MaxProblems = 20;

    private List<Item> Check(IEnumerable<Transaction> transactions)
    {
        var problems = new List<string>();
        var ids = new HashSet<string>(StringComparer.Ordinal);
        var list = new List<Item>();
        var n = 0;
        void Problem(string s) { if (problems.Count < MaxProblems) problems.Add(s); }
        foreach (var tx in transactions)
        {
            n++;
            if (tx is null) { Problem($"Transaction {n} is missing."); continue; }
            var name = $"Transaction {n}";
            if (!IsKey(tx.Id)) { Problem($"{name} needs an id of 1 to 64 visible characters."); continue; }
            name = $"Transaction {Text.Safe(tx.Id)}";
            var ok = true;
            if (!ids.Add(tx.Id)) { Problem($"{name} appears twice."); ok = false; }
            if (!IsKey(tx.Account)) { Problem($"{name} needs an account of 1 to 64 visible characters."); ok = false; }
            if (tx.Amount <= 0 || tx.Amount > MaxAmount) { Problem(string.Create(CultureInfo.InvariantCulture, $"{name}: the amount must be above 0 and at most {MaxAmount:N0}.")); ok = false; }
            if (!Enum.IsDefined(tx.Direction) || !Enum.IsDefined(tx.Channel)) { Problem($"{name} has an unknown direction or channel."); ok = false; }
            if (tx.Currency is null || !rules.Rates.TryGetValue(tx.Currency, out var rate))
            {
                Problem($"{name}: there's no rate for the currency \"{Text.Safe(tx.Currency ?? "")}\"; add it to the rule set's rates.");
                continue;
            }
            string? country = null;
            if (!string.IsNullOrWhiteSpace(tx.CounterpartyCountry))
            {
                country = tx.CounterpartyCountry.Trim().ToUpperInvariant();
                if (country.Length != 2 || !country.All(char.IsAsciiLetterUpper)) { Problem($"{name}: the counterparty country must be a two-letter code such as KH."); ok = false; }
            }
            var value = ok ? decimal.Round(tx.Amount * rate, 4) : 0;
            if (value > MaxBase) { Problem(string.Create(CultureInfo.InvariantCulture, $"{name} is worth more than {MaxBase:N0} {rules.BaseCurrency}.")); ok = false; }
            if (ok) list.Add(new Item(tx, value, Counterparty(tx.Counterparty), country));
        }
        if (problems.Count > 0)
        {
            if (problems.Count == MaxProblems) problems.Add("Further problems weren't listed.");
            throw new MonitorException(problems);
        }
        return list;
    }

    /// <summary>The largest value accepted in the base currency, so that sums of a million transactions can't overflow.</summary>
    public const decimal MaxBase = 100_000_000_000_000_000_000m;

    /// <summary>The largest amount accepted, in any currency.</summary>
    public const decimal MaxAmount = 1_000_000_000_000_000m;

    private static bool IsKey(string? s) => s is { Length: > 0 and <= 64 } && !string.IsNullOrWhiteSpace(s) && !s.Any(c => char.IsControl(c) || Text.IsInvisible(c)) && !Text.HasBrokenSurrogate(s);

    /// <summary>Counterparty names compared as people would: case, spacing and invisible characters don't make a new payee.</summary>
    internal static string? Counterparty(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        var s = string.Join(' ', Text.Safe(name, 200).ToUpperInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries));
        return s.Length == 0 ? null : s;
    }

    internal sealed record Item(Transaction Tx, decimal Base, string? Counterparty, string? Country);

    /// <summary>What's known about the account being run; reused for the next account, so a run allocates per rule, not per account.</summary>
    internal sealed class AccountState(string account, int rules)
    {
        public string Account { get; private set; } = account;
        public Evaluator?[] Evaluators { get; } = new Evaluator?[rules];
        public int Count;
        public DateTimeOffset? Last;
        public HashSet<string> Paid { get; } = new(StringComparer.Ordinal);

        public void Reset(string account)
        {
            Account = account;
            Count = 0;
            Last = null;
            Paid.Clear();
            foreach (var e in Evaluators) e?.Reset();
        }
    }

    /// <summary>What a rule found. The explanation is formatted the same whatever the machine's culture.</summary>
    internal sealed class Found(FormattableString explanation, IReadOnlyList<string> ids)
    {
        public string Explanation { get; } = explanation.ToString(CultureInfo.InvariantCulture);
        public IReadOnlyList<string> Ids { get; } = ids;
    }

    /// <summary>One rule's memory for the account being run. Its settings are read once, when it's made.</summary>
    internal abstract class Evaluator(RuleConfig rule, RuleSet set)
    {
        protected RuleConfig Rule { get; } = rule;
        protected string Cur { get; } = set.BaseCurrency;

        public abstract Found? Next(Item t, AccountState a);

        /// <summary>Forgets everything, for the next account.</summary>
        public virtual void Reset() { }

        public static Evaluator For(RuleConfig rule, RuleSet set) => rule.Type switch
        {
            "large" => new Large(rule, set),
            "high-risk-country" => new HighRisk(rule, set),
            "dormant" => new Dormant(rule, set),
            "new-counterparty" => new NewCounterparty(rule, set),
            "structuring" => new Structuring(rule, set),
            "velocity" => new Velocity(rule, set),
            "pass-through" => new PassThrough(rule, set),
            "round-amounts" => new RoundAmounts(rule, set),
            "fan-in" or "fan-out" => new Fan(rule, set),
            _ => throw new InvalidOperationException("Unknown rule type."),
        };

        protected decimal N(string name, decimal fallback) => Rule.Numbers.TryGetValue(name, out var v) ? v : fallback;

        protected static IReadOnlyList<string> Ids(IEnumerable<Item> txs) => txs.Take(MaxIdsPerAlert).Select(t => t.Tx.Id).ToArray();

        protected string Amount(Item t) => Money(t.Base, Cur) + Original(t, Cur);
    }

    private sealed class Large(RuleConfig rule, RuleSet set) : Evaluator(rule, set)
    {
        private readonly decimal limit = rule.Numbers["amount"];

        public override Found? Next(Item t, AccountState a) => t.Base >= limit
            ? new($"{What(t.Tx, Amount(t))}, at or above the {Money(limit, Cur)} limit{Via(Rule, "for")}.", [t.Tx.Id])
            : null;
    }

    private sealed class HighRisk(RuleConfig rule, RuleSet set) : Evaluator(rule, set)
    {
        private readonly decimal min = rule.Numbers.GetValueOrDefault("minimum", 0);
        private readonly IReadOnlySet<string> countries = set.HighRiskCountries;

        public override Found? Next(Item t, AccountState a) => t.Country is { } c && t.Base >= min && countries.Contains(c)
            ? new($"{What(t.Tx, Amount(t))} {(t.Tx.Direction == Direction.In ? "from" : "to")} {Who(t)} in {c}, a high-risk country in the rule set.", [t.Tx.Id])
            : null;
    }

    private sealed class Dormant(RuleConfig rule, RuleSet set) : Evaluator(rule, set)
    {
        private readonly decimal days = rule.Numbers["days"];
        private readonly decimal min = rule.Numbers.GetValueOrDefault("minimum", 0);

        public override Found? Next(Item t, AccountState a)
        {
            if (a.Last is not { } last || t.Base < min) return null;
            var idle = t.Tx.Time - last;
            return idle.TotalDays >= (double)days
                ? new($"{What(t.Tx, Amount(t))} after {Math.Floor(idle.TotalDays):N0} days with no activity (the rule looks for {days:0} or more); the last was on {last:yyyy-MM-dd}.", [t.Tx.Id])
                : null;
        }
    }

    private sealed class NewCounterparty(RuleConfig rule, RuleSet set) : Evaluator(rule, set)
    {
        private readonly decimal min = rule.Numbers["minimum"];

        public override Found? Next(Item t, AccountState a) =>
            t.Tx.Direction == Direction.Out && t.Base >= min && t.Counterparty is { } cp && a.Count > 0 && !a.Paid.Contains(cp)
                ? new($"A payment of {Amount(t)} to {Who(t)}, whom the account hasn't paid before in this data, at or above {Money(min, Cur)}.", [t.Tx.Id])
                : null;
    }

    /// <summary>Several deposits each just under a threshold that together reach it.</summary>
    private sealed class Structuring(RuleConfig rule, RuleSet set) : Evaluator(rule, set)
    {
        private readonly Queue<Item> window = new();
        private decimal sum;
        private readonly decimal threshold = rule.Numbers["threshold"];
        private readonly decimal near = rule.Numbers.GetValueOrDefault("near", 0.8m);
        private readonly decimal count = rule.Numbers.GetValueOrDefault("count", 3);
        private readonly double days = (double)rule.Numbers.GetValueOrDefault("days", 7);

        public override void Reset() { window.Clear(); sum = 0; }

        public override Found? Next(Item t, AccountState a)
        {
            if (t.Tx.Direction != Direction.In || t.Base < near * threshold || t.Base >= threshold) return null;
            while (window.Count > 0 && (t.Tx.Time - window.Peek().Tx.Time).TotalDays > days) sum -= window.Dequeue().Base;
            window.Enqueue(t);
            sum += t.Base;
            if (window.Count < count || sum < threshold) return null;
            var span = (t.Tx.Time - window.Peek().Tx.Time).TotalDays;
            var found = new Found(
                $"{window.Count} deposits{Via(Rule)} within {Days(span)}, each between {Money(near * threshold, Cur)} and the {Money(threshold, Cur)} threshold, together {Money(sum, Cur)}: amounts kept just under a reporting threshold.", Ids(window));
            Reset();
            return found;
        }
    }

    /// <summary>Many transactions in a short time.</summary>
    private sealed class Velocity(RuleConfig rule, RuleSet set) : Evaluator(rule, set)
    {
        private readonly Queue<Item> window = new();
        private readonly decimal count = rule.Numbers["count"];
        private readonly double hours = (double)rule.Numbers["hours"];

        public override void Reset() => window.Clear();

        public override Found? Next(Item t, AccountState a)
        {
            while (window.Count > 0 && (t.Tx.Time - window.Peek().Tx.Time).TotalHours > hours) window.Dequeue();
            window.Enqueue(t);
            if (window.Count < count) return null;
            var found = new Found(
                $"{window.Count} transactions{Via(Rule)} within {Hours((t.Tx.Time - window.Peek().Tx.Time).TotalHours)} (the rule looks for {count:0} within {hours:0} hours), together {Money(window.Sum(x => x.Base), Cur)}.",
                Ids(window));
            Reset();
            return found;
        }
    }

    /// <summary>Money in, then most of it out again soon after.</summary>
    private sealed class PassThrough(RuleConfig rule, RuleSet set) : Evaluator(rule, set)
    {
        // The window always starts with money in: money out before any money in can't be passing it on.
        private readonly LinkedList<Item> window = new();
        private decimal inSum, outSum;
        private readonly double hours = (double)rule.Numbers.GetValueOrDefault("hours", 48);
        private readonly decimal minimum = rule.Numbers["minimum"];
        private readonly decimal share = rule.Numbers.GetValueOrDefault("share", 0.8m);

        public override void Reset() { window.Clear(); inSum = outSum = 0; }

        public override Found? Next(Item t, AccountState a)
        {
            while (window.First is { } first && (t.Tx.Time - first.Value.Tx.Time).TotalHours > hours)
            {
                Remove(first);
                while (window.First is { } f && f.Value.Tx.Direction == Direction.Out) Remove(f);
            }
            if (t.Tx.Direction == Direction.In) { window.AddLast(t); inSum += t.Base; return null; }
            if (window.Count == 0) return null;
            window.AddLast(t);
            outSum += t.Base;
            if (inSum < minimum || outSum < share * inSum) return null;
            var ins = window.Count(x => x.Tx.Direction == Direction.In);
            var span = (t.Tx.Time - window.First!.Value.Tx.Time).TotalHours;
            var found = new Found(
                $"{Money(inSum, Cur)} came in ({Plural(ins, "transaction")}) and {Money(outSum, Cur)} ({outSum / inSum * 100:0}%) went out again within {Hours(span)}: money passed straight through the account (the rule looks for {share * 100:0}% or more of at least {Money(minimum, Cur)} within {hours:0} hours).",
                Ids(window));
            Reset();
            return found;
        }

        private void Remove(LinkedListNode<Item> node)
        {
            if (node.Value.Tx.Direction == Direction.In) inSum -= node.Value.Base; else outSum -= node.Value.Base;
            window.Remove(node);
        }
    }

    /// <summary>Repeated round amounts.</summary>
    private sealed class RoundAmounts(RuleConfig rule, RuleSet set) : Evaluator(rule, set)
    {
        private readonly Queue<Item> window = new();
        private readonly decimal multiple = rule.Numbers.GetValueOrDefault("multiple", 1000);
        private readonly decimal min = rule.Numbers.GetValueOrDefault("minimum", rule.Numbers.GetValueOrDefault("multiple", 1000));
        private readonly decimal count = rule.Numbers.GetValueOrDefault("count", 3);
        private readonly double days = (double)rule.Numbers.GetValueOrDefault("days", 30);

        public override void Reset() => window.Clear();

        public override Found? Next(Item t, AccountState a)
        {
            if (t.Base < min || t.Tx.Amount % multiple != 0) return null;
            while (window.Count > 0 && (t.Tx.Time - window.Peek().Tx.Time).TotalDays > days) window.Dequeue();
            window.Enqueue(t);
            if (window.Count < count) return null;
            var found = new Found(
                $"{window.Count} transactions{Via(Rule)} of exact multiples of {multiple:N0} within {Days((t.Tx.Time - window.Peek().Tx.Time).TotalDays)}, such as {string.Join(", ", window.Take(3).Select(x => x.Tx.Amount.ToString("N0", Inv) + " " + x.Tx.Currency))}.",
                Ids(window));
            Reset();
            return found;
        }
    }

    /// <summary>Money from many senders, or to many recipients.</summary>
    private sealed class Fan(RuleConfig rule, RuleSet set) : Evaluator(rule, set)
    {
        private readonly Queue<Item> window = new();
        private readonly Dictionary<string, int> counts = new(StringComparer.Ordinal);
        private readonly bool incoming = rule.Type == "fan-in";
        private readonly decimal counterparties = rule.Numbers["counterparties"];
        private readonly double days = (double)rule.Numbers.GetValueOrDefault("days", 7);

        public override void Reset() { window.Clear(); counts.Clear(); }

        public override Found? Next(Item t, AccountState a)
        {
            if (t.Tx.Direction != (incoming ? Direction.In : Direction.Out) || t.Counterparty is not { } cp) return null;
            while (window.Count > 0 && (t.Tx.Time - window.Peek().Tx.Time).TotalDays > days)
            {
                var old = window.Dequeue().Counterparty!;
                if (--counts[old] == 0) counts.Remove(old);
            }
            window.Enqueue(t);
            counts[cp] = counts.GetValueOrDefault(cp) + 1;
            if (counts.Count < counterparties) return null;
            var found = new Found(
                $"{Money(window.Sum(x => x.Base), Cur)} {(incoming ? "from" : "to")} {counts.Count} different {(incoming ? "senders" : "recipients")} in {Plural(window.Count, "transaction")}{Via(Rule)} within {Days((t.Tx.Time - window.Peek().Tx.Time).TotalDays)} (the rule looks for {counterparties:0} within {days:0} days).",
                Ids(window));
            Reset();
            return found;
        }
    }

    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    internal static string Money(decimal amount, string currency) => amount.ToString("N2", Inv) + " " + currency;

    private static string Original(Item t, string baseCurrency) =>
        t.Tx.Currency == baseCurrency ? "" : $" ({t.Tx.Amount.ToString("N2", Inv)} {t.Tx.Currency})";

    private static string What(Transaction tx, string amount) => tx.Direction == Direction.In
        ? tx.Channel == Channel.Cash ? $"A cash deposit of {amount}" : $"{amount} received"
        : tx.Channel == Channel.Cash ? $"A cash withdrawal of {amount}" : $"A payment of {amount}";

    private static string Via(RuleConfig rule, string word = "by") => rule.Channels is { Count: > 0 } c && c.Count < Enum.GetValues<Channel>().Length
        ? $" {word} " + string.Join(" or ", c.Order().Select(x => x.ToString().ToLowerInvariant()))
        : "";

    private static string Who(Item t) => t.Tx.Counterparty is { } name && Text.Safe(name, 60) is { Length: > 0 } s ? $"\"{s}\"" : "a counterparty";

    private static string Plural(int n, string word) => n == 1 ? $"1 {word}" : $"{n.ToString("N0", Inv)} {word}s";

    private static string Days(double days) => days < 1 ? Hours(days * 24) : days < 1.5 ? "1 day" : $"{Math.Round(days).ToString(Inv)} days";

    private static string Hours(double hours) => hours < 1 ? "under an hour" : hours < 1.5 ? "1 hour" : $"{Math.Round(hours).ToString(Inv)} hours";
}

/// <summary>Text from transactions made safe to show: no control or invisible characters, and a length limit.</summary>
public static class Text
{
    /// <summary>Drops control, format and separator characters (which can hide or reorder text) and cuts to a length.</summary>
    public static string Safe(string s, int max = 64)
    {
        ArgumentNullException.ThrowIfNull(s);
        var b = new System.Text.StringBuilder(Math.Min(s.Length, max));
        for (var i = 0; i < s.Length && b.Length < max; i++)
        {
            var c = s[i];
            if (char.IsWhiteSpace(c)) { if (b.Length > 0 && b[^1] != ' ') b.Append(' '); continue; }
            if (char.IsControl(c) || IsInvisible(c)) continue;
            if (char.IsSurrogate(c))
            {
                // a pair is one character; half of one isn't text at all
                if (char.IsHighSurrogate(c) && i + 1 < s.Length && char.IsLowSurrogate(s[i + 1]) && b.Length + 2 <= max) b.Append(c).Append(s[++i]);
                continue;
            }
            b.Append(c);
        }
        return b.ToString().Trim();
    }

    /// <summary>Whether text holds half of a surrogate pair, which isn't valid Unicode.</summary>
    public static bool HasBrokenSurrogate(string s)
    {
        ArgumentNullException.ThrowIfNull(s);
        for (var i = 0; i < s.Length; i++)
        {
            if (!char.IsSurrogate(s[i])) continue;
            if (char.IsHighSurrogate(s[i]) && i + 1 < s.Length && char.IsLowSurrogate(s[i + 1])) { i++; continue; }
            return true;
        }
        return false;
    }

    /// <summary>Format characters (zero-width, bidirectional controls) and line or paragraph separators.</summary>
    public static bool IsInvisible(char c) =>
        char.GetUnicodeCategory(c) is UnicodeCategory.Format or UnicodeCategory.LineSeparator or UnicodeCategory.ParagraphSeparator;
}
