# Overview

This site documents how a private **Nordea Denmark** customer can give a
self-hosted app read access to their own account transactions through the
**Enable Banking** open banking API, and what the developer has to build.

## The decision in one paragraph

Nordea's own developer portal only grants production access to companies
holding a PSD2 licence and an eIDAS certificate, so a private person cannot
use it directly. Enable Banking is a licensed account information service
provider (AISP) that offers a free **restricted production** mode: you link
your own accounts in their control panel, your app authenticates with a
private key, and you approve access once with MitID. Your app then receives
balances and transactions as JSON for up to 180 days per consent. It is the
only practical, contract-free and terms-compliant route for a hobby or
household app today. The alternatives are a paid aggregator contract, a
manual CSV export, or scraping, which is both fragile and against Nordea's
terms.

## Documents

| Document | Audience | Purpose |
|---|---|---|
| [Whitepaper](whitepaper.md) | Owner, developer | Options, regulation, how the flow works, constraints and their consequences |
| [Developer specification](developer-spec.md) | Developer | Functional and technical specification of the C# web app and its Docker deployment |
| [Library guide](library.md) | Developer | How to use the BankSync library that implements the Enable Banking side, quota, backfill and storage |
| [API reference](api-reference.md) | Developer | The Enable Banking endpoints, headers, fields and error codes the app uses |
| [Owner setup checklist](setup-checklist.md) | Owner | The steps in the Enable Banking control panel that only the owner can do |
| [Privacy and risks](privacy-and-risks.md) | Owner, developer | Short summary of where data flows, what can go wrong and how to limit it |

## Key facts the whole design rests on

| Fact | Consequence for the app |
|---|---|
| Consent is valid for at most 180 days (Nordea DK) | The app must detect expiry and let the owner renew with MitID from the phone |
| Unattended fetches are limited to 4 per account per day | A fixed four-slot schedule, no polling, and a quota-exempt manual refresh that sends PSU headers |
| Full history is only available for about an hour after consent; later only roughly 90 days | Backfill the whole history immediately after every consent |
| Restricted production returns only accounts linked in the control panel | The owner links accounts first, otherwise the account list is empty |
| Enable Banking does not store account data | Your server is the only place the data lives, so it must be protected and backed up |
| Read-only: payment initiation is not available without a PISP licence | The app can never move money, which removes the worst-case risk |

## Sources

The documents cite Enable Banking's API reference, FAQ and Danish market
notes, the official C# sample, Nordea's open banking support pages and
independent reviews of self-serve PSD2 providers. Links are collected at the
end of the [whitepaper](whitepaper.md#references).
