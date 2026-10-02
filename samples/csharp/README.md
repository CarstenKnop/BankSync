# C# reference sample

A small console program that exercises the Enable Banking calls the app
needs, in the order the app will make them. It compiles with .NET 10 and
is meant to be read and copied from, not shipped.

```
EnableBanking.Sample/
├── JwtFactory.cs           RS256 JWT with kid=application id, cached, max 24 h
├── EnableBankingClient.cs  GET /aspsps, POST /auth, POST /sessions, DELETE /sessions,
│                           GET balances, GET transactions with continuation_key, PSU headers
├── Models.cs               DTOs with the exact JSON property names
└── Program.cs              aspsps | auth | session <code> | transactions <uid> <from> <to>
```

## Run against the sandbox

```bash
set EB_APPLICATION_ID=<sandbox app id>
set EB_PRIVATE_KEY_PATH=C:\path\to\sandbox_private_key.pem
set EB_REDIRECT_URL=https://localhost:5001/callback
cd samples/csharp/EnableBanking.Sample
dotnet run -- aspsps
dotnet run -- auth
```

Open the printed URL, complete the mock or sandbox login, copy `code` from
the callback URL, then:

```bash
dotnet run -- session <code>
dotnet run -- transactions <uid> 2026-01-01 2026-10-01
```

## What the sample deliberately leaves out

* Storage, deduplication, scheduling and quota bookkeeping (see the
  developer specification, sections 8 and 9).
* Retry policy. The client throws `EnableBankingException`; the caller
  decides.
* Encryption of the session id at rest.
