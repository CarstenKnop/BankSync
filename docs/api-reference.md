# Enable Banking API: the parts this app uses

This is a working summary for the developer. The authoritative source is
<https://enablebanking.com/docs/api/reference/>, which must be checked for
exact schemas before coding. Field names below are copied from that
reference.

## Base URL and authentication

| Item | Value |
|---|---|
| Base URL | `https://api.enablebanking.com` (`api.tilisy.com` is deprecated) |
| Auth header | `Authorization: Bearer <JWT>` |
| JWT header | `{"typ":"JWT","alg":"RS256","kid":"<application_id>"}` |
| JWT claims | `iss`=`enablebanking.com`, `aud`=`api.enablebanking.com`, `iat`, `exp` (max `iat`+86400) |
| Key | RSA private key matching the certificate uploaded in the control panel; `openssl genrsa -out private.key 4096` |
| Content type | `application/json` |

## PSU headers (data endpoints only)

Send **all or none**. Presence means "the user is online", which exempts
the call from the 4-per-day unattended limit.

| Header | Value |
|---|---|
| `Psu-Ip-Address` | Client IP of the owner's current request (from `X-Forwarded-For`) |
| `Psu-User-Agent` | Browser user agent of the owner's request |
| `Psu-Accept-Language`, `Psu-Accept`, `Psu-Accept-Charset`, `Psu-Accept-Encoding`, `Psu-Referer`, `Psu-Geo-Location` | Optional extras; include what the request provides |

Sending an incomplete set returns error `PSU_HEADER_NOT_PROVIDED`. The ASPSP
record's `required_psu_headers` lists what Nordea requires.

## Endpoints

### `GET /application`

Returns the application record (name, environment, redirect URLs, status).
Use at startup to verify the key and app id and to show the environment on
the `/system` page.

### `GET /aspsps`

Query: `country=DK` (optional filter), `psu_type=personal`.
Response: array of ASPSP objects.

```json
{
  "name": "Nordea",
  "country": "DK",
  "logo": "https://...",
  "psu_types": ["personal", "business"],
  "auth_methods": [ { "name": "...", "title": "...", "psu_type": "personal", "credentials": [], "approach": "REDIRECT", "hidden_method": false } ],
  "maximum_consent_validity": 15552000,
  "beta": false,
  "bic": "NDEADKKK",
  "required_psu_headers": ["psu-ip-address", "psu-user-agent"],
  "sandbox": { "users": [] }
}
```

`maximum_consent_validity` is in seconds (15552000 = 180 days). Cache for
24 hours. Values shown for `auth_methods`, `bic` and `required_psu_headers`
are illustrative; read them from the live response.

### `POST /auth`

Request (`StartAuthorizationRequest`):

```json
{
  "access": {
    "valid_until": "2027-03-31T00:00:00Z",
    "balances": true,
    "transactions": true
  },
  "aspsp": { "name": "Nordea", "country": "DK" },
  "state": "<random, base64url, stored with the pending authorisation>",
  "redirect_url": "https://nordea.home.example/callback",
  "psu_type": "personal",
  "language": "da"
}
```

Optional: `psu_id`, `auth_method`, `credentials`, `credentials_autosubmit`,
`access.accounts` (restrict to specific IBANs).

Response (`StartAuthorizationResponse`):

```json
{ "url": "https://...enablebanking.com/...", "authorization_id": "..." }
```

Redirect the browser to `url`. After authentication the browser lands on
`redirect_url?code=<code>&state=<state>` or
`redirect_url?error=<code>&error_description=<text>&state=<state>`.

### `POST /sessions`

Request: `{ "code": "<code from callback>" }` (single use).

Response (`AuthorizeSessionResponse`), abbreviated:

```json
{
  "session_id": "4f1e...",
  "accounts": [
    {
      "uid": "9b2c...",
      "account_id": { "iban": "DK5000400440116243", "other": { "identification": "0040-0440116243", "scheme_name": "BBAN" } },
      "all_account_ids": [ { "identification": "DK5000400440116243", "scheme_name": "IBAN" }, { "identification": "0040-0440116243", "scheme_name": "BBAN" } ],
      "account_servicer": { "bic_fi": "NDEADKKK", "name": "Nordea" },
      "name": "Lønkonto",
      "product": "...",
      "currency": "DKK",
      "cash_account_type": "CACC",
      "usage": "PRIV",
      "psu_status": "...",
      "identification_hash": "sha256:...",
      "identification_hashes": ["sha256:..."]
    }
  ],
  "aspsp": { "name": "Nordea", "country": "DK" },
  "psu_type": "personal",
  "access": { "valid_until": "2027-03-31T00:00:00Z", "balances": true, "transactions": true }
}
```

The IBAN/BBAN values are the ISO example format, not real accounts. Store
`uid` per consent and `identification_hash` per account.

### `GET /sessions/{session_id}`

Response (`GetSessionResponse`): `status` (`AUTHORIZED`, `CLOSED`,
`EXPIRED`, …), `accounts` (uids), `accounts_data`, `aspsp`, `psu_type`,
`access`, `authorized`, `created`. Use to re-validate a session before
offering "Renew" (a still-valid session does not need a new MitID round).

### `DELETE /sessions/{session_id}`

Closes the session and revokes the consent at the bank. Used by
"Disconnect".

### `GET /accounts/{uid}/details`

Response (`AccountResource`): the same account object as in the session
response, plus `credit_limit`, `postal_address`, `legal_age`, `details`.
Fetch once per consent.

### `GET /accounts/{uid}/balances`

Response (`HalBalances`):

```json
{
  "balances": [
    {
      "name": "Booked",
      "balance_amount": { "currency": "DKK", "amount": "12345.67" },
      "balance_type": "CLBD",
      "last_change_date_time": "2026-10-01T22:00:00Z",
      "reference_date": "2026-10-01",
      "last_committed_transaction": "..."
    },
    { "name": "Available", "balance_amount": { "currency": "DKK", "amount": "12245.67" }, "balance_type": "ITAV" }
  ]
}
```

`balance_type` values: `CLAV`, `CLBD`, `FWAV`, `INFO`, `ITAV`, `ITBD`,
`OPAV`, `OPBD`, `OTHR`, `PRCD`, `VALU`, `XPCD`. Banks differ: **Nordea
returns `ITBD` (booked), `ITAV` (available) and `VALU`, and no `CLBD`.**
The BankSync library hides this behind `Booked()` and `Available()`.
Amounts are strings; parse with `InvariantCulture`.

### `GET /accounts/{uid}/transactions`

Query:

| Parameter | Meaning |
|---|---|
| `date_from` | `YYYY-MM-DD`, inclusive, UTC |
| `date_to` | `YYYY-MM-DD`, inclusive, UTC |
| `continuation_key` | From the previous page; omit for the first page |
| `transaction_status` | Optional filter (`BOOK`, `PEND`, …) |
| `strategy` | Optional fetch strategy; leave unset unless the reference recommends one for Nordea |

Response (`HalTransactions`):

```json
{
  "transactions": [
    {
      "entry_reference": "2026100112345",
      "transaction_id": null,
      "transaction_amount": { "currency": "DKK", "amount": "-249.00" },
      "credit_debit_indicator": "DBIT",
      "status": "BOOK",
      "booking_date": "2026-10-01",
      "value_date": "2026-10-01",
      "transaction_date": "2026-09-30",
      "creditor": { "name": "NETTO 1234" },
      "creditor_account": null,
      "debtor": null,
      "debtor_account": { "iban": "DK5000400440116243" },
      "bank_transaction_code": { "code": "PMNT", "sub_code": "CCRD", "description": "Card payment" },
      "remittance_information": ["Dankort-nota NETTO 1234 30.09"],
      "balance_after_transaction": { "currency": "DKK", "amount": "12096.67" },
      "merchant_category_code": "5411",
      "reference_number": null,
      "note": null
    }
  ],
  "continuation_key": "eyJ..."
}
```

Notes:

* Which of `entry_reference` and `transaction_id` Nordea fills must be
  confirmed in the sandbox and on the first production fetch; the dedup
  rules in the spec cover all cases.
* `credit_debit_indicator` is `CRDT` or `DBIT`; the amount sign may or may
  not already reflect it. Normalise: store `Amount` negative for `DBIT`.
* `remittance_information` is an array of strings; join with a newline.
* Pending (`PEND`) items can disappear or reappear as `BOOK` with a
  different reference.

### `GET /accounts/{uid}/transactions/{transaction_id}`

Detail for a single transaction when `transaction_id` is present. Not
needed for the sync; optional for the detail page.

## Errors

| HTTP | Typical `error` values | Meaning |
|---|---|---|
| 400/422 | `VALIDATION_ERROR`, `PSU_HEADER_NOT_PROVIDED`, `EXPIRED_SESSION`, `SESSION_CLOSED`, `WRONG_REQUEST_PARAMETERS` | Fix the request, or re-consent for expired sessions |
| 401 | `UNAUTHORIZED` | Bad JWT: wrong key, wrong `kid`, expired, `exp` too far out |
| 403 | `FORBIDDEN`, `ACCESS_DENIED` | App not active in this environment, or account not linked |
| 429 | `TOO_MANY_REQUESTS` | 4-per-day unattended limit or general throttling. Wait for the next slot. |
| 5xx | `ASPSP_ERROR`, `INTERNAL_ERROR` | Bank or Enable Banking problem; one retry, then next slot |

Error body shape: `{ "error": "<CODE>", "message": "<text>", ... }`.
Exact code strings must be verified against the reference; match on HTTP
status first and on code second.

## Sandbox

* Sandbox applications are activated automatically and have a
  `Mock ASPSP` plus bank sandboxes. Sandbox credentials are listed in the
  Enable Banking docs under "Sandbox credentials".
* The sandbox and production are separate applications with separate keys
  and ids; the app's configuration must make the environment visible.
