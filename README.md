# Nordea transactions via Enable Banking

Whitepaper, developer specification, privacy/risk summary and reference
code for a small, self-hosted **C# web app** that pulls account
transactions from a private **Nordea Denmark** account through the
**Enable Banking** open banking API, instead of exporting CSV files by
hand from the Nordea app.

The app runs in one Docker container on the owner's private server and is
used from a mobile phone browser.

## Contents

- [Why Enable Banking](#why-enable-banking)
- [What is in this repository](#what-is-in-this-repository)
- [Who reads what](#who-reads-what)
- [Prerequisites](#prerequisites)
- [Quick start](#quick-start)
- [Viewing the documentation](#viewing-the-documentation)
- [Building the C# sample](#building-the-c-sample)
- [Publishing on GitLab Pages](#publishing-on-gitlab-pages)
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
| Manual CSV export | The status quo; kept as a fallback import in the app |
| Screen-scraping with your MitID | Breaks Nordea's terms and breaks on every UI change |

The full comparison is in the [whitepaper](docs/whitepaper.md#3-options-considered).

## What is in this repository

```
.
├── README.md                  # this file
├── Serve-Docs.ps1             # serves the documentation on localhost (no GitLab needed)
├── mkdocs.yml                 # site definition for GitLab Pages (MkDocs + Material)
├── .gitlab-ci.yml             # builds docs/ into public/ and publishes Pages
├── .gitattributes, .gitignore # LF line endings; keys, venv and build output never committed
├── docs/
│   ├── index.md               # landing page of the published site
│   ├── whitepaper.md          # options, PSD2 background, end-to-end flow, constraints, roadmap
│   ├── developer-spec.md      # the specification the developer builds from
│   ├── api-reference.md       # Enable Banking endpoints, headers, fields and errors used by the app
│   ├── setup-checklist.md     # owner's steps in the Enable Banking control panel
│   └── privacy-and-risks.md   # short summary of privacy and risks
└── samples/
    ├── csharp/                # compilable .NET 10 reference client: JWT factory, typed client, DTOs
    └── docker/                # Dockerfile, docker-compose.yml and .env.example skeleton
```

| Document | What it covers |
|---|---|
| [Whitepaper](docs/whitepaper.md) | Problem, requirements, seven options compared, PSD2 terms in plain language, who is responsible for what, the consent-and-sync flow as a sequence diagram, a constraints table with design consequences, Nordea DK specifics, costs, phased roadmap, open points, references |
| [Developer specification](docs/developer-spec.md) | Scope, goals, technology stack, architecture, configuration keys, JWT and client rules, consent state machine and routes, sync engine with the four-per-day quota model and history backfill, data model, UI pages, security requirements, Docker deployment, observability, tests, ten acceptance criteria |
| [API reference](docs/api-reference.md) | The Enable Banking endpoints the app uses with JSON examples, PSU headers, balance types, pagination, error codes, sandbox notes |
| [Owner setup checklist](docs/setup-checklist.md) | Account, key pair, production app registration, linking own accounts, network decisions, hand-over to the developer, first consent, renewal, revocation |
| [Privacy and risks](docs/privacy-and-risks.md) | Data flow, what the app can and cannot do, risk table with mitigations, legal notes, owner's checklist |
| [C# sample](samples/csharp/README.md) | Console walkthrough of `/aspsps`, `/auth`, `/sessions` and transactions with pagination |

## Who reads what

| Reader | Start here |
|---|---|
| Owner (decides, registers the Enable Banking app, gives consent with MitID) | [Whitepaper](docs/whitepaper.md), then [setup checklist](docs/setup-checklist.md) |
| Developer (builds the C# web app and the Docker image) | [Developer specification](docs/developer-spec.md), [API reference](docs/api-reference.md), `samples/` |
| Both | [Privacy and risks](docs/privacy-and-risks.md) |

## Prerequisites

| Purpose | Needs |
|---|---|
| Reading the documents | Nothing. They are plain Markdown with Mermaid diagrams, which GitLab renders. |
| Viewing the rendered site locally | Windows PowerShell 5.1 or PowerShell 7, Python 3.9+ on PATH. `Serve-Docs.ps1` installs the rest into `.venv`. |
| Building the C# sample | .NET SDK 10.0 (8.0 works if you change `TargetFramework`). |
| Publishing with GitLab Pages | A GitLab server with Pages enabled and a runner using the Docker executor. |
| Running the app (once built) | Docker on the owner's server, a reverse proxy with TLS, an Enable Banking account. |

## Quick start

```powershell
git clone <your-gitlab-url>/nordea.git
cd nordea
.\Serve-Docs.ps1
```

The browser opens at <http://127.0.0.1:8000/> with the whitepaper site.

## Viewing the documentation

### On localhost with the script

[Serve-Docs.ps1](Serve-Docs.ps1) creates a Python virtual environment in
`.venv` (git-ignored), installs `mkdocs-material` on first run, starts
`mkdocs serve` with live reload and opens the browser.

```powershell
.\Serve-Docs.ps1                 # serve on http://127.0.0.1:8000/ and open the browser
.\Serve-Docs.ps1 -Port 8080      # another port
.\Serve-Docs.ps1 -NoBrowser      # serve only
.\Serve-Docs.ps1 -Build          # write a static copy to .\site\ (open site\index.html)
```

If script execution is blocked, run it once with:

```powershell
powershell -ExecutionPolicy Bypass -File .\Serve-Docs.ps1
```

### With Docker

```bash
docker run --rm -it -p 8000:8000 -v "${PWD}:/docs" squidfunk/mkdocs-material serve -a 0.0.0.0:8000
```

### With Python directly

```bash
pip install mkdocs-material
mkdocs serve
```

### Without any tooling

Open the `.md` files in `docs/` in GitLab, VS Code or any Markdown
viewer. Mermaid diagrams render in GitLab and in VS Code with a Mermaid
extension.

## Building the C# sample

```bash
dotnet build samples/csharp/EnableBanking.Sample
```

The sample is reference code for the developer. It compiles with zero
warnings, but it is not the app: it has no storage, scheduler or quota
bookkeeping. See [samples/csharp/README.md](samples/csharp/README.md) for
running it against the Enable Banking sandbox.

## Publishing on GitLab Pages

1. Push this repository to your GitLab server.
2. Make sure GitLab Pages is enabled on the server and a runner with the
   Docker executor is available to the project.
3. The pipeline in [.gitlab-ci.yml](.gitlab-ci.yml) builds the site with
   the `squidfunk/mkdocs-material` image in strict mode and publishes
   `public/` on every push to the default branch. Merge requests and other
   branches get a build-only job that fails on broken links.
4. The site appears at `https://<namespace>.<pages-domain>/<project>/`
   (see **Deploy → Pages** in the project).

## Facts the design rests on

| Fact | Consequence for the app |
|---|---|
| Consent is valid for at most 180 days (Nordea DK) | Detect expiry, show a renewal banner 14 days before, renew with MitID from the phone |
| Unattended fetches are limited to 4 per account per day | Fixed four-slot schedule, no polling, no retry loops on HTTP 429; manual refresh sends PSU headers and is exempt |
| Full history is only available for about an hour after consent, afterwards roughly 90 days | Backfill the whole history immediately after every consent |
| Restricted production returns only accounts linked in the control panel | The owner links accounts first, otherwise the account list is empty |
| Enable Banking does not store account data | Your server is the only persistent copy, so protect it and back it up encrypted |
| Read-only: no payment initiation without a PISP licence | The app can never move money |

## Things to confirm in the sandbox

The documents are based on Enable Banking's public reference and FAQ. A
few details can only be confirmed against live responses and are marked
in the documents:

- Which of `entry_reference` and `transaction_id` Nordea fills, and
  whether the amount sign already reflects `credit_debit_indicator`.
- The exact error code strings (`EXPIRED_SESSION` and friends); match on
  HTTP status first.
- Whether each page of a paginated transactions fetch counts separately
  against the daily quota (the spec assumes yes).
- The column names of Nordea's CSV export, for the fallback importer.

## Next steps

1. Push to GitLab and enable Pages.
2. Owner: work through the [setup checklist](docs/setup-checklist.md) and
   answer the four open points at the end of the
   [whitepaper](docs/whitepaper.md#11-open-points-for-the-owner). The
   redirect URL decision drives the network and TLS setup.
3. Owner: hand the developer the production application ID, but install
   the production private key on the server yourself.
4. Developer: run the C# sample against the sandbox, then build phase 2 of
   the roadmap in the whitepaper.

## Security notes for this repository

- Never commit private keys, certificates or `.env` files. `.gitignore`
  blocks `*.pem`, `*.key`, `*.crt`, `*.p12`, `*.pfx` and `.env*`
  (except `.env.example`).
- The Enable Banking application ID is not secret, but keep it out of
  public issues together with any hostname.
- Anonymise any real CSV export before adding it under `samples/`.
- If a key is ever committed by accident, revoke it in the Enable Banking
  control panel and register a new certificate; rewriting history is not
  enough.

## Contributing and conventions

- Default branch `main`; work on feature branches and merge via merge
  requests so the Pages build runs first.
- Documents are Markdown with Mermaid diagrams, one sentence per line is
  not required, but keep lines under about 80 characters.
- `mkdocs build --strict` must pass; the pipeline fails on broken links
  or missing pages.
- Line endings are LF (see `.gitattributes`); `Serve-Docs.ps1` is CRLF.
- Commit messages: imperative subject line, body explaining why.

## Status

| Date | Change |
|---|---|
| 2026-10-02 | First version of all documents, sample code, Pages pipeline and local serve script. |

## Licence

Private repository for personal use. No licence is granted for
redistribution. The Enable Banking API, its documentation and sample code
are subject to Enable Banking's own terms.
