# Security

TxMonitor reads two kinds of input from outside: rule sets and transaction files. Its alerts are then read by people, in a terminal, in a spreadsheet or in a browser. A monitoring tool can also fail quietly, by missing a pattern it should catch. TxMonitor is built with all of that in mind.

## Reporting a problem

Please report vulnerabilities privately, through GitHub's **Security → Report a vulnerability** on this repository, not in a public issue. A pattern the rules should catch but don't is welcome as a normal issue, with made-up data.

## What is protected, and how

| Threat | Protection | Tested by |
|---|---|---|
| **A rule set that runs code** | A rule set is data. It chooses a type from a fixed list and sets numbers within checked ranges. Unknown types, settings and keys are refused, and so are numbers given as text. | `ABadRuleSetIsRefused_SayingWhy` (28 cases) |
| **A rule set that's expensive** | At most 200 rules and 1 MB, with JSON depth limited to 8. Every window is a queue with running totals, so each transaction enters and leaves each window once, however large the window or count. | `HostileRuleSetsAreBounded`, `ARuleSetCantBeMadeExpensive`, `ManyTransactionsInOneWindow_StayLinear` |
| **Too much output** | A run stops with a clear message after 100,000 alerts: rules that loose need tuning, not a longer list. Each alert lists at most 100 transaction ids. | `RulesTooLooseForTheData_StopWithAReason`, `AnAlertListsAtMostAHundredIds_ButCountsThemAll` |
| **Many accounts** | Accounts are run one at a time. Only one account's state is held, and it's reused for the next account, so memory doesn't grow with the number of accounts. | `ManyRulesOverManyAccounts_RunInOnePass` (200 rules, 200,000 transactions, 100,000 accounts) |
| **Overflow** | Amounts are at most 10^15. Rates run from 10^-9 to 10^9. A transaction worth more than 10^20 in the base currency is refused, so sums of a million transactions fit in a `decimal`. The base currency's rate can't be changed. | `HugeAmountsCantOverflowTheSums`, `TheBaseCurrencysRateCantBeChanged` |
| **Hostile transaction files** | At most 64 MB, a million rows, 50 cells per row and 1,000 characters per field. A file must be UTF-8. Amounts must be plain numbers: no signs, exponents, `NaN` or thousands separators. Times must carry a time zone. A file is refused with its first 20 problems. | `ABadRowIsRefused_ByLine`, `HostileFilesAreBounded`, `RandomCsv_OnlyEverFailsWithAReason` (3,000 mutated files and 300 random byte strings) |
| **Evading a rule with look-alike names** | Counterparty names are compared without case, extra spaces, invisible characters or broken surrogates. "B", "b", " B " and "B" followed by a zero-width space are one sender. | `FanInAndFanOut_CountDifferentCounterparties`, `NewCounterparty_OnlyAfterHistory_AndOnlyTheFirstTime` |
| **Misleading text** | Ids and accounts with control characters, direction overrides, zero-width characters or half a surrogate pair are refused. Counterparty names in explanations have such characters removed and are cut to 60 characters. Problem messages quote input only after the same cleaning. | `BadTransactionsAreRefused_SayingWhy`, `CounterpartyNamesAreSafeInExplanations`, `RandomTransactionsAndRules_NeverCrash` |
| **Spreadsheet formulas** | CSV cells starting with `=`, `+`, `-` or `@`, or a tab or carriage return, get a leading apostrophe, unless they're plain numbers. Cells with commas, quotes, line breaks or edge spaces are quoted. | `CellsCantRunInASpreadsheet`, `AlertCsvIsSafe` |
| **The command** | Unknown options, repeated options and missing values are refused. `--out` won't overwrite the input file. A rule set that isn't UTF-8 is refused, not half-read. | `MistakesExitWith2_AndSayWhy` |
| **Data leaving the device** | The website runs everything in the browser and sends nothing. Its Content-Security-Policy allows scripts and connections only to its own origin, and the page can't be framed. Names and file names are shown as text, never as HTML. | `site/test/e2e.cjs`: hostile names and file names, no outside requests, no policy violations, framing |
| **Different results on different machines** | Explanations and numbers are formatted the same whatever the machine's language settings. | `ExplanationsDontDependOnTheMachinesCulture` |
| **Supply chain** | The library and command have no dependencies outside .NET. The browser tests use Playwright at an exact version with a lockfile. CI runs `npm audit` and `dotnet list package --vulnerable`, and pins GitHub Actions to commits. | CI |

## What it doesn't do

- **It doesn't decide that anyone did anything wrong.** An alert is a reason to look, for a trained person with the full picture.
- **It catches only what its rules describe, with the data it's given.** Thresholds tuned too loosely bury real alerts. Thresholds tuned too tightly miss them, and so does activity split across accounts the file doesn't link.
- **Its example values aren't regulatory values.** That includes the thresholds, the rates and the high-risk countries.
