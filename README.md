# OpenBudget

A self-hosted budgeting and cashflow app. Feed it your accounts and
transactions and you get per-project budgets, year-to-date variance reports,
and forward-looking balance projections.

## Concepts

- **User** — a login. Users are created only from the command line (see
  [Users and sharing](#users-and-sharing)); there is no sign-up page.
- **Source** — one imported data set, owned by one user. Multiple sources can
  coexist (e.g. personal + shared household).
- **Project** — a budget context: a set of accounts and tracked categories
  from one source, owned by one user. Each project gets its own dashboard
  tile. A project can be shared with other users.
- **Budget** — a yearly per-category plan attached to a project. Older
  budgets stay as history.

## The screens

- **Dashboard (`/`)** — project tiles with month / YTD / year totals,
  progress, and warnings.
- **Budgets (`/budgets`)** — yearly variance per project, with a stacked-bar
  trend chart.
- **Budget editor (`/budgets/{id}/edit`)** — monthly amount grid.
- **Wizard (`/budgets/wizard`)** — create a new budget from history or by
  cloning.
- **Cashflow (`/cashflow`)** — projected balance trajectory; flags when
  balance dips below your floor.
- **Project settings (`/projects/{id}`)** — accounts, tracked categories,
  income / expense classification.
- **Sources (`/sources`)** — your own sources, their upload freshness and
  upload tokens.
- **Account (`/Account`)** — change your password, register passkeys, sign out.

## Running it

Local development:

```bash
dotnet run --project src/OpenBudget
```

Production via Docker:

```bash
echo "OPENBUDGET_UPLOAD_TOKEN=$(openssl rand -hex 32)" > .env
docker compose up -d
```

The container speaks plain HTTP on `:8080` and expects a TLS-terminating
reverse proxy in front (it trusts `X-Forwarded-Proto`/`-For`/`-Host`). The web
UI requires a login; the upload API stays token-authenticated.

## Users and sharing

### First deploy

Until the first user exists, every page shows only a setup message and nothing
else. Create the first admin inside the running container:

```bash
docker exec openbudget dotnet OpenBudget.dll users add <username> --admin
```

Without a terminal (plain `docker exec`) a random initial password is generated
and **printed once**. Add `-it` to be prompted for a password instead, or pipe
one in: `echo 'secret' | docker exec -i openbudget dotnet OpenBudget.dll users
add <username> --password-stdin`. Change it afterwards on the account page.

Existing data is handed over by name: when a user is created, a source whose
name equals the username and that has no owner yet is assigned to that user,
together with that source's projects that have no owner yet. So with sources
`alice` and `bob`, `users add alice --admin` takes source `alice` and its
projects and `users add bob` takes source `bob` and its projects. Anything
that already has an owner is never touched, and sources/projects without an
owner are invisible to everybody (admins included) until claimed.
(`users claim <name>` repeats the claiming step for an existing user, e.g. for
a source that was first uploaded after the user was created.)

### User commands

```bash
docker exec openbudget dotnet OpenBudget.dll users add <name> [--admin] [--password-stdin | --generate-password]
docker exec openbudget dotnet OpenBudget.dll users list
docker exec openbudget dotnet OpenBudget.dll users set-password <name> [--password-stdin | --generate-password]
docker exec openbudget dotnet OpenBudget.dll users remove <name> --yes
docker exec openbudget dotnet OpenBudget.dll users claim <name>
```

Usernames are lowercase letters, digits and hyphens. `remove` deletes the login
and its shares; its sources and projects stay in the database but become
unowned (invisible until a user of the same name claims them). Passwords use
ASP.NET Core Identity's defaults (PBKDF2 hashing, password rules, lockout after
five failed attempts). `--admin` only marks a user as an administrator; it
grants no access to other users' data.

### Sessions and cookies

The auth cookie is valid for 30 days and renewed while in use; it is `Secure`,
`HttpOnly` and `SameSite=Lax`. Data-protection keys (which protect the cookie
and the antiforgery tokens) are stored in the data volume at
`/app/data/keys`, so redeploying does not log anyone out — **keep `/app/data`
on a persistent volume and do not share its `keys` folder**.

### Passkeys

After logging in, open the account page and add a passkey (Face ID / Touch ID
via the platform authenticator, or a security key). The login page then offers
"Sign in with a passkey"; the password keeps working as a fallback. Passkeys use
ASP.NET Core Identity's built-in WebAuthn support. The relying-party id
defaults to the request host (which therefore must be the public host name:
make sure the proxy passes `X-Forwarded-Host`, or `ProxyPreserveHost On` in
Apache). To pin it, set `OPENBUDGET_RP_ID` (for example
`budget.example.org`); the passkey origin must then be that host or a subdomain
of it, over https. Passkeys are bound to the relying-party id: changing it
invalidates registered passkeys.

### Ownership and sharing

- Every source and project has an owner; users create their own sources
  (Sources page) and projects (dashboard). Accounts and transactions belong to
  the owner of their source.
- The owner can share a project on its **Settings** page with another user as
  **viewer** (Overview, Budgets, Browse, Cashflow, Rebalance, last upload
  time) or **editor** (additionally budgets, category assignments, tracking and
  income/expense settings). Which accounts belong to a project, sharing itself,
  the parent project and deleting the project stay with the owner. Sub-projects
  inherit their parent's shares.
- Someone a project is shared with sees only that project's assigned accounts
  and their transactions - never the rest of the owner's source. Anything outside
  a user's scope behaves like a page that does not exist.
- Source pages (upload token, depot and instrument mapping, tax settings,
  deleting a source) are owner-only.

### Upload tokens

Every source has its own upload token (Sources page; the owner can show and
rotate it). It authorizes uploads to that source only. The legacy global token
`OPENBUDGET_UPLOAD_TOKEN` (or the deprecated `MONEYMONEY_DB_UPLOAD_TOKEN`) keeps
working for every source, so already installed exporters need no change; each
upload lands in the source named in its URL. Sources created after the upgrade
should use their own token only.

## Importers

Data enters through importers. Each lives in its own folder under
`src/OpenBudget/Importers/` and is the only code that knows its source
application's name and file format.

### MoneyMoney

The `exporter/` directory ships a small macOS helper ("OpenBudget Importer")
that decrypts the [MoneyMoney](https://moneymoney-app.com) database and uploads
it to the app. Build it with `exporter/build.sh` and run it on the same Mac that
runs MoneyMoney; the bundle needs Full Disk Access to reach MoneyMoney's
container.

Uploads are the raw SQLite file as the request body with
`Authorization: Bearer <token>` — the source's own upload token or the legacy
global `OPENBUDGET_UPLOAD_TOKEN`:

- `POST /api/import/moneymoney/{source}` — MoneyMoney snapshot
- `POST /api/portfolioperformance/{source}` — PortfolioPerformance file
- `DELETE /api/sources/{source}` — remove a source

### Legacy names (compatibility)

Deployments and the previously installed `MoneyMoneyExport` tool predate the
rename. Until that tool is retired the server still accepts
`MONEYMONEY_DB_UPLOAD_TOKEN` (deprecated; logs a warning), `POST
/api/moneymoney-db/{source}` and `DELETE /api/moneymoney-sources/{source}`.
These shims live in `src/OpenBudget/Compat/` and the importer's
`MoneyMoneyEndpoints.cs`.

## Localization

English and German. UI follows the browser's `Accept-Language`.

## Tech stack

.NET 11 (preview) / Blazor Server, ASP.NET Core Identity (passwords + passkeys), Entity Framework Core + SQLite, Bootstrap 5.3,
Bootstrap Icons, Chart.js. Theme follows the OS `prefers-color-scheme`.
