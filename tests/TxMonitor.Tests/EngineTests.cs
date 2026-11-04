using Xunit;

namespace TxMonitor.Tests;

public sealed class EngineTests
{
    private static readonly DateTimeOffset T0 = new(2026, 5, 4, 9, 0, 0, TimeSpan.FromHours(7));
    private static int next;

    internal static Transaction Tx(double hours, decimal amount, Direction d = Direction.In, Channel ch = Channel.Transfer, string account = "A1",
        string currency = "USD", string? cp = null, string? country = null) =>
        new($"t{Interlocked.Increment(ref next):00000}", account, T0.AddHours(hours), amount, currency, d, ch, cp, country);

    private static IReadOnlyList<Alert> Run(string rules, params Transaction[] txs) =>
        new Engine(RuleSet.Parse($$"""{ "rates": { "KHR": 0.00025 }, "highRiskCountries": ["KP", "MM"], "rules": [ {{rules}} ] }""")).Run(txs);

    [Fact]
    public void LargeIsAtOrAboveTheAmount_InTheBaseCurrency_ForTheChosenChannels()
    {
        const string rule = """{ "id": "big", "type": "large", "amount": 10000, "channels": ["cash"] }""";
        Assert.Empty(Run(rule, Tx(0, 9999.99m, ch: Channel.Cash)));
        Assert.Single(Run(rule, Tx(0, 10000m, ch: Channel.Cash)));
        Assert.Empty(Run(rule, Tx(0, 50000m, ch: Channel.Wire)));                     // not cash
        var a = Assert.Single(Run(rule, Tx(0, 40_000_000m, ch: Channel.Cash, currency: "KHR")));   // 10,000 USD
        Assert.Contains("10,000.00 USD (40,000,000.00 KHR)", a.Explanation, StringComparison.Ordinal);
        Assert.Equal(("big", "large", "A1", Severity.Medium), (a.Rule, a.Type, a.Account, a.Severity));
    }

    [Fact]
    public void DirectionFiltersARule()
    {
        const string rule = """{ "id": "big-out", "type": "large", "amount": 100, "direction": "out" }""";
        Assert.Empty(Run(rule, Tx(0, 500m)));
        Assert.Single(Run(rule, Tx(0, 500m, Direction.Out)));
    }

    [Fact]
    public void Structuring_NeedsEnoughDepositsJustUnderTheThreshold_WithinTheWindow()
    {
        const string rule = """{ "id": "s", "type": "structuring", "threshold": 10000, "near": 0.8, "count": 3, "days": 7 }""";
        var cash = Channel.Cash;
        var a = Assert.Single(Run(rule, Tx(0, 9000m, ch: cash), Tx(24, 9500m, ch: cash), Tx(48, 8500m, ch: cash)));
        Assert.Equal(3, a.Transactions.Count);
        Assert.Contains("3 deposits by cash within 2 days", a.Explanation, StringComparison.Ordinal);
        Assert.Contains("together 27,000.00 USD", a.Explanation, StringComparison.Ordinal);

        Assert.Empty(Run(rule, Tx(0, 9000m, ch: cash), Tx(24, 9500m, ch: cash), Tx(48, 7999m, ch: cash)));        // one below "near"
        Assert.Empty(Run(rule, Tx(0, 9000m, ch: cash), Tx(24, 9500m, ch: cash), Tx(48, 10000m, ch: cash)));       // one at the threshold: reported anyway
        Assert.Empty(Run(rule, Tx(0, 9000m, ch: cash), Tx(24, 9500m, ch: cash), Tx(24 * 7 + 1, 9000m, ch: cash))); // outside 7 days
        Assert.Empty(Run(rule, Tx(0, 9000m), Tx(24, 9500m), Tx(48, 8500m)));                                       // transfers, not cash
        Assert.Empty(Run(rule, Tx(0, 9000m, Direction.Out, cash), Tx(24, 9500m, Direction.Out, cash), Tx(48, 8500m, Direction.Out, cash)));
        // other accounts don't add up
        Assert.Empty(Run(rule, Tx(0, 9000m, ch: cash), Tx(24, 9500m, ch: cash, account: "A2"), Tx(48, 8500m, ch: cash)));
    }

    [Fact]
    public void Structuring_ChannelsCanBeWidened()
    {
        const string rule = """{ "id": "s", "type": "structuring", "threshold": 10000, "count": 2, "channels": ["cash", "cheque"] }""";
        Assert.Single(Run(rule, Tx(0, 9000m, ch: Channel.Cash), Tx(5, 9000m, ch: Channel.Cheque)));
    }

    [Fact]
    public void AfterAnAlert_ARuleStartsCountingAfresh()
    {
        const string rule = """{ "id": "v", "type": "velocity", "count": 3, "hours": 24 }""";
        var alerts = Run(rule, Enumerable.Range(0, 7).Select(i => Tx(i, 10m)).ToArray());
        Assert.Equal(2, alerts.Count);                  // 3 + 3, and the 7th is the start of a new count
        Assert.Equal(3, alerts[0].Transactions.Count);
        Assert.Empty(alerts[0].Transactions.Intersect(alerts[1].Transactions));
    }

    [Fact]
    public void Velocity_CountsWithinTheHours()
    {
        const string rule = """{ "id": "v", "type": "velocity", "count": 3, "hours": 2 }""";
        Assert.Single(Run(rule, Tx(0, 1m), Tx(1, 1m), Tx(2, 1m)));
        Assert.Empty(Run(rule, Tx(0, 1m), Tx(1, 1m), Tx(2.01, 1m)));
    }

    [Fact]
    public void PassThrough_MoneyInThenMostOfItOut()
    {
        const string rule = """{ "id": "p", "type": "pass-through", "minimum": 5000, "share": 0.8, "hours": 48 }""";
        var a = Assert.Single(Run(rule, Tx(0, 10000m, ch: Channel.Wire), Tx(10, 4000m, Direction.Out), Tx(20, 4100m, Direction.Out)));
        Assert.Contains("10,000.00 USD came in (1 transaction) and 8,100.00 USD (81%) went out again within 20 hours", a.Explanation, StringComparison.Ordinal);
        Assert.Equal(3, a.Transactions.Count);

        Assert.Empty(Run(rule, Tx(0, 10000m), Tx(10, 7000m, Direction.Out)));                 // only 70% out
        Assert.Empty(Run(rule, Tx(0, 4000m), Tx(10, 4000m, Direction.Out)));                  // under the minimum
        Assert.Empty(Run(rule, Tx(0, 10000m), Tx(49, 9000m, Direction.Out)));                 // too late
        Assert.Empty(Run(rule, Tx(0, 9000m, Direction.Out), Tx(1, 10000m)));                  // out before in isn't passing it on
        // two deposits together
        Assert.Single(Run(rule, Tx(0, 3000m), Tx(5, 3000m), Tx(30, 5000m, Direction.Out)));
    }

    [Fact]
    public void PassThrough_ForgetsMoneyInOutsideTheWindow()
    {
        const string rule = """{ "id": "p", "type": "pass-through", "minimum": 5000, "share": 0.8, "hours": 24 }""";
        // 10,000 in at hour 0, a little out at 1; at 30 the first deposit has gone from the window and only 1,000 is in it
        Assert.Empty(Run(rule, Tx(0, 10000m), Tx(1, 100m, Direction.Out), Tx(29, 1000m), Tx(30, 900m, Direction.Out)));
    }

    [Fact]
    public void HighRiskCountry_FromTheListAboveTheMinimum()
    {
        const string rule = """{ "id": "h", "type": "high-risk-country", "minimum": 1000 }""";
        var a = Assert.Single(Run(rule, Tx(0, 5000m, Direction.Out, Channel.Wire, cp: "Example Co", country: "mm")));
        Assert.Contains("to \"Example Co\" in MM", a.Explanation, StringComparison.Ordinal);
        Assert.Empty(Run(rule, Tx(0, 500m, country: "KP")));
        Assert.Empty(Run(rule, Tx(0, 5000m, country: "KH")));
        Assert.Empty(Run(rule, Tx(0, 5000m)));
    }

    [Fact]
    public void RoundAmounts_UseTheAmountInItsOwnCurrency()
    {
        const string rule = """{ "id": "r", "type": "round-amounts", "multiple": 1000, "count": 3, "days": 30 }""";
        Assert.Single(Run(rule, Tx(0, 2000m), Tx(48, 5000m), Tx(96, 3000m)));
        Assert.Empty(Run(rule, Tx(0, 2000m), Tx(48, 5000m), Tx(96, 3000.01m)));
        Assert.Empty(Run(rule, Tx(0, 2000m), Tx(48, 5000m), Tx(24 * 31, 3000m)));
        // 4,000,000 riel is round in riel, and worth 1,000 USD: at the default minimum of one multiple
        Assert.Single(Run(rule, Tx(0, 4_000_000m, currency: "KHR"), Tx(1, 8_000_000m, currency: "KHR"), Tx(2, 4_000_000m, currency: "KHR")));
        // but 1,000,000 riel (250 USD) is below it
        Assert.Empty(Run(rule, Tx(0, 1_000_000m, currency: "KHR"), Tx(1, 1_000_000m, currency: "KHR"), Tx(2, 1_000_000m, currency: "KHR")));
    }

    [Fact]
    public void Dormant_NeedsEarlierActivity_LongEnoughAgo()
    {
        const string rule = """{ "id": "d", "type": "dormant", "days": 90, "minimum": 1000 }""";
        Assert.Empty(Run(rule, Tx(0, 5000m)));                                          // first ever: not dormant, just new
        var a = Assert.Single(Run(rule, Tx(0, 50m), Tx(24 * 100, 5000m, Direction.Out, Channel.Cash)));
        Assert.Contains("A cash withdrawal of 5,000.00 USD after 100 days with no activity", a.Explanation, StringComparison.Ordinal);
        Assert.Empty(Run(rule, Tx(0, 50m), Tx(24 * 89, 5000m)));
        Assert.Empty(Run(rule, Tx(0, 50m), Tx(24 * 100, 500m)));                        // under the minimum
        Assert.Empty(Run(rule, Tx(0, 50m), Tx(24 * 50, 1m), Tx(24 * 100, 5000m)));        // a small transaction in between counts as activity
    }

    [Fact]
    public void FanInAndFanOut_CountDifferentCounterparties()
    {
        const string fanIn = """{ "id": "f", "type": "fan-in", "counterparties": 3, "days": 7 }""";
        Assert.Single(Run(fanIn, Tx(0, 100m, cp: "A"), Tx(1, 100m, cp: "B"), Tx(2, 100m, cp: "C")));
        Assert.Empty(Run(fanIn, Tx(0, 100m, cp: "A"), Tx(1, 100m, cp: "B"), Tx(2, 100m, cp: "b")));     // same sender, other case
        Assert.Empty(Run(fanIn, Tx(0, 100m, cp: "A"), Tx(1, 100m, cp: "B"), Tx(2, 100m, cp: " B ")));
        Assert.Empty(Run(fanIn, Tx(0, 100m, cp: "A"), Tx(1, 100m, cp: "B"), Tx(2, 100m, cp: "B\u200B")));  // nor with an invisible character
        Assert.Empty(Run(fanIn, Tx(0, 100m, cp: "A"), Tx(1, 100m, cp: "B"), Tx(24 * 8, 100m, cp: "C")));
        Assert.Empty(Run(fanIn, Tx(0, 100m, Direction.Out, cp: "A"), Tx(1, 100m, Direction.Out, cp: "B"), Tx(2, 100m, Direction.Out, cp: "C")));
        Assert.Empty(Run(fanIn, Tx(0, 100m), Tx(1, 100m), Tx(2, 100m)));                                // no counterparties
        const string fanOut = """{ "id": "f", "type": "fan-out", "counterparties": 3 }""";
        var a = Assert.Single(Run(fanOut, Tx(0, 100m, Direction.Out, cp: "A"), Tx(1, 100m, Direction.Out, cp: "B"), Tx(2, 100m, Direction.Out, cp: "C")));
        Assert.Contains("to 3 different recipients", a.Explanation, StringComparison.Ordinal);
    }

    [Fact]
    public void NewCounterparty_OnlyAfterHistory_AndOnlyTheFirstTime()
    {
        const string rule = """{ "id": "n", "type": "new-counterparty", "minimum": 1000 }""";
        Assert.Empty(Run(rule, Tx(0, 5000m, Direction.Out, cp: "X")));                                        // no history yet
        Assert.Single(Run(rule, Tx(0, 50m, cp: "Employer"), Tx(1, 5000m, Direction.Out, cp: "X")));
        Assert.Single(Run(rule, Tx(0, 50m), Tx(1, 5000m, Direction.Out, cp: "X"), Tx(2, 5000m, Direction.Out, cp: "x")));  // the second time is known
        Assert.Empty(Run(rule, Tx(0, 50m, Direction.Out, cp: "X"), Tx(1, 5000m, Direction.Out, cp: "X")));
        Assert.Empty(Run(rule, Tx(0, 50m), Tx(1, 500m, Direction.Out, cp: "X")));
    }

    [Fact]
    public void TheOrderTransactionsArriveInDoesntMatter()
    {
        var rules = RuleSet.Parse(RuleSet.Example);
        var txs = Sample.Generate();
        var a = new Engine(rules).Run(txs);
        var b = new Engine(rules).Run(txs.Reverse().ToArray());
        var c = new Engine(rules).Run(txs.OrderBy(t => t.Id.GetHashCode(StringComparison.Ordinal)).ToArray());
        Assert.Equal(a, b, new AlertComparer());
        Assert.Equal(a, c, new AlertComparer());
    }

    private sealed class AlertComparer : IEqualityComparer<Alert>
    {
        public bool Equals(Alert? x, Alert? y) => x is not null && y is not null && x with { Transactions = [] } == y with { Transactions = [] } && x.Transactions.SequenceEqual(y.Transactions);
        public int GetHashCode(Alert obj) => obj.Rule.GetHashCode(StringComparison.Ordinal);
    }

    [Fact]
    public void TheSampleHidesExactlyThePlantedPatterns()
    {
        var alerts = new Engine(RuleSet.Parse(RuleSet.Example)).Run(Sample.Generate());
        Assert.Equal(Sample.Planted.Keys.Order(), alerts.Select(a => a.Account).Distinct().Order());
        Assert.Contains(alerts, a => a is { Account: "ACC-2001", Type: "structuring" });
        Assert.Contains(alerts, a => a is { Account: "ACC-2002", Type: "pass-through" });
        Assert.Contains(alerts, a => a is { Account: "ACC-2003", Type: "velocity" });
        Assert.Contains(alerts, a => a is { Account: "ACC-2004", Type: "dormant" });
        Assert.Contains(alerts, a => a is { Account: "ACC-2005", Type: "high-risk-country" });
        Assert.Contains(alerts, a => a is { Account: "ACC-2006", Type: "fan-in" });
        Assert.Contains(alerts, a => a is { Account: "ACC-2007", Type: "round-amounts" });
        Assert.Contains(alerts, a => a is { Account: "ACC-2008", Type: "new-counterparty" });
        Assert.Contains(alerts, a => a is { Account: "ACC-2009", Type: "large" });
        // and it's the same every time
        Assert.Equal(Sample.Generate(), Sample.Generate());
    }

    [Fact]
    public void AnAlertListsAtMostAHundredIds_ButCountsThemAll()
    {
        const string rule = """{ "id": "v", "type": "velocity", "count": 500, "hours": 24 }""";
        var a = Assert.Single(Run(rule, Enumerable.Range(0, 500).Select(i => Tx(i / 100.0, 1m)).ToArray()));
        Assert.Equal(Engine.MaxIdsPerAlert, a.Transactions.Count);
        Assert.StartsWith("500 transactions", a.Explanation, StringComparison.Ordinal);
    }

    [Fact]
    public void ExplanationsDontDependOnTheMachinesCulture()
    {
        var before = System.Globalization.CultureInfo.CurrentCulture;
        try
        {
            System.Globalization.CultureInfo.CurrentCulture = new System.Globalization.CultureInfo("de-DE");
            var a = Assert.Single(Run("""{ "id": "b", "type": "large", "amount": 1000 }""", Tx(0, 1234.5m)));
            Assert.Contains("1,234.50 USD", a.Explanation, StringComparison.Ordinal);
        }
        finally
        {
            System.Globalization.CultureInfo.CurrentCulture = before;
        }
    }
}
