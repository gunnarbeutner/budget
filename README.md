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

The image includes Chromium (Debian trixie's package) for web logins, see
[Web logins](#web-logins-signing-in-on-the-banks-own-page); it adds roughly
400 MB over a plain ASP.NET image and is only started while someone signs in
to such a bank. Set `CHROME_PATH` to use another Chromium; without one
everything else keeps working and web-login banks report "browser not
available".

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
- Each user can mark one of their own, unshared projects as the **default
  project for new accounts** (Settings page, shown as a badge on the dashboard).
  Accounts that newly appear in the user's sources - a new account in a MoneyMoney
  upload, or one created by a bank connector - are assigned to it automatically;
  existing assignments are never changed. Sharing the default project clears the
  default (with a notice), so a new account never becomes visible to a sharee on
  its own. A one-time button on the same card assigns all of the owner's accounts
  that are in no project yet.
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

## Bank connectors

Besides MoneyMoney uploads, OpenBudget can talk to banks directly through **connectors** (one per bank, registered by a stable type string). The framework is bank independent; it is proven end to end with a fake bank; real banks are separate connectors (currently Scalable Capital).

### Secrets key (required)

Bank credentials and sessions are stored AES-256-GCM encrypted. The 256-bit key comes from the environment variable `OPENBUDGET_SECRETS_KEY` (base64) and is deliberately **not** stored in the database or the data volume, so a copy of `data/` alone reveals no credentials.

```sh
openssl rand -base64 32      # generate once, keep it safe (a password manager, your compose secrets)
```

Set it where the container is configured (`docker-compose.yml` `environment:`, `docker run -e`, or the deployment's secret store) and keep using the same value: with a different key stored credentials cannot be decrypted, and the login shows "re-enter credentials" (nothing crashes). Without the variable the app runs normally, but the Bank logins page is disabled and says why.

### Bank logins, syncing and challenges

- A bank login belongs to one user and is invisible to everyone else (including shared-project viewers); only its owner can see, edit, sync or delete it and answer its challenges.
- There is **no background fetching**. A sync runs only while its owner has OpenBudget open: automatically when a page loads and the login is due, on "Sync now" (per login) and on "Sync all". Syncs run server-side as one job per login, so navigating away does not stop them.
- Refresh interval per login: every two hours (default), four times a day, once a day, once a week, only manually (never automatic, but included in "Sync all"), never (not even in "Sync all"; only its own "Sync now").
- When the bank wants the user (choose a TAN method, enter a code, look at an image, approve in an app) the owner gets a dialog on whatever page is open. Approvals are polled every few seconds. An unanswered challenge expires after five minutes and the sync fails with a clear message. Credentials the bank rejects stop automatic syncs until they are re-entered.
- Remote accounts are linked to an existing account (suggested by IBAN, typically a MoneyMoney account) or get a new account in a dedicated source `bank-<id>` (kept apart because a MoneyMoney upload replaces every account of the source it targets). Linking switches the account's `provider` to the connector; the MoneyMoney import then leaves its transactions, balance, holdings and refresh state alone. Unlinking switches a taken-over account back to MoneyMoney (the next upload refills it); a connector-created account becomes "manual" and keeps its data.
- The first sync after linking starts at the day of the account's newest *booked* transaction; same-day rows are matched against existing ones by amount + purpose (+ name) instead of being duplicated, pending rows are ignored for that boundary. Later syncs reach back a week but never before that boundary. Rows are recognised by the bank's transaction id, or without one by date + amount + name + purpose with a counter, so identical same-day bookings stay separate. Pending rows are replaced on every sync; booked rows are kept.
- Deleting a login removes its credentials, session and downloaded statements. Already imported accounts and transactions are kept (see unlinking above).

### Statements

Statement PDFs are stored on the data volume (`data/statements/<owner>/<login>/`) and indexed in the database, so each document is downloaded once. Downloading is configured **per account** (off by default; only for linked accounts the bank still lists): every month (default: tried at the start of a month, then daily until one arrived for the current month), every week, every day or only manually, plus a "Download now" button. Documents without an account (for example tax certificates) are fetched whenever any account of that login fetches statements and are visible to the owner only; account statements are visible to everyone who can see the account (owner and viewers of a shared project that uses it).

### Scalable Capital (`scalable`)

No credentials are stored: when the session is gone, OpenBudget opens a **web login** (see below) on Scalable's own login page, you sign in yourself (captcha and 2FA included) and the sync goes on. The connector then speaks to Scalable's web client GraphQL interface over plain HTTP with the cookies that sign-in produced. They are stored encrypted, so later syncs skip the sign-in until Scalable ends the session (the log says how long a session lasted when it expires).

Every broker portfolio shows up as two accounts: a **Depot** (positions; link it to a depot account) and a **Verrechnungskonto** (cash balance, IBAN and all bookings including security buys/sells and savings plans). Mailbox documents become statements: documents of a portfolio go to its depot (or, when only the cash account has statements switched on, to the cash account); documents without a portfolio are account-less. Savings accounts (Scalable's interest product) are not read yet.

### Web logins: signing in on the bank's own page

Some banks cannot be signed in to by a program (captcha, device checks, 2FA). A connector can instead ask for a **web login**: the owner gets a dialog with a live view of the bank's sign-in page, running in a headless Chromium on the server, and signs in there themselves, 2FA included. Nothing typed is logged or stored.

- Every web login gets its own throw-away browser context with no profile on disk; it is destroyed when the login completes, fails, is cancelled or after ten minutes. No bank session stays in a browser on the server, and bank sessions of different logins never mix.
- The login's stored cookie jar (the encrypted session) goes both ways: its cookies for the connector's domains are put into the fresh browser first, so long-lived "remember this device" cookies can spare the 2FA, and after success all cookies of those domains (with expiry, flags and SameSite) replace the old ones in the jar. The encrypted jar in the database is the only persistent copy.
- The view is streamed over a WebSocket (`/browser/ws/<session id>`) that only accepts the signed-in owner of that session (128-bit random id, owner check, same-origin check). A reverse proxy must pass WebSocket upgrades. Frames are never stored; input messages are validated and size-limited.
- Needs Chromium: the Docker image has it (about 1 GB more than a plain ASP.NET image, mostly Chromium's graphics libraries; 1.5 GB in total); elsewhere set `CHROME_PATH`. The browser is only started when a login needs it and is closed a minute after the last one ended. Without a Chromium, such connectors fail with "browser not available" and everything else is unaffected.
- A connector declares it as a challenge: `ConnectorChallenge.WebLogin(message, new WebLoginSpec(startUrl, cookieDomains, successUrlPrefix, successWhen))`; connectors that need no username/PIN set `CredentialRequirement.None` in their registration.

### FinTS banks (ING, Baader Bank, PSD Bank Nürnberg)

The connectors `ING`, `Baader Bank` and `PSD Bank Nürnberg` talk FinTS 3.0 (PIN/TAN) through a small built-in client (no third-party package, `src/OpenBudget/Connectors/FinTs/`). The login is the bank's login name and PIN; they are stored like every other bank credential.

**Product registration (required).** FinTS banks only accept registered applications. Register OpenBudget with the Deutsche Kreditwirtschaft (<https://www.hbci-zka.de/register/prod_register.htm>) and pass the product ID in the environment variable `OPENBUDGET_FINTS_PRODUCT_ID` (container environment, `.env`, secret store; never commit it). Without it a FinTS sync fails with "FinTS product registration not configured" and nothing is sent to the bank. The product version sent along is the application's version.

What each bank offers: ING: Girokonto and Extra-Konto (balance, transactions; VL-Spar and the Postbox need a separate web part and are not available yet). Baader Bank: cash account (balance, transactions) and depot positions; statements are not available yet. PSD Bank Nürnberg: accounts and the electronic account statements as PDF (HKEKA). The client reads transactions as camt.052 (HKCAZ), falls back to MT940 (HKKAZ) when a bank has no HKCAZ, reads balances (HKSAL) and depots (HKWPD).

TAN handling follows the bank: a choice of TAN methods becomes a choice dialog (remembered afterwards), a TAN becomes a code dialog, app approval (pushTAN, decoupled) becomes the usual approval wait that is polled until the bank confirms. Bank parameters (BPD/UPD) and the customer system id are cached in the encrypted login session; for Atruvia banks (PSD) an unchanged per-account inventory marker in the UPD lets a sync skip fetching transactions.

### Fake bank (for trying things out)

`OPENBUDGET_ENABLE_FAKE_BANK=true` (always on in tests) offers "Example Bank (fake)" with obviously made-up data. Any username and PIN work (PIN `wrong` is rejected). Flags after a `+` in the username trigger behaviours: `code` (answer 123456), `choice`, `approve` / `approve5` (completes after N polls), `image` (answer 4711), `loginfail`, `syncerror`, `refreshcode`, `statementcode`, `web` (sign in on a web page in the remote browser: opens example.com and is done once you click through to iana.org; `OPENBUDGET_FAKE_BANK_WEBLOGIN_URL` / `_SUCCESS` change the pages), `pending` (a reservation that becomes booked on the next sync), `dups` (identical same-day bookings), `noid` (no bank transaction ids), `statements`. Example: `demo+code+pending+statements`.

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
