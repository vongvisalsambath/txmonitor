namespace TxMonitor;

/// <summary>Money into or out of the monitored account.</summary>
public enum Direction
{
    /// <summary>Money received.</summary>
    In,
    /// <summary>Money sent.</summary>
    Out,
}

/// <summary>How the money moved.</summary>
public enum Channel
{
    /// <summary>Cash at a branch or ATM.</summary>
    Cash,
    /// <summary>A card payment.</summary>
    Card,
    /// <summary>A domestic transfer.</summary>
    Transfer,
    /// <summary>An international wire.</summary>
    Wire,
    /// <summary>A cheque.</summary>
    Cheque,
    /// <summary>Anything else.</summary>
    Other,
}

/// <summary>One transaction on a monitored account.</summary>
public sealed record Transaction(
    string Id,
    string Account,
    DateTimeOffset Time,
    decimal Amount,
    string Currency,
    Direction Direction,
    Channel Channel,
    string? Counterparty = null,
    string? CounterpartyCountry = null,
    string? Description = null);

/// <summary>How serious an alert is.</summary>
public enum Severity
{
    /// <summary>Worth a look.</summary>
    Low,
    /// <summary>Review it.</summary>
    Medium,
    /// <summary>Review it first.</summary>
    High,
}

/// <summary>Something a rule found, with the transactions behind it and why.</summary>
/// <param name="Rule">The rule's id from the configuration.</param>
/// <param name="Type">The rule's type, such as "structuring".</param>
/// <param name="Account">The account.</param>
/// <param name="Time">When the pattern was complete: the time of the last transaction in it.</param>
/// <param name="Severity">From the rule's configuration.</param>
/// <param name="Explanation">Plain English, with the numbers.</param>
/// <param name="Transactions">The ids of the transactions that make up the pattern.</param>
public sealed record Alert(string Rule, string Type, string Account, DateTimeOffset Time, Severity Severity, string Explanation, IReadOnlyList<string> Transactions);

/// <summary>Thrown when rules or transactions can't be used. <see cref="Problems"/> says why.</summary>
public sealed class MonitorException(IReadOnlyList<string> problems)
    : Exception(problems.Count == 1 ? problems[0] : $"{problems.Count} problems: " + string.Join(" ", problems))
{
    /// <summary>Every reason, one sentence each.</summary>
    public IReadOnlyList<string> Problems { get; } = problems;
}
