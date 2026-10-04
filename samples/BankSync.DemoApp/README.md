# BankSync demo app

A minimal ASP.NET Core host that shows how an app uses the library: the
three endpoints (`/connect`, `/callback`, `/refresh`) and the reads.
It has **no login**. Run it on `localhost` only.

## Keeping your data out of git

Everything personal stays outside the repository:

| What | Where it goes | Why it cannot be committed |
|---|---|---|
| Application id, key path, database path | .NET user secrets | Stored in your user profile, not in the repository |
| Private key (`.pem`) | A folder outside the repository | And `*.pem` is git-ignored as a second line of defence |
| Database with transactions | A folder outside the repository | And `*.db`, `*.db-wal`, `*.db-shm`, `banksync.key` are git-ignored |
| Local settings files | `appsettings.Development.json`, `appsettings.Local.json` | `appsettings.*.json` is git-ignored |

`appsettings.json` itself is tracked and deliberately has no slot for the
application id or key path. Do not add them there.

## Run

```bash
dotnet user-secrets set BankSync:ApplicationId <app id>
dotnet user-secrets set BankSync:PrivateKeyPath C:\Users\<you>\secrets\eb_private_key.pem
dotnet user-secrets set BankSync:DatabasePath C:\Users\<you>\banksync-data\banksync.db
dotnet run
```

Open <https://localhost:5001/> and click **Connect**. The redirect URL
`https://localhost:5001/callback` must be registered for the application
in the Enable Banking control panel.

Start with a **sandbox** application: it returns invented accounts and
transactions, so the whole flow can be tested without real data.

## Before you push

```bash
git status --short
```

Nothing under `samples/BankSync.DemoApp` should be listed unless you
changed the code.
