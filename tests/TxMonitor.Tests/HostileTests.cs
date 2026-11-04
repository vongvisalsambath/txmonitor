using System.Diagnostics;
using System.Text;
using Xunit;

namespace TxMonitor.Tests;

public sealed class HostileTests
{
    private static readonly DateTimeOffset T0 = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public void HugeAmountsCantOverflowTheSums()
    {
        var set = RuleSet.Parse("""{ "rates": { "XAU": 1000000000 }, "rules": [ { "id": "v", "type": "velocity", "count": 2, "hours": 1 }, { "id": "p", "type": "pass-through", "minimum": 1 } ] }""");
        var e = Assert.Throws<MonitorException>(() => new Engine(set).Run([new("t1", "A", T0, Engine.MaxAmount, "XAU", Direction.In, Channel.Wire)]));
        Assert.Contains("is worth more than", e.Message, StringComparison.Ordinal);
        // the most each may be worth, many times over, adds up without overflow
        var max = Engine.MaxBase / 1_000_000_000m;
        var txs = Enumerable.Range(0, 10_000).Select(i => new Transaction($"t{i}", "A", T0.AddSeconds(i), max, "XAU", i % 2 == 0 ? Direction.In : Direction.Out, Channel.Wire)).ToList();
        Assert.NotEmpty(new Engine(set).Run(txs));
    }

    [Fact]
    public void TheBaseCurrencysRateCantBeChanged() =>
        Assert.Contains("is the base currency", Assert.Throws<MonitorException>(() => RuleSet.Parse("""{ "rates": { "USD": 2 }, "rules": [ { "id": "a", "type": "large", "amount": 1 } ] }""")).Message, StringComparison.Ordinal);

    [Fact]
    public void ManyRulesOverManyAccounts_RunInOnePass()
    {
        // 200 rules of every windowed type over 200,000 transactions on 100,000 accounts
        // strict enough that alerts are rare: this measures the evaluation, not the writing of alerts
        var types = new[]
        {
            """{ "type": "velocity", "count": 20, "hours": 1 }""", """{ "type": "structuring", "threshold": 10000, "count": 5 }""",
            """{ "type": "pass-through", "minimum": 100000 }""", """{ "type": "round-amounts", "multiple": 1000, "count": 20 }""",
            """{ "type": "fan-in", "counterparties": 15 }""", """{ "type": "fan-out", "counterparties": 15 }""", """{ "type": "dormant", "days": 3650 }""",
            """{ "type": "new-counterparty", "minimum": 100000 }""",
        };
        var json = "{ \"rules\": [" + string.Join(",", Enumerable.Range(0, 200).Select(i => types[i % types.Length].Replace("{ ", $"{{ \"id\": \"r{i}\", ", StringComparison.Ordinal))) + "] }";
        var set = RuleSet.Parse(json);
        var rnd = new Random(1);
        var txs = Enumerable.Range(0, 200_000).Select(i => new Transaction($"t{i}", $"A{rnd.Next(100_000)}", T0.AddMinutes(rnd.Next(60 * 24 * 90)),
            rnd.Next(1, 12000), "USD", (Direction)rnd.Next(2), (Channel)rnd.Next(6), $"C{rnd.Next(20)}")).ToList();
        var sw = Stopwatch.StartNew();
        var alerts = new Engine(set).Run(txs);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(15), sw.Elapsed.ToString());
        Assert.Equal(alerts.OrderBy(a => a.Time).Select(a => a.Time), alerts.Select(a => a.Time));
    }

    [Fact]
    public void RulesTooLooseForTheData_StopWithAReason()
    {
        var set = RuleSet.Parse("""{ "rules": [ { "id": "a", "type": "large", "amount": 1 }, { "id": "b", "type": "large", "amount": 2 } ] }""");
        var txs = Enumerable.Range(0, 60_000).Select(i => new Transaction($"t{i}", $"A{i % 10}", T0.AddMinutes(i), 5, "USD", Direction.In, Channel.Cash)).ToList();
        Assert.Contains("more than 100,000 alerts", Assert.Throws<MonitorException>(() => new Engine(set).Run(txs)).Message, StringComparison.Ordinal);
        Assert.Equal(100_000, new Engine(set).Run(txs.Take(50_000)).Count);
    }

    [Fact]
    public void ManyTransactionsInOneWindow_StayLinear()
    {
        var set = RuleSet.Parse("""{ "rules": [ { "id": "v", "type": "velocity", "count": 100000, "hours": 2160 }, { "id": "f", "type": "fan-in", "counterparties": 100000, "days": 365 }, { "id": "p", "type": "pass-through", "minimum": 1e12, "hours": 720 } ] }""");
        var txs = Enumerable.Range(0, 300_000).Select(i => new Transaction($"t{i}", "A", T0.AddSeconds(i), 5, "USD", i % 3 == 0 ? Direction.Out : Direction.In, Channel.Card, $"C{i % 50_000}")).ToList();
        var sw = Stopwatch.StartNew();
        var alerts = new Engine(set).Run(txs);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(10), sw.Elapsed.ToString());
        Assert.Contains(alerts, a => a.Rule == "v");
        Assert.All(alerts, a => Assert.True(a.Transactions.Count <= Engine.MaxIdsPerAlert));
    }

    [Fact]
    public void RandomTransactionsAndRules_NeverCrash()
    {
        var rnd = new Random(42);
        var names = new[] { "", " ", "A", "a", "\u202E", "\u0000", "=1+1", "Ab\u200Bc", new string('x', 300), "\uD800", "Nguyễn", "សុខ" };
        for (var round = 0; round < 300; round++)
        {
            var rules = Enumerable.Range(0, rnd.Next(1, 6)).Select(i =>
            {
                var type = RuleSet.Types.Keys.ElementAt(rnd.Next(RuleSet.Types.Count));
                var settings = RuleSet.Types[type].Settings.Select(s => $"\"{s.Key}\": {Pick(rnd, s.Value.Min, s.Value.Max)}");
                return $$"""{ "id": "r{{i}}", "type": "{{type}}", {{string.Join(", ", settings)}} }""";
            });
            var set = RuleSet.Parse($$"""{ "rates": { "KHR": 0.00025, "XAU": 3000 }, "highRiskCountries": ["KP"], "rules": [ {{string.Join(",", rules)}} ] }""");
            var txs = Enumerable.Range(0, rnd.Next(1, 300)).Select(i => new Transaction($"t{i}", $"A{rnd.Next(3)}", T0.AddMinutes(rnd.Next(-100000, 100000)),
                Pick(rnd, 0.01m, 1e9m), new[] { "USD", "KHR", "XAU" }[rnd.Next(3)], (Direction)rnd.Next(2), (Channel)rnd.Next(6),
                rnd.Next(4) == 0 ? null : names[rnd.Next(names.Length)], new[] { null, "KP", "kp", "KH" }[rnd.Next(4)])).ToList();
            var alerts = new Engine(set).Run(txs);
            Assert.All(alerts, a =>
            {
                Assert.NotEmpty(a.Transactions);
                Assert.DoesNotContain(a.Explanation, c => char.IsControl(c) || Text.IsInvisible(c));
            });
        }
    }

    private static decimal Pick(Random rnd, decimal min, decimal max) => rnd.Next(4) switch
    {
        0 => min,
        1 => max,
        _ => decimal.Round(min + (max - min) * (decimal)rnd.NextDouble(), 2) is var v && v >= min && v <= max ? v : min,
    };

    private const string Noise = "\",\r\n\u0000x-=1Z+:\u202E";

    [Fact]
    public void RandomCsv_OnlyEverFailsWithAReason()
    {
        var rnd = new Random(7);
        var good = new StringWriter();
        TransactionCsv.Write(Sample.Generate().Take(50), good);
        var text = good.ToString();
        for (var i = 0; i < 3000; i++)
        {
            var chars = text.ToCharArray();
            for (var k = rnd.Next(1, 8); k > 0; k--)
                chars[rnd.Next(chars.Length)] = Noise[rnd.Next(Noise.Length)];
            try
            {
                var txs = TransactionCsv.Parse(new string(chars));
                new Engine(RuleSet.Parse(RuleSet.Example.Replace("\"THB\": 0.028", "\"THB\": 0.028, \"XSD\": 1", StringComparison.Ordinal))).Run(txs);
            }
            catch (MonitorException e)
            {
                Assert.NotEmpty(e.Problems);
                Assert.All(e.Problems, p => Assert.DoesNotContain(p, c => c is '\u0000' or '\u202E'));
            }
        }
        // random bytes
        for (var i = 0; i < 300; i++)
        {
            var bytes = new byte[rnd.Next(0, 2000)];
            rnd.NextBytes(bytes);
            try { TransactionCsv.Read(new MemoryStream(bytes)); }
            catch (MonitorException) { }
        }
    }

    [Fact]
    public void ARuleSetCantBeMadeExpensive()
    {
        // the largest windows and counts are still bounded work: one pass, each transaction entering and leaving a window once
        var json = "{ \"rules\": [" + string.Join(",", Enumerable.Range(0, 200).Select(i => $$"""{ "id": "r{{i}}", "type": "round-amounts", "multiple": 1, "count": 1000, "days": 365, "minimum": 0 }""")) + "] }";
        var txs = Enumerable.Range(0, 50_000).Select(i => new Transaction($"t{i}", "A", T0.AddMinutes(i), 1, "USD", Direction.In, Channel.Cash)).ToList();
        var sw = Stopwatch.StartNew();
        Assert.Equal(200 * 50, new Engine(RuleSet.Parse(json)).Run(txs).Count);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(10), sw.Elapsed.ToString());
    }

    [Fact]
    public void CounterpartyNamesAreSafeInExplanations()
    {
        var set = RuleSet.Parse("""{ "highRiskCountries": ["KP"], "rules": [ { "id": "h", "type": "high-risk-country" } ] }""");
        var a = Assert.Single(new Engine(set).Run([new("t1", "A", T0, 5, "USD", Direction.Out, Channel.Wire, "Evil\u001b[31m\u202E Co\r\nInjected: line" + new string('x', 500), "KP")]));
        Assert.DoesNotContain(a.Explanation, c => char.IsControl(c) || Text.IsInvisible(c));
        Assert.True(a.Explanation.Length < 200, a.Explanation);
        Assert.Contains("Evil[31m Co Injected: line", a.Explanation, StringComparison.Ordinal);
        _ = Encoding.UTF8;
    }
}
