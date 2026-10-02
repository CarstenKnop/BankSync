# Owner setup checklist: Enable Banking control panel

These steps can only be done by the owner, because they involve the
owner's email, MitID and the Enable Banking account. Allow about an hour.
Hand the developer only what the last section lists.

## 1. Create the Enable Banking account

1. Go to <https://enablebanking.com/cp> and sign in with your email
   (passwordless link).
2. Accept the terms for the control panel. Read the parts about
   "restricted" production use; they confirm own-account use is allowed
   without a contract.

## 2. Generate the key pair

Do this on the server that will run the app, or on a trusted PC, never on
a shared machine.

```bash
openssl genrsa -out eb_private_key.pem 4096
openssl req -new -x509 -days 3650 -key eb_private_key.pem -out eb_public.crt -subj "/CN=nordea-app"
```

* `eb_private_key.pem` stays with you. It goes into the Docker secret.
  Keep one encrypted offline copy.
* `eb_public.crt` is uploaded to Enable Banking in the next step.
* Alternatively, let the control panel generate the key in the browser and
  download the private key file it offers. Treat that file the same way.

## 3. Register the production application

1. In the control panel choose **Applications → Register new application**.
2. Environment: **Production** (not Sandbox). You may register a second,
   Sandbox application for the developer; it activates automatically.
3. Name: anything, e.g. `Nordea home app`.
4. Upload `eb_public.crt` (or use the in-browser key option).
5. Redirect URLs: add exactly the callback address the app will use, e.g.
   `https://nordea.home.example/callback`. HTTPS, no trailing slash
   differences, no query string. You can add more later.
6. Save. Note the **Application ID** (UUID). This is not secret, but
   combine it only with the key on the server.
7. The production app shows as **pending**. That is expected until the
   next step.

## 4. Link your own accounts (restricted production)

1. In the application's page find the option to activate it in restricted
   mode by linking your own accounts (wording in the control panel:
   "link accounts" / "own accounts").
2. Choose **Nordea**, country **Denmark**, user type **personal**.
3. You are redirected to Nordea; authenticate with **MitID** and approve
   the accounts you want the app to see. Approve every account you intend
   to use; accounts you leave out will be stripped from all API responses
   even if you select them later during the app's own consent.
4. Back in the control panel, confirm the linked accounts are listed and
   the application status is active (restricted).

If the app later shows "no accounts", come back here: the most common
cause is a missing linked account.

## 5. Decide the network setup with the developer

| Question | Your answer |
|---|---|
| Public hostname for the app, e.g. `nordea.home.example` | |
| Reachable from the internet, LAN only, or VPN only? | |
| Who provides TLS (reverse proxy on the server)? | |
| Four daily sync times (default 06:30, 11:30, 16:30, 21:30) | |

The redirect URL in step 3 must be `https://<hostname>/callback`.

## 6. Hand over to the developer

Give the developer:

* The **Application ID** of the production app, and of the sandbox app if
  you registered one.
* The sandbox private key, if you registered a sandbox app (the developer
  may also register their own sandbox app with their own email).
* The hostname and network decisions from step 5.

Do **not** give the developer the production private key. Install it on
the server yourself as the Docker secret file described in the
[developer specification](developer-spec.md#12-docker-deployment). If the
developer must deploy for you, let them do it with a sandbox key first and
swap in the production key yourself.

## 7. First consent in the app

1. Open the app on your phone, log in, tap **Connect Nordea**.
2. Check that the address bar shows `enablebanking.com`, then Nordea and
   MitID. Approve.
3. You land back in the app; the accounts appear and the backfill starts.
   Keep the app open for a few minutes; the first hour after consent is
   when full history is available.
4. Check `/system` after a day: four sync runs per account, no errors.

## 8. Every ~6 months

The app shows a renewal banner 14 days before the consent ends. Tap
**Renew**, approve with MitID, done. The linked accounts in the control
panel do not need to be redone unless you add a new account.

## Revoking access

* In the app: **Disconnect** (calls `DELETE /sessions`).
* In the Enable Banking control panel: delete the application or unlink
  accounts.
* At Nordea: netbank → settings → third-party access (konto-adgang for
  tredjepart), remove Enable Banking.
