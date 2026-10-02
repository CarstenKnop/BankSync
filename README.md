# BankSync: Nordea transactions via Enable Banking

A C# library plus whitepaper and specification for pulling your own bank
transactions through the [Enable Banking](https://enablebanking.com) open
banking API into a local SQLite database, so a self-hosted app can show
them without manual CSV exports. Written for a private **Nordea Denmark**
customer, usable with any bank Enable Banking supports.

**The library does all the hard parts.** Your app registers it, provides
three endpoints (connect, callback, refresh) and reads from `IBankSync`.
Consent, JWTs, the PSD2 four-calls-per-day quota, history backfill,
deduplication and storage are inside.

```csharp
builder.Services.AddBankSync(builder.Configuration);
// …
var accounts = await bank.GetAccountsAsync();
var page = await bank.GetTransactionsAsync(new TransactionQuery { AccountId = accounts[0].Id, Take = 50 });
```

## Contents

- [Why Enable Banking](#why-enable-banking)
- [What is in this repository](#what-is-in-this-repository)
- [Who reads what](#who-reads-what)
- [Prerequisites](#prerequisites)
- [Quick start: the library](#quick-start-the-library)
- [Quick start: the documentation](#quick-start-the-documentation)
- [Building and testing](#building-and-testing)
- [Publishing on GitLab Pages or GitHub](#publishing-on-gitlab-pages-or-github)
- [Facts the design rests on](#facts-the-design-rests-on)
- [Things to confirm in the sandbox](#things-to-confirm-in-the-sandbox)
- [Next steps](#next-steps)
- [Security notes for this repository](#security-notes-for-this-repository)
- [Contributing and conventions](#contributing-and-conventions)
- [Status](#status)
- [Licence](#licence)

## Why Enable Banking

Nordea exposes its PSD2 account API only to licensed third-party
providers with an eIDAS certificate, which a private person cannot obtain.
Enable Banking (Helsinki, licensed account information service provider)
offers a free **restricted production** mode: you link your own Nordea
accounts in their control panel, your app authenticates with a private
key, and you approve access once with MitID. The app then receives
balances and transactions as JSON for up to 180 days per consent.

It is not the only way in theory, but it is the only route that is free,
contract-free, compliant with Nordea's terms and open to a private person:

| Alternative | Why not |
|---|---|
| Nordea's own developer portal | Production access needs a PSD2 licence and an eIDAS certificate |
| Commercial aggregators (Tink, Plaid, TrueLayer, Mastercard) | Contracts, KYB checks and monthly minimums aimed at companies |
| GoCardless Bank Account Data (ex-Nordigen) | Closed to new sign-ups and being wound down |
| No-code sync products | Data leaves your server, subscription cost |
| Manual CSV export | The status quo; planned as a fallback import |
| Screen-scraping with your MitID | Breaks Nordea's terms and breaks on every UI change |

The full comparison is in the [whitepaper](docs/whitepaper.md#3-options-considered).

## What is in this repository

```
.
├── README.md, LICENSE (MIT)
├── BankSync.slnx              # solution: library, tests, demo app
├── src/
│   ├── BankSync/              # the library (NuGet package "BankSync")
│   └── BankSync.Tests/        # xUnit tests with an in-process fake Enable Banking API
├── samples/
│   ├── BankSync.DemoApp/      # minimal ASP.NET Core host showing the three endpoints and the reads
│   └── docker/                # Dockerfile, docker-compose.yml, .env.example for the owner's server
├── docs/                      # the documentation site (MkDocs + Material)
│   ├── index.md               # landing page
│   ├── whitepaper.md          # options, PSD2 background, end-to-end flow, constraints, roadmap
│   ├── developer-spec.md      # the specification; sections 5-9 are implemented by the library
│   ├── library.md             # how to use BankSync from an app
│   ├── api-reference.md       # Enable Banking endpoints, headers, fields and errors used
│   ├── setup-checklist.md     # owner's steps in the Enable Banking control panel
│   └── privacy-and-risks.md   # short summary of privacy and risks
├── Serve-Docs.ps1             # serves the documentation on localhost (no GitLab needed)
├── mkdocs.yml, .gitlab-ci.yml # docs site and GitLab pipeline (tests + Pages)
└── .github/workflows/ci.yml   # GitHub Actions: build, test, pack on tag, docs check
```

| Document | What it covers |
|---|---|
| [Whitepaper](docs/whitepaper.md) | Problem, requirements, seven options compared, PSD2 terms in plain language, who is responsible for what, the consent-and-sync flow as a sequence diagram, a constraints table with design consequences, Nordea DK specifics, costs, phased roadmap, open points, references |
| [Developer specification](docs/developer-spec.md) | Scope, goals, technology stack, architecture, configuration keys, JWT and client rules, consent state machine and routes, sync engine with the quota model and history backfill, data model, UI pages, security requirements, Docker deployment, tests, acceptance criteria |
| [Library guide](docs/library.md) | Registering BankSync, configuration keys, the three endpoints the app provides, reading data, consent states, diagnostics, error behaviour, sandbox walkthrough, roadmap |
| [API reference](docs/api-reference.md) | The Enable Banking endpoints the library uses with JSON examples, PSU headers, balance types, pagination, error codes, sandbox notes |
| [Owner setup checklist](docs/setup-checklist.md) | Account, key pair, production app registration, linking own accounts, network decisions, hand-over to the developer, first consent, renewal, revocation |
| [Privacy and risks](docs/privacy-and-risks.md) | Data flow, what the app can and cannot do, risk table with mitigations, legal notes, owner's checklist |

## Who reads what

| Reader | Start here |
|---|---|
| Owner (decides, registers the Enable Banking app, gives consent with MitID) | [Whitepaper](docs/whitepaper.md), then [setup checklist](docs/setup-checklist.md) |
| App developer (builds the UI and the Docker image) | [Library guide](docs/library.md), then [developer specification](docs/developer-spec.md) sections 10 to 12, `samples/` |
| Library contributor | [Developer specification](docs/developer-spec.md) sections 5 to 9, [API reference](docs/api-reference.md), `src/` |
| Everyone | [Privacy and risks](docs/privacy-and-risks.md) |

## Prerequisites

| Purpose | Needs |
|---|---|
| Reading the documents | Nothing. Plain Markdown with Mermaid diagrams, which GitLab and GitHub render. |
| Viewing the rendered site locally | PowerShell 5.1 or 7, Python 3.9+ on PATH. `Serve-Docs.ps1` installs the rest into `.venv`. |
| Building the library, tests and demo app | .NET SDK 10.0 |
| Publishing the docs with GitLab Pages | A GitLab server with Pages enabled and a runner using the Docker executor |
| Running an app on the library | Docker on the owner's server, a reverse proxy with TLS, an Enable Banking account |

## Quick start: the library

```bash
git clone <repository-url> banksync
cd banksync
dotnet test BankSync.slnx
```

Then in your own ASP.NET Core project:

```bash
dotnet add reference path/to/src/BankSync/BankSync.csproj   # or: dotnet add package BankSync (once published)
```

```csharp
builder.Services.AddBankSync(o =>
{
    o.ApplicationId  = "<from the Enable Banking control panel>";
    o.PrivateKeyPath = "/run/secrets/eb_private_key";
    o.RedirectUrl    = "https://your.host/callback";
    o.DatabasePath   = "/data/banksync.db";
});
```

The [library guide](docs/library.md) has the three endpoints to add and
every read method. `samples/BankSync.DemoApp` is a runnable version:

```bash
cd samples/BankSync.DemoApp
dotnet user-secrets set BankSync:ApplicationId <sandbox app id>
dotnet user-secrets set BankSync:PrivateKeyPath C:\path\to\sandbox.pem
dotnet run
```

## Quick start: the documentation

```powershell
.\Serve-Docs.ps1
```

The browser opens at <http://127.0.0.1:8000/> with the whitepaper site.

[Serve-Docs.ps1](Serve-Docs.ps1) creates a Python virtual environment in
`.venv` (git-ignored), installs `mkdocs-material` on first run, starts
`mkdocs serve` with live reload and opens the browser.

```powershell
.\Serve-Docs.ps1                 # serve on http://127.0.0.1:8000/ and open the browser
.\Serve-Docs.ps1 -Port 8080      # another port
.\Serve-Docs.ps1 -NoBrowser      # serve only
.\Serve-Docs.ps1 -Build          # write a static copy to .\site\ (open site\index.html)
```

If script execution is blocked:

```powershell
powershell -ExecutionPolicy Bypass -File .\Serve-Docs.ps1
```

Alternatives: `docker run --rm -it -p 8000:8000 -v "${PWD}:/docs" squidfunk/mkdocs-material serve -a 0.0.0.0:8000`,
or `pip install mkdocs-material` followed by `mkdocs serve`, or simply open
the `.md` files in `docs/` in GitLab, GitHub or VS Code.

## Building and testing

```bash
dotnet build BankSync.slnx
dotnet test BankSync.slnx
dotnet pack src/BankSync/BankSync.csproj -c Release -o out
```

The tests use an in-process fake of the Enable Banking API and a fake
clock. They cover the JWT, the consent flow, quota enforcement and day
rollover, backfill resumption across days, PSU header handling,
deduplication, pending-to-booked replacement, pagination, expiry and
listener notifications. No network access is needed.

## Publishing on GitLab Pages or GitHub

**GitLab**: push, enable Pages, make a Docker-executor runner available.
[.gitlab-ci.yml](.gitlab-ci.yml) runs the .NET tests, builds the site in
strict mode and publishes `public/` on the default branch. The site
appears at `https://<namespace>.<pages-domain>/<project>/`.

**GitHub**: [.github/workflows/ci.yml](.github/workflows/ci.yml) builds and
tests on every push and pull request, builds the docs in strict mode, and
publishes them to GitHub Pages on every push to `main`. One-time setup in
the repository: **Settings → Pages → Build and deployment → Source:
GitHub Actions**. The site then appears at
`https://<user>.github.io/<repository>/`. A `v*` tag additionally packs a
NuGet package; publishing to nuget.org needs a `NUGET_API_KEY` secret and
uncommenting one line in the workflow.

## Facts the design rests on

| Fact | Consequence, implemented in the library |
|---|---|
| Consent is valid for at most 180 days (Nordea DK) | `ConsentStatus.NeedsRenewal` 14 days before expiry; renewal keeps account identity and user data |
| Unattended fetches are limited to 4 per account per day | Fixed slots, a per-call counter, no retry loops on HTTP 429; manual refresh sends PSU headers and is exempt |
| Full history is only available for about an hour after consent, afterwards roughly 90 days | Backfill is queued the moment consent completes and reuses the owner's PSU headers for 55 minutes |
| Restricted production returns only accounts linked in the control panel | `ConsentStatus.AccountCount == 0` plus a logged hint |
| Enable Banking does not store account data | SQLite on your server is the only persistent copy, so protect it and back it up encrypted |
| Read-only: no payment initiation without a PISP licence | The library has no payment calls at all |

## Things to confirm in the sandbox

The library and documents are based on Enable Banking's public reference
and FAQ. A few details can only be confirmed against live responses:

- Which of `entry_reference` and `transaction_id` Nordea fills, and
  whether the amount sign already reflects `credit_debit_indicator`. The
  dedup and sign rules cover all cases, but the first real fetch should be
  checked.
- The exact error code strings for an expired session. The library
  matches any 4xx whose code contains `EXPIRED` or `SESSION_CLOSED`.
- Whether each page of a paginated fetch counts separately against the
  quota. The library assumes yes.
- The column names of Nordea's CSV export, for the planned fallback importer.

## Next steps

1. Push to GitLab (or GitHub) and let the pipeline run.
2. Owner: work through the [setup checklist](docs/setup-checklist.md) and
   answer the four open points at the end of the
   [whitepaper](docs/whitepaper.md#11-open-points-for-the-owner).
3. Developer: run the demo app against the Enable Banking sandbox, then
   build the UI described in the developer specification on top of
   `IBankSync`.
4. Owner: install the production private key on the server yourself;
   hand the developer only the application id.

## Security notes for this repository

- Never commit private keys, certificates, `.env` files or database files.
  `.gitignore` blocks `*.pem`, `*.key`, `*.crt`, `*.p12`, `*.pfx`,
  `.env*` (except `.env.example`), `*.db` and `banksync.key`.
- The Enable Banking application ID is not secret, but keep it out of
  public issues together with any hostname.
- Anonymise any real CSV export or API response before adding it to the
  tests or samples.
- If a key is ever committed by accident, revoke it in the Enable Banking
  control panel and register a new certificate; rewriting history is not
  enough.

## Contributing and conventions

- Default branch `main`; work on feature branches and merge via merge or
  pull requests so the tests and the docs build run first.
- Library code: nullable enabled, warnings clean, no network in tests.
  Public API changes need a note in the [library guide](docs/library.md).
- Documents are Markdown with Mermaid diagrams; `mkdocs build --strict`
  must pass.
- Line endings are LF (see `.gitattributes`); `Serve-Docs.ps1` is CRLF.
- Commit messages: imperative subject line, body explaining why.

## Status

| Date | Change |
|---|---|
| 2026-10-02 | First version of all documents, Pages pipeline and local serve script. |
| 2026-10-02 | BankSync library 0.1.0 with tests and demo app; MIT licence; GitHub Actions workflow. |

## Licence

MIT, see [LICENSE](LICENSE). The Enable Banking API, its documentation and
sample code are subject to Enable Banking's own terms.
