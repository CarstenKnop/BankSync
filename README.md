# Nordea transactions via Enable Banking

Whitepaper, developer specification and privacy/risk summary for a small,
self-hosted C# web app that pulls account transactions from a private
**Nordea Denmark** account through the **Enable Banking** open banking API,
instead of exporting CSV files by hand from the Nordea app.

The documents are written for two readers:

| Reader | Start here |
|---|---|
| The owner (decides, registers the Enable Banking app, gives consent with MitID) | [docs/whitepaper.md](docs/whitepaper.md), then [docs/setup-checklist.md](docs/setup-checklist.md) |
| The developer (builds the C# web app and the Docker image) | [docs/developer-spec.md](docs/developer-spec.md), [docs/api-reference.md](docs/api-reference.md), `samples/` |
| Both | [docs/privacy-and-risks.md](docs/privacy-and-risks.md) |

## Repository layout

```
.
├── README.md
├── mkdocs.yml                 # site definition for GitLab Pages (MkDocs + Material)
├── .gitlab-ci.yml             # builds docs/ into public/ and publishes Pages
├── docs/
│   ├── index.md               # landing page of the published site
│   ├── whitepaper.md          # why, what, options, regulation, flow, constraints
│   ├── developer-spec.md      # the specification the developer builds from
│   ├── api-reference.md       # Enable Banking endpoints used by the app
│   ├── setup-checklist.md     # owner's steps in the Enable Banking control panel
│   └── privacy-and-risks.md   # short summary of privacy and risks
└── samples/
    ├── csharp/                # compilable reference code: JWT + API client (.NET 10)
    └── docker/                # Dockerfile and docker-compose.yml skeleton
```

## Reading the documents

Every document is plain Markdown and readable directly in GitLab or any
editor. Diagrams are written in Mermaid and render in GitLab's Markdown
viewer and in the published Pages site.

## Publishing on GitLab Pages

1. Push this repository to your GitLab server.
2. Make sure GitLab Pages is enabled on the server and a runner with the
   Docker executor is available to the project.
3. The pipeline in `.gitlab-ci.yml` builds the site with the
   `squidfunk/mkdocs-material` image and publishes `public/` on every push
   to the default branch.
4. The site appears at `https://<namespace>.<pages-domain>/<project>/`
   (see **Deploy → Pages** in the project).

### Building the site locally

With Docker:

```bash
docker run --rm -it -p 8000:8000 -v "${PWD}:/docs" squidfunk/mkdocs-material serve -a 0.0.0.0:8000
```

With Python:

```bash
pip install mkdocs-material
mkdocs serve
```

## Building the C# sample

```bash
dotnet build samples/csharp/EnableBanking.Sample
```

The sample is reference code for the developer. It compiles, but it is not
the app; see the developer specification for what the app has to do.

## Status

| Date | Change |
|---|---|
| 2026-10-02 | First version of all documents. |
