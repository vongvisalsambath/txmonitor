using System.Globalization;
using System.Text;
using System.Text.Json;
using TxMonitor;
using TxMonitor.Cli;

return Cli.Execute(args, Console.Out, Console.Error);

namespace TxMonitor.Cli
{
    /// <summary>The txmonitor command.</summary>
    public static class Cli
    {
        /// <summary>Runs a command; returns 0 when it worked, 2 for bad input.</summary>
        public static int Execute(string[] args, TextWriter stdout, TextWriter stderr)
        {
            try
            {
                if (args.Length == 0 || args[0] is "-h" or "--help" or "help") { stdout.Write(Help); return 0; }
                var (command, positional, options) = Parse(args);
                return command switch
                {
                    "run" => Run(positional, options, stdout, stderr),
                    "check-rules" => CheckRules(positional, options, stdout),
                    "rules" => Rules(positional, options, stdout),
                    "example-rules" => Example(positional, options, stdout),
                    "sample" => SampleCommand(positional, options, stdout),
                    _ => throw new UsageException($"Unknown command \"{Text.Safe(command, 40)}\"."),
                };
            }
            catch (UsageException e)
            {
                stderr.WriteLine("error: " + e.Message);
                stderr.WriteLine("Run 'txmonitor --help' for the commands.");
                return 2;
            }
            catch (MonitorException e)
            {
                foreach (var p in e.Problems) stderr.WriteLine("error: " + Text.Safe(p, 400));
                return 2;
            }
            catch (IOException e)
            {
                stderr.WriteLine("error: " + Text.Safe(e.Message, 300));
                return 2;
            }
            catch (UnauthorizedAccessException e)
            {
                stderr.WriteLine("error: " + Text.Safe(e.Message, 300));
                return 2;
            }
        }

        private const string Help = """
            txmonitor: transaction monitoring rules over a CSV of transactions

              txmonitor run --rules <rules.json> <transactions.csv> [--format text|csv|json] [--min-severity low|medium|high] [--out <file>]
                  Runs the rules and prints the alerts, each explained with its numbers.
              txmonitor check-rules <rules.json>
                  Checks a rule set and lists every problem.
              txmonitor rules
                  Lists the rule types and their settings.
              txmonitor example-rules [--out <file>]
                  Prints a starting rule set to tune.
              txmonitor sample [--seed <n>] [--out <file>]
                  Writes made-up transactions with planted patterns, to try the rules on.

            Transactions CSV columns: id, account, time, amount, currency, direction (in/out),
            channel (cash, card, transfer, wire, cheque, other), and optionally counterparty,
            counterparty_country and description. Times need Z or an offset: 2026-03-01T09:30:00+07:00.

            Exit codes: 0 done, 2 bad input.

            """;

        private static readonly HashSet<string> Flags = ["rules", "format", "min-severity", "out", "seed"];

        internal static (string Command, List<string> Positional, Dictionary<string, string> Options) Parse(string[] args)
        {
            var positional = new List<string>();
            var options = new Dictionary<string, string>(StringComparer.Ordinal);
            for (var i = 1; i < args.Length; i++)
            {
                var a = args[i];
                if (a.StartsWith("--", StringComparison.Ordinal))
                {
                    var name = a[2..];
                    if (!Flags.Contains(name)) throw new UsageException($"Unknown option \"{Text.Safe(a, 40)}\".");
                    if (i + 1 >= args.Length) throw new UsageException($"{a} needs a value.");
                    if (!options.TryAdd(name, args[++i])) throw new UsageException($"{a} is given twice.");
                }
                else positional.Add(a);
            }
            return (args[0], positional, options);
        }

        private static void Allow(List<string> positional, Dictionary<string, string> options, int count, params string[] allowed)
        {
            if (positional.Count != count) throw new UsageException(count == 0 ? "This command takes no file names." : $"This command takes {count} file name{(count == 1 ? "" : "s")}.");
            foreach (var o in options.Keys)
                if (!allowed.Contains(o)) throw new UsageException($"This command has no --{o} option.");
        }

        private static string ReadText(string path, int max, string what)
        {
            var info = new FileInfo(path);
            if (!info.Exists) throw new UsageException($"There's no {what} file \"{Text.Safe(path, 200)}\".");
            if (info.Length > max) throw new UsageException($"The {what} file is larger than {max / 1024 / 1024.0:0.#} MB.");
            try
            {
                return File.ReadAllText(path, new UTF8Encoding(false, true));
            }
            catch (DecoderFallbackException)
            {
                throw new UsageException($"The {what} file isn't UTF-8 text.");
            }
        }

        private static RuleSet LoadRules(string path) => RuleSet.Parse(ReadText(path, RuleSet.MaxBytes, "rules"));

        private static int Run(List<string> positional, Dictionary<string, string> options, TextWriter stdout, TextWriter stderr)
        {
            Allow(positional, options, 1, "rules", "format", "min-severity", "out");
            if (!options.TryGetValue("rules", out var rulesPath)) throw new UsageException("run needs --rules <rules.json>.");
            var rules = LoadRules(rulesPath);
            var format = options.GetValueOrDefault("format", "text");
            if (format is not ("text" or "csv" or "json")) throw new UsageException("--format is text, csv or json.");
            var min = options.GetValueOrDefault("min-severity", "low") switch
            {
                "low" => Severity.Low, "medium" => Severity.Medium, "high" => Severity.High,
                _ => throw new UsageException("--min-severity is low, medium or high."),
            };
            IReadOnlyList<Transaction> txs;
            using (var input = OpenInput(positional[0])) txs = TransactionCsv.Read(input);
            var alerts = new Engine(rules).Run(txs).Where(a => a.Severity >= min).ToList();
            Output(options, stdout, positional[0], w =>
            {
                switch (format)
                {
                    case "csv": AlertCsv.Write(alerts, w); break;
                    case "json": Json(alerts, w); break;
                    default:
                        foreach (var a in alerts)
                        {
                            w.WriteLine($"{a.Severity.ToString().ToUpperInvariant(),-6} {a.Time.ToString("yyyy-MM-dd HH:mm zzz", CultureInfo.InvariantCulture)}  {a.Account}  [{a.Rule}]");
                            w.WriteLine("       " + a.Explanation);
                            w.WriteLine("       transactions: " + string.Join(", ", a.Transactions));
                        }
                        break;
                }
            });
            var accounts = txs.Select(t => t.Account).Distinct(StringComparer.Ordinal).Count();
            stderr.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"{txs.Count:N0} transactions on {accounts:N0} accounts; {alerts.Count:N0} alerts ({alerts.Count(a => a.Severity == Severity.High)} high, {alerts.Count(a => a.Severity == Severity.Medium)} medium, {alerts.Count(a => a.Severity == Severity.Low)} low)."));
            return 0;
        }

        private static Stream OpenInput(string path)
        {
            if (path == "-") return Console.OpenStandardInput();
            if (!File.Exists(path)) throw new UsageException($"There's no transactions file \"{Text.Safe(path, 200)}\".");
            return File.OpenRead(path);
        }

        private static void Output(Dictionary<string, string> options, TextWriter stdout, string? input, Action<TextWriter> write)
        {
            if (!options.TryGetValue("out", out var path)) { write(stdout); return; }
            if (input is not null && input != "-" && Path.GetFullPath(path) == Path.GetFullPath(input)) throw new UsageException("--out would overwrite the input file.");
            using var w = new StreamWriter(path, false, new UTF8Encoding(false));
            write(w);
        }

        private static void Json(IReadOnlyList<Alert> alerts, TextWriter w)
        {
            using var buffer = new MemoryStream();
            using (var j = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = true }))
            {
                j.WriteStartArray();
                foreach (var a in alerts)
                {
                    j.WriteStartObject();
                    j.WriteString("time", a.Time.ToString("yyyy-MM-dd'T'HH:mm:sszzz", CultureInfo.InvariantCulture));
                    j.WriteString("account", a.Account);
                    j.WriteString("rule", a.Rule);
                    j.WriteString("type", a.Type);
                    j.WriteString("severity", a.Severity.ToString().ToLowerInvariant());
                    j.WriteString("explanation", a.Explanation);
                    j.WriteStartArray("transactions");
                    foreach (var id in a.Transactions) j.WriteStringValue(id);
                    j.WriteEndArray();
                    j.WriteEndObject();
                }
                j.WriteEndArray();
            }
            w.WriteLine(Encoding.UTF8.GetString(buffer.ToArray()));
        }

        private static int CheckRules(List<string> positional, Dictionary<string, string> options, TextWriter stdout)
        {
            Allow(positional, options, 1);
            var rules = LoadRules(positional[0]);
            stdout.WriteLine($"The rule set is valid: {rules.Rules.Count} rules, amounts in {rules.BaseCurrency}, {rules.Rates.Count} currencies, {rules.HighRiskCountries.Count} high-risk countries.");
            foreach (var r in rules.Rules)
                stdout.WriteLine($"  {r.Id} ({r.Type}, {r.Severity.ToString().ToLowerInvariant()}): " +
                    string.Join(", ", r.Numbers.Select(n => $"{n.Key} {n.Value.ToString(CultureInfo.InvariantCulture)}")));
            return 0;
        }

        private static int Rules(List<string> positional, Dictionary<string, string> options, TextWriter stdout)
        {
            Allow(positional, options, 0);
            foreach (var (type, spec) in RuleSet.Types)
            {
                stdout.WriteLine($"{type}: {spec.Description}");
                foreach (var (name, s) in spec.Settings)
                    stdout.WriteLine($"  {name}{(s.Required ? "" : " (optional)")}: {s.Meaning}; {s.Min.ToString(CultureInfo.InvariantCulture)} to {s.Max.ToString("0.###", CultureInfo.InvariantCulture)}");
            }
            stdout.WriteLine("Every rule also takes id, severity (low, medium, high), and optionally channels and direction.");
            return 0;
        }

        private static int Example(List<string> positional, Dictionary<string, string> options, TextWriter stdout)
        {
            Allow(positional, options, 0, "out");
            Output(options, stdout, null, w => w.WriteLine(RuleSet.Example.Trim()));
            return 0;
        }

        private static int SampleCommand(List<string> positional, Dictionary<string, string> options, TextWriter stdout)
        {
            Allow(positional, options, 0, "seed", "out");
            var seed = 2026;
            if (options.TryGetValue("seed", out var s) && !int.TryParse(s, NumberStyles.None, CultureInfo.InvariantCulture, out seed))
                throw new UsageException("--seed is a whole number.");
            Output(options, stdout, null, w => TransactionCsv.Write(TxMonitor.Sample.Generate(seed), w));
            return 0;
        }
    }

    /// <summary>A mistake in how the command was called.</summary>
    public sealed class UsageException(string message) : Exception(message);
}
