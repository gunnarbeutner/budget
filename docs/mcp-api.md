# MCP API (read-only)

A small read-only JSON API under `/api/mcp/v1/` for the MCP gateway that exposes OpenBudget to Claude as a
connector. Each user creates a personal API token in OpenBudget and pastes it into the gateway's link page; the
gateway sends it as `Authorization: Bearer <token>`. A token reads exactly what its user sees in the app — their own
accounts plus the accounts of projects shared with them — and cannot change anything.

There are no bank connections, web logins, receipts, mail or statement downloads in this API, and no writes.

## Configuration

| Key | Default | Purpose |
| --- | --- | --- |
| `McpApi:Enabled` (environment: `McpApi__Enabled`) | `false` | Turns the API and the **Settings → API tokens** page on. While off, every `/api/mcp/` path answers `404` and the settings entry is hidden. |

Entra access tokens (optional, see below):

| Key | Default | Purpose |
| --- | --- | --- |
| `McpApi:Entra:Enabled` (`McpApi__Entra__Enabled`) | `false` | Also accept Microsoft Entra delegated access tokens on `/api/mcp/v1`. |
| `McpApi:AllowedClientId` (`McpApi__AllowedClientId`) | — (required when enabled) | The only client (`azp` / v1 `appid`) whose tokens are accepted: the "MCP Gateway" app, `c406b8b1-3f73-41cd-bf9b-e10e42514418`. |
| `McpApi:Entra:Audiences` (`McpApi__Entra__Audiences`) | the client id and `api://<client id>` | Accepted `aud` values (comma-separated). |
| `Auth:Microsoft:TenantId`, `OPENBUDGET_ONEDRIVE_CLIENT_ID` | — | Tenant and registration, shared with Microsoft sign-in (docs/microsoft-signin.md). |

The app refuses to start when `McpApi:Entra:Enabled` is on without the tenant id, client id and allowed client.

Nothing else is needed for API tokens: they live in the app database (table `api_tokens`, created by the `ApiTokens` migration on
startup).

## Creating a token

1. Sign in to OpenBudget and open **Settings → API tokens** (`/api-tokens`).
2. Enter a name (e.g. "Claude connector"), optionally pick an expiry (30 days, 90 days, one year), and choose
   **Create token**.
3. Copy the token (`obk_…`) right away: it is shown only this once. OpenBudget keeps only its SHA-256 hash and the
   first 12 characters for display.
4. Paste it into the gateway's link page.

The list shows each token's name, prefix, creation date, last use (updated at most once a minute), expiry and
revocation. **Revoke** ends a token's access immediately. Removing a user removes their tokens.

## Authentication

- `Authorization: Bearer obk_…` on every request. Unknown, revoked or expired tokens, a missing header, and the
  login cookie alone all get `401`:

  ```json
  { "error": "unauthorized", "message": "A valid, unrevoked API token is required (Authorization: Bearer <token>)." }
  ```

- Tokens work only under `/api/mcp/v1/`. Every other route ignores them (pages redirect to the login, other API paths
  answer `401`).
- Tokens are never logged.

### Entra access tokens

With `McpApi:Entra:Enabled`, the gateway may instead send a Microsoft Entra delegated access token for OpenBudget's
registration (`Authorization: Bearer eyJ…`; anything not starting with `obk_`). It is accepted when:

- it is signed by the tenant (keys from the tenant's OpenID metadata) and issued by the configured tenant, as a v2
  (`https://login.microsoftonline.com/<tid>/v2.0`) or v1 (`https://sts.windows.net/<tid>/`) token;
- `aud` is one of `McpApi:Entra:Audiences`;
- `scp` contains `mcp.access` (else `403 insufficient_scope`);
- `azp` (v2) or `appid` (v1) equals `McpApi:AllowedClientId` (else `403 client_not_allowed`);
- the token's `tid`/`oid` is linked to an OpenBudget user (else `403 user_not_linked`). Links are made by the user
  (Account page → Link Microsoft account) or by the operator (`Auth:Microsoft:Links`), see docs/microsoft-signin.md.

The request then runs as that user exactly like an API-token request. The log records which method authenticated a
request, never the token.

## Conventions

- JSON, camelCase. Dates are ISO-8601 (`2026-03-10`), timestamps ISO-8601 with offset.
- Money is `{ "amount": -42.50, "currency": "EUR" }` (decimal amount, ISO currency; negative = outgoing).
- Errors: `{ "error": "<code>", "message": "..." }` with `invalid_parameter` (400), `unauthorized` (401),
  `not_found` (404).
- Date parameters (`from`, `to`) take `yyyy-MM-dd` (or a full ISO timestamp, which counts by its UTC date); both
  ends are inclusive.

## Endpoints

### `GET /api/mcp/v1/accounts`

The accounts the user sees (account groups left out). `ownership` is `own` or `shared` (via a shared project).
`type` is one of `checking`, `savings`, `creditCard`, `loan`, `manual`, `cash`, `portfolio`, `other`.

```json
{
  "accounts": [
    { "id": 8589934593, "name": "Giro", "type": "checking", "currency": "EUR", "closed": false, "ownership": "own" },
    { "id": 8589934601, "name": "Joint account", "type": "checking", "currency": "EUR", "closed": false, "ownership": "shared" }
  ]
}
```

### `GET /api/mcp/v1/balances`

Current balance per account as the app shows it: a portfolio is worth its holdings; any other account has the balance
its bank reports, else its initial balance plus its bookings. `refreshedAt` is the last bank sync, if any. `totals`
sums the open accounts per currency.

```json
{
  "balances": [
    {
      "accountId": 8589934593, "name": "Giro", "type": "checking", "closed": false,
      "balance": { "amount": 2412.37, "currency": "EUR" }, "refreshedAt": "2026-11-27T06:12:40+00:00"
    }
  ],
  "totals": [ { "amount": 15890.12, "currency": "EUR" } ]
}
```

### `GET /api/mcp/v1/transactions`

Newest first. Parameters, all optional:

| Parameter | Meaning |
| --- | --- |
| `query` | Text in payee, purpose, comment, note or order items (umlaut-insensitive), or an amount (`42,50`, `-12`) — the app's search box. Max 200 characters. |
| `from`, `to` | Booking date range, inclusive. |
| `accountId` | One account (an id from `/accounts`; an account the user cannot see yields no rows). |
| `category` | A category id, or the name of a category or category group (case-insensitive; a group covers everything below it). An unknown name yields no rows. |
| `limit` | Rows returned, default 50, at most 500 (larger values are capped). |

Split bookings appear as their parts (`splitPart: true`); `pending` marks bookings not yet booked. `totalCount` is the
number of matches before the limit.

```json
{
  "transactions": [
    {
      "id": 8589935012,
      "date": "2026-03-10",
      "valueDate": "2026-03-10",
      "amount": { "amount": -100.00, "currency": "EUR" },
      "payee": "REWE Markt",
      "purpose": "Kartenzahlung 09.03.",
      "comment": null,
      "note": null,
      "category": { "id": 8589934792, "name": "Groceries" },
      "account": { "id": 8589934593, "name": "Giro" },
      "pending": false,
      "splitPart": false
    }
  ],
  "totalCount": 1,
  "limit": 50
}
```

### `GET /api/mcp/v1/spending/summary`

Income and expenses in a period, per currency. Parameters: `from`, `to` (default: the current month up to today;
at most 10 years), `groupBy` = `category` (default) or `month`. Transfers between own accounts (paired bookings and
transfer categories) and split parents are left out; bookings without a category are grouped as `Uncategorized`
(`categoryId: null`). `expenses` is positive; `net` = income − expenses.

```json
{
  "from": "2026-03-01",
  "to": "2026-03-31",
  "groupBy": "category",
  "groups": [
    { "key": "Groceries", "categoryId": 8589934792, "currency": "EUR", "income": 0, "expenses": 412.30, "net": -412.30, "count": 17 },
    { "key": "Uncategorized", "categoryId": null, "currency": "EUR", "income": 3100.00, "expenses": 59.90, "net": 3040.10, "count": 4 }
  ],
  "totals": [ { "currency": "EUR", "income": 3100.00, "expenses": 472.20, "net": 2627.80 } ]
}
```

With `groupBy=month`, `key` is `yyyy-MM` and `categoryId` is null.

### `GET /api/mcp/v1/budgets`

Every visible project that has a budget for the year (`year`, default the current one), with the figures of the
app's Budget view, year to date (January through the current month; the whole year for past years). `role` is the
user's role in the project (`owner`, `editor`, `viewer`). Each line is a tracked category or category group
(`kind`); `actual` is in the line's natural direction (income for income lines, spending for expense lines).

```json
{
  "year": 2026,
  "projects": [
    {
      "projectId": 1, "projectName": "Household", "role": "owner",
      "budgetId": 3, "budgetName": "2026", "year": 2026, "fromMonth": 1, "toMonth": 11,
      "currency": "EUR", "totalBudget": 13200.00, "totalActual": 12104.55, "totalRemaining": 1095.45,
      "lines": [
        { "id": 8589934700, "kind": "group", "name": "Food", "isIncome": false, "budget": 6600.00, "actual": 6012.10, "remaining": 587.90 }
      ]
    }
  ]
}
```
