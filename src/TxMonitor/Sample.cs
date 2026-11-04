namespace TxMonitor;

/// <summary>
/// Made-up transactions for trying the rules: ordinary accounts, and nine accounts each hiding one pattern that
/// <see cref="RuleSet.Example"/> should find. Every name and number is invented.
/// </summary>
public static class Sample
{
    /// <summary>Which account hides which pattern, so a demo can show what was planted.</summary>
    public static readonly IReadOnlyDictionary<string, string> Planted = new Dictionary<string, string>
    {
        ["ACC-2001"] = "structuring: four cash deposits just under 10,000 in five days",
        ["ACC-2002"] = "pass-through: 15,000 in by wire, 13,800 out again within a day and a half",
        ["ACC-2003"] = "velocity: thirty card payments in an afternoon",
        ["ACC-2004"] = "dormant: quiet since March, then a 12,000 withdrawal in October",
        ["ACC-2005"] = "high-risk country: a wire to a counterparty in a listed country",
        ["ACC-2006"] = "fan-in: twelve different senders in five days",
        ["ACC-2007"] = "round amounts: five round cash deposits in three weeks",
        ["ACC-2008"] = "new counterparty: 9,500 to a payee never paid before",
        ["ACC-2009"] = "large cash: a cash deposit of 61,500,000 KHR (about 15,000 USD)",
    };

    /// <summary>The transactions, the same for the same seed.</summary>
    public static IReadOnlyList<Transaction> Generate(int seed = 2026)
    {
        var rnd = new Random(seed);
        var tz = TimeSpan.FromHours(7);
        var start = new DateTimeOffset(2026, 3, 1, 0, 0, 0, tz);
        var end = new DateTimeOffset(2026, 10, 31, 0, 0, 0, tz);
        var list = new List<(string Account, DateTimeOffset Time, decimal Amount, string Currency, Direction Dir, Channel Ch, string? Cp, string? Country, string? Desc)>();
        void Add(string acc, DateTimeOffset t, decimal amount, string cur, Direction d, Channel ch, string? cp = null, string? country = null, string? desc = null) =>
            list.Add((acc, t, amount, cur, d, ch, cp, country, desc));
        DateTimeOffset At(DateTimeOffset day, int hourFrom, int hourTo) => day.AddHours(rnd.Next(hourFrom, hourTo)).AddMinutes(rnd.Next(60)).AddSeconds(rnd.Next(60));
        // ordinary amounts are never round thousands, so they don't look like the round-amounts pattern by chance
        decimal Cents(double from, double to, decimal scale = 1) => decimal.Round((decimal)(from + rnd.NextDouble() * (to - from)) * scale, 2) is var v && v % 1000 == 0 ? v + 1.37m : v;

        string[] employers = ["Riverside Garment Co", "Golden Lotus Hotel", "Angkor Logistics", "Bayon Software", "Sunrise Clinic"];
        string[] shops = ["Corner Mart", "City Pharmacy", "Lucky Noodle House", "Metro Fuel", "Green Market", "Book Nook", "Phone Plus", "Daily Bakery"];
        string[] landlords = ["Lotus Property Rentals", "Mr Dara Rentals", "Palm Court Apartments"];

        // ordinary accounts: a salary, rent, utilities and card spending
        for (var n = 0; n < 40; n++)
        {
            var acc = $"ACC-{1001 + n}";
            var cur = n % 8 == 3 ? "KHR" : n % 8 == 5 ? "THB" : "USD";
            var scale = cur switch { "KHR" => 4100m, "THB" => 36m, _ => 1m };
            var salary = Cents(600, 3200, scale);
            var employer = employers[n % employers.Length];
            var landlord = landlords[n % landlords.Length];
            for (var month = start; month < end; month = month.AddMonths(1))
            {
                Add(acc, At(month.AddDays(rnd.Next(0, 2)), 8, 11), salary, cur, Direction.In, Channel.Transfer, employer, "KH", "salary");
                Add(acc, At(month.AddDays(rnd.Next(2, 5)), 9, 18), Cents(180, 690, scale), cur, Direction.Out, Channel.Transfer, landlord, "KH", "rent");
                Add(acc, At(month.AddDays(rnd.Next(10, 15)), 9, 18), Cents(15, 60, scale), cur, Direction.Out, Channel.Transfer, "City Power and Water", "KH", "utilities");
                for (var i = rnd.Next(6, 18); i > 0; i--)
                    Add(acc, At(month.AddDays(rnd.Next(0, 28)), 7, 22), Cents(2, 85, scale), cur, Direction.Out, Channel.Card, shops[rnd.Next(shops.Length)], "KH");
                if (rnd.Next(3) == 0)
                    Add(acc, At(month.AddDays(rnd.Next(0, 28)), 9, 17), Cents(20, 300, scale), cur, Direction.Out, Channel.Cash, null, null, "ATM");
            }
        }

        // the planted patterns
        var d = new DateTimeOffset(2026, 9, 14, 0, 0, 0, tz);
        foreach (var (day, amount) in new[] { (0, 9200m), (1, 9480m), (3, 8950m), (4, 9730m) })
            Add("ACC-2001", At(d.AddDays(day), 9, 16), amount, "USD", Direction.In, Channel.Cash, null, null, "branch deposit");
        Background("ACC-2001");

        d = new DateTimeOffset(2026, 8, 3, 10, 12, 0, tz);
        Add("ACC-2002", d, 15000m, "USD", Direction.In, Channel.Wire, "Kestrel Holdings Ltd", "SG", "consulting");
        Add("ACC-2002", d.AddHours(20), 7000m, "USD", Direction.Out, Channel.Transfer, "R. Vann", "KH");
        Add("ACC-2002", d.AddHours(33), 6800m, "USD", Direction.Out, Channel.Transfer, "S. Mey", "KH");
        Background("ACC-2002");

        d = new DateTimeOffset(2026, 7, 21, 13, 0, 0, tz);
        for (var i = 0; i < 30; i++)
            Add("ACC-2003", d.AddMinutes(i * 9 + rnd.Next(5)), Cents(20, 95), "USD", Direction.Out, Channel.Card, $"Online Store {(char)('A' + i % 6)}", "KH");
        Background("ACC-2003");

        Add("ACC-2004", new DateTimeOffset(2026, 3, 4, 10, 5, 0, tz), 850m, "USD", Direction.In, Channel.Transfer, "Golden Lotus Hotel", "KH", "salary");
        Add("ACC-2004", new DateTimeOffset(2026, 3, 9, 15, 40, 0, tz), 120.50m, "USD", Direction.Out, Channel.Card, "Corner Mart", "KH");
        Add("ACC-2004", new DateTimeOffset(2026, 10, 20, 9, 30, 0, tz), 12450m, "USD", Direction.In, Channel.Wire, "Northgate Ventures", "HK", "loan repayment");
        Add("ACC-2004", new DateTimeOffset(2026, 10, 21, 11, 15, 0, tz), 12000m, "USD", Direction.Out, Channel.Cash, null, null, "branch withdrawal");

        Add("ACC-2005", new DateTimeOffset(2026, 6, 11, 14, 22, 0, tz), 4500m, "USD", Direction.Out, Channel.Wire, "Example Import Co", "MM", "machine parts");
        Background("ACC-2005");

        d = new DateTimeOffset(2026, 5, 18, 0, 0, 0, tz);
        string[] senders = ["K. Sok", "L. Chan", "M. Heng", "N. Lim", "P. Ouk", "R. Keo", "S. Pich", "T. Chea", "V. Nhem", "Y. Prak", "B. Seng", "C. Touch"];
        for (var i = 0; i < senders.Length; i++)
            Add("ACC-2006", At(d.AddDays(i * 5 / senders.Length), 8, 21), Cents(300, 900), "USD", Direction.In, Channel.Transfer, senders[i], "KH");
        Background("ACC-2006");

        d = new DateTimeOffset(2026, 4, 6, 0, 0, 0, tz);
        foreach (var (day, amount) in new[] { (0, 2000m), (4, 5000m), (9, 3000m), (15, 4000m), (20, 2000m) })
            Add("ACC-2007", At(d.AddDays(day), 9, 16), amount, "USD", Direction.In, Channel.Cash, null, null, "branch deposit");
        Background("ACC-2007");

        Background("ACC-2008");
        Add("ACC-2008", new DateTimeOffset(2026, 9, 29, 16, 48, 0, tz), 9500m, "USD", Direction.Out, Channel.Transfer, "Bright Future Trading", "KH", "invoice 4471");

        Add("ACC-2009", new DateTimeOffset(2026, 8, 25, 10, 2, 0, tz), 61_500_000m, "KHR", Direction.In, Channel.Cash, null, null, "branch deposit");
        Background("ACC-2009", "KHR", 4100m);

        void Background(string acc, string cur = "USD", decimal scale = 1)
        {
            for (var month = start; month < end; month = month.AddMonths(1))
            {
                Add(acc, At(month.AddDays(1), 8, 11), Cents(700, 1500, scale), cur, Direction.In, Channel.Transfer, "Riverside Garment Co", "KH", "salary");
                for (var i = rnd.Next(4, 10); i > 0; i--)
                    Add(acc, At(month.AddDays(rnd.Next(0, 28)), 7, 22), Cents(2, 60, scale), cur, Direction.Out, Channel.Card, shops[rnd.Next(shops.Length)], "KH");
            }
        }

        var ordered = list.OrderBy(x => x.Time).ThenBy(x => x.Account, StringComparer.Ordinal).ToList();
        return ordered.Select((x, i) => new Transaction($"T{i + 1:000000}", x.Account, x.Time, x.Amount, x.Currency, x.Dir, x.Ch, x.Cp, x.Country, x.Desc)).ToList();
    }
}
