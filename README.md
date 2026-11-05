# TxMonitor

**Website: [txmonitor.vercel.app](https://txmonitor.vercel.app)** · runs in your browser; nothing is uploaded.

A transaction monitoring rules engine for anti-money-laundering (AML) alerts, in .NET. You describe rules in JSON and run them over a CSV of transactions. Every alert says what was found, in plain English and with the numbers.

The rules cover:

- deposits kept just under a reporting threshold (structuring);
- money passing straight through an account;
- bursts of activity;
- dormant accounts that wake up;
- many senders or many recipients;
- high-risk countries;
- round amounts;
- large payments to new payees.

**Try it:** the website runs the rules in your browser over made-up sample data or your own file. Nothing is uploaded.

```text
HIGH   2026-08-04 19:12 +07:00  ACC-2002  [pass-through]
       15,000.00 USD came in (1 transaction) and 13,800.00 USD (92%) went out again within 33 hours: money passed
       straight through the account (the rule looks for 80% or more of at least 5,000.00 USD within 48 hours).
       transactions: T003412, T003436, T003464
HIGH   2026-09-17 15:14 +07:00  ACC-2001  [structuring]
       3 deposits by cash within 3 days, each between 8,000.00 USD and the 10,000.00 USD threshold, together
       27,630.00 USD: amounts kept just under a reporting threshold.
       transactions: T004318, T004358, T004400
```

## Install

On NuGet: [TxMonitor](https://www.nuget.org/packages/TxMonitor) · [TxMonitor.Cli](https://www.nuget.org/packages/TxMonitor.Cli). For .NET 8 and .NET 10.

```sh
dotnet add package TxMonitor
dotnet tool install --global TxMonitor.Cli   # the `txmonitor` command
```

## What's in it

| Part | What it does |
|---|---|
| `src/TxMonitor` | The library: rule sets, the engine, the CSV reader and writers, and the sample data. It has no dependencies outside .NET 8 or 10. |
| `src/TxMonitor.Cli` | The `txmonitor` command. |
| `site/TxMonitor.Web` | The website: Blazor WebAssembly, running entirely in the browser. |
| `samples/` | Made-up transactions with planted patterns, and the example rule set. |

## The command

```sh
dotnet run --project src/TxMonitor.Cli -- example-rules --out rules.json   # a starting rule set to tune
dotnet run --project src/TxMonitor.Cli -- sample --out transactions.csv     # made-up data to try it on
dotnet run --project src/TxMonitor.Cli -- run --rules rules.json transactions.csv
dotnet run --project src/TxMonitor.Cli -- run --rules rules.json transactions.csv --format csv --out alerts.csv
dotnet run --project src/TxMonitor.Cli -- check-rules rules.json           # lists every problem at once
dotnet run --project src/TxMonitor.Cli -- rules                            # the rule types and their settings
```

`--format` can be `text`, `csv` or `json`. `--min-severity` can be `low`, `medium` or `high`.

The command exits with 0 when it ran and 2 for bad input. A summary goes to standard error: `5,320 transactions on 49 accounts; 11 alerts (6 high, 3 medium, 2 low).`

## The library

```csharp
using TxMonitor;

var rules = RuleSet.Parse(File.ReadAllText("rules.json"));        // throws MonitorException listing every problem
var transactions = TransactionCsv.Read(File.OpenRead("transactions.csv"));
foreach (var alert in new Engine(rules).Run(transactions))
    Console.WriteLine($"{alert.Severity} {alert.Account} {alert.Rule}: {alert.Explanation}");
```

You can also build `Transaction` records from a database yourself. `Engine.Run` checks them in either case:

- ids are unique;
- each currency has a rate;
- amounts are positive;
- names have no hidden characters.

In a browser, use `TransactionCsv.ReadAsync`.

## Rules

A rule set is JSON. Here is a shortened version of the example:

```jsonc
{
  "baseCurrency": "USD",                                     // rule amounts are in this currency
  "rates": { "KHR": 0.000245, "EUR": 1.08, "THB": 0.028 },   // 1 KHR = 0.000245 USD (made-up rates)
  "highRiskCountries": ["KP", "IR", "MM"],                   // an example: check the current FATF list
  "rules": [
    { "id": "large-cash", "type": "large", "amount": 10000, "channels": ["cash"], "severity": "high" },
    { "id": "structuring", "type": "structuring", "threshold": 10000, "near": 0.8, "count": 3, "days": 7, "severity": "high" },
    { "id": "pass-through", "type": "pass-through", "minimum": 5000, "share": 0.8, "hours": 48, "severity": "high" },
    { "id": "velocity", "type": "velocity", "count": 25, "hours": 24 }
  ]
}
```

Every rule has an `id`, a `type` and a `severity` (`low`, `medium` or `high`; the default is medium). It can also take `channels` (`cash`, `card`, `transfer`, `wire`, `cheque`, `other`) and `direction` (`in` or `out`) to narrow what it looks at.

| Type | Looks for | Settings |
|---|---|---|
| `large` | One transaction at or above an amount. | `amount` |
| `structuring` | Deposits each just under a threshold that together reach it. Cash only unless `channels` says otherwise. | `threshold`, `near` (0.8), `count` (3), `days` (7) |
| `pass-through` | Money in, then most of it out again soon after: a mule or layering pattern. | `minimum`, `share` (0.8), `hours` (48) |
| `velocity` | Many transactions in a short time. | `count`, `hours` |
| `high-risk-country` | A counterparty in a listed country. | `minimum` (0) |
| `round-amounts` | Repeated exact multiples, such as 2,000 and 5,000. The multiple is in the transaction's own currency. | `multiple` (1000), `count` (3), `days` (30), `minimum` |
| `dormant` | Activity after a long quiet spell. | `days`, `minimum` (0) |
| `fan-in` / `fan-out` | Money from many different senders, or to many recipients. | `counterparties`, `days` (7) |
| `new-counterparty` | A large payment to someone the account hasn't paid before. | `minimum` |

Defaults are in brackets. Every setting has a range: for example, `near` must be from 0.5 to 0.999. A rule set with an unknown type, an unknown setting or a value out of range is refused, and every problem is listed.

Rules are data, not code. A rule set can only choose from these types and set numbers. It can't run anything.

### How rules run

- **One account at a time, in time order.** Transactions with the same time are taken in id order. The order of the file doesn't matter.
- **In one pass.** Each windowed rule keeps a queue of the transactions in its window, with running totals. Each transaction enters and leaves a window once, so a run is linear in the number of transactions.
- **After an alert, a rule starts counting afresh** for that account. The same deposits don't raise the same alert again and again.
- **Amounts are converted** to the base currency with the rule set's rates. Round amounts are judged in the transaction's own currency, where roundness means something.
- **Counterparty names are compared the way people read them.** Case, extra spaces and invisible characters don't make a new payee or a new sender.
- **An alert lists up to 100 transaction ids.** Its explanation gives the full count.

## The transactions file

A CSV file needs a header row with these columns, in any order:

- **Required:** `id`, `account`, `time`, `amount`, `currency`, `direction` (`in`/`out`), `channel`.
- **Optional:** `counterparty`, `counterparty_country` (two letters), `description`.

```csv
id,account,time,amount,currency,direction,channel,counterparty,counterparty_country,description
T000001,ACC-1001,2026-03-01T08:14:09+07:00,1835.20,USD,in,transfer,Riverside Garment Co,KH,salary
```

Each time needs a zone: `Z` or an offset such as `+07:00`. A time without one could be any of 24 hours, so it's refused. A date alone means midnight UTC. Amounts are plain numbers, with no sign or thousands separators.

A file that can't be used is refused with the first 20 problems, each with its line number.

## The sample

`Sample.Generate()` (and `txmonitor sample`) produces 5,320 made-up transactions from March to October 2026:

- 40 ordinary accounts with salaries, rent, utilities and card spending;
- nine accounts that each hide one pattern.

The example rules find all nine patterns and nothing else. A test checks this. Every name and number in the sample is invented.

## The website

The website is a Blazor WebAssembly app. Rules and transactions stay in the browser. Its protections:

- **Content-Security-Policy:** scripts only from its own origin, and connections only to its own origin.
- **No framing.**
- **File limit:** files of up to 20 MB.
- **Alert list:** it shows the first 300 alerts.

To build and test it:

```sh
dotnet publish site/TxMonitor.Web -c Release -o out
cd site/test && npm ci && npx playwright install chromium && node e2e.cjs ../../out/wwwroot
```

The browser test serves the site with the production headers from `vercel.json`. It checks:

- the sample run;
- rule and file errors;
- hostile names and file names;
- a file over the limit;
- the layout on a phone;
- framing.

## Tests

`dotnet test` runs 93 tests on .NET 8 and on .NET 10. They cover:

- each rule type at and around its boundaries;
- windows, and starting afresh after an alert;
- currency conversion;
- order independence;
- every way a rule set or a file can be refused;
- spreadsheet-safe CSV;
- the command;
- hostile and random inputs, including 200 rules over 200,000 transactions, and 300,000 transactions in one window.

## Limits

TxMonitor is for learning, testing and prototyping monitoring rules. A real AML programme needs more:

- risk-based rules tuned to its customers and its regulator;
- customer due diligence;
- case management and trained reviewers;
- reporting to the financial intelligence unit;
- record keeping.

The example thresholds and rates are illustrations, not regulatory values. The example's high-risk countries need checking against the current FATF list. Rules see only the transactions they're given: an account's earlier history counts only if it's in the file.

## Security

See [SECURITY.md](SECURITY.md).

## Licence

Apache-2.0 (see [LICENSE](LICENSE)). Written with the help of Claude (Anthropic).
