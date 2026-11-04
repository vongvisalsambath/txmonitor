using Microsoft.AspNetCore.Components.Forms;

namespace TxMonitor.Web.Pages;

public partial class Home
{
    private const int MaxShown = 300;
    private const int MaxFileMb = 20;

    private string rulesJson = RuleSet.Example.Trim();
    private IReadOnlyList<Transaction>? transactions;
    private bool sample;
    private string status = "No transactions loaded yet.";
    private IReadOnlyList<Alert>? alerts;
    private IReadOnlyList<string>? problems;
    private string problemTitle = "";
    private Severity minSeverity = Severity.Low;
    private bool running;
    private int txCount, accountCount;

    private IEnumerable<Alert> Filtered => alerts?.Where(a => a.Severity >= minSeverity) ?? [];
    private IEnumerable<Alert> Shown => Filtered.Take(MaxShown);

    private void UseSample()
    {
        transactions = Sample.Generate();
        sample = true;
        status = $"The made-up sample: {Describe(transactions)}, with {Sample.Planted.Count} accounts each hiding one pattern.";
        alerts = null;
        problems = null;
    }

    private async Task Load(InputFileChangeEventArgs e)
    {
        alerts = null;
        problems = null;
        transactions = null;
        sample = false;
        status = "Reading…";
        try
        {
            if (e.File.Size > MaxFileMb * 1024L * 1024) throw new MonitorException([$"The file is larger than {MaxFileMb} MB; use the command line for big files."]);
            await using var stream = e.File.OpenReadStream(MaxFileMb * 1024L * 1024);
            transactions = await TransactionCsv.ReadAsync(stream);
            status = $"{Text.Safe(e.File.Name, 80)}: {Describe(transactions)}.";
        }
        catch (MonitorException ex)
        {
            Fail("The file can't be used:", ex.Problems, loading: true);
        }
        catch (IOException ex)
        {
            Fail("The file can't be read:", [Text.Safe(ex.Message, 200)], loading: true);
        }
    }

    private void ResetRules()
    {
        rulesJson = RuleSet.Example.Trim();
        problems = null;
    }

    private async Task Run()
    {
        problems = null;
        alerts = null;
        if (transactions is null) UseSample();
        running = true;
        await Task.Yield();  // let "Running…" show before the work
        try
        {
            var rules = RuleSet.Parse(rulesJson);
            var found = new Engine(rules).Run(transactions!);
            txCount = transactions!.Count;
            accountCount = transactions.Select(t => t.Account).Distinct(StringComparer.Ordinal).Count();
            alerts = found;
        }
        catch (MonitorException ex)
        {
            Fail("The rules or transactions can't be used:", ex.Problems);
        }
        finally
        {
            running = false;
        }
    }

    private void Fail(string title, IReadOnlyList<string> list, bool loading = false)
    {
        problemTitle = title;
        problems = list.Select(p => Text.Safe(p, 400)).ToList();
        if (loading) status = "No transactions loaded.";
    }

    private static string Describe(IReadOnlyList<Transaction> txs) =>
        $"{Plural(txs.Count, "transaction")} on {Plural(txs.Select(t => t.Account).Distinct(StringComparer.Ordinal).Count(), "account")}";

    private static string Plural(int n, string word) => n == 1 ? $"1 {word}" : $"{n:N0} {word}s";
}
