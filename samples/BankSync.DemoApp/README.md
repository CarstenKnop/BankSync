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

## First test, step by step (sandbox)

Use a **sandbox** application first. It returns invented accounts and
transactions, so no real data is involved anywhere.

1. **Create the key pair** in a folder outside the repository, for example
   `C:\Users\<you>\secrets\`:

   ```bash
   openssl genrsa -out sandbox.pem 2048
   openssl req -new -x509 -days 365 -key sandbox.pem -out sandbox.crt -subj "/CN=banksync-sandbox"
   ```

   Alternatively let the control panel generate the key in the browser and
   save the downloaded `.pem` there.
2. **Register the application** at <https://enablebanking.com/cp>:
   environment **Sandbox**, upload `sandbox.crt`, and add the redirect URL
   `https://localhost:5001/callback`. Note the application id.
3. **Set the three user secrets** from the demo folder:

   ```bash
   cd samples/BankSync.DemoApp
   dotnet user-secrets set BankSync:ApplicationId <sandbox application id>
   dotnet user-secrets set BankSync:PrivateKeyPath C:\Users\<you>\secrets\sandbox.pem
   dotnet user-secrets set BankSync:DatabasePath C:\Users\<you>\banksync-data\banksync.db
   ```

4. **Pick the bank.** The demo defaults to Nordea Denmark. If your sandbox
   application lists a different bank (for example a mock bank), set it to
   match:

   ```bash
   dotnet user-secrets set BankSync:AspspName "<name as listed>"
   dotnet user-secrets set BankSync:AspspCountry <country code>
   ```

5. **Start it:** `dotnet run`. The browser opens <https://localhost:5001/>.
   The HTTPS development certificate must be trusted
   (`dotnet dev-certs https --trust`, once).
6. **Click Connect.** You are sent to the sandbox bank. Log in with the test
   credentials from Enable Banking's "Sandbox credentials" documentation.
   Approve, and you land back on the demo.
7. **Check the page.** You should see the consent as Active with days left,
   the accounts, balances and transactions, today's quota and the first
   sync runs. Leave it open a minute; the history backfill runs in the
   background.
8. **Try Refresh now,** then stop the app with Ctrl+C. Start it again: the
   data is still there, read from the database file.

### If something fails

| Symptom | Likely cause |
|---|---|
| The app stops at start with "ApplicationId is required" | A user secret is missing; run `dotnet user-secrets list` |
| The control panel rejects the redirect URL | It may not accept `localhost`; note the exact message, the fix is a tunnel or a real hostname |
| After Connect you get "Unknown state parameter" | The consent took longer than 15 minutes, or the app was restarted with a different database; click Connect again |
| Consent is Active but there are no accounts | The sandbox application has no accounts for that bank; check the bank name and country |
| 401 in the sync history | Wrong application id or key, or the key does not match the uploaded certificate |
| 403 in the sync history | The application is not active in this environment |
| 429 in the sync history | The daily quota is used up; the demo waits for the next slot, which is normal |

The demo logs at Debug level for BankSync. Errors show in the console and in
the "Last sync runs" list on the page.

## Real accounts later

Switch to a **production** application only after the sandbox works. The
production key is installed by the owner, not the developer; follow the
[owner setup checklist](../../docs/setup-checklist.md), including linking
your accounts in the control panel. The same user secrets are used with the
production application id and key.

## Before you push

```bash
git status --short
```

Nothing under `samples/BankSync.DemoApp` should be listed unless you
changed the code.
