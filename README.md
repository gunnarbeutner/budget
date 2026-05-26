# OpenBudget

A self-hosted budgeting and cashflow companion for
[MoneyMoney](https://moneymoney-app.com), the macOS personal-finance app. Point
it at a snapshot of your MoneyMoney database and you get per-project budgets,
year-to-date variance reports, and forward-looking balance projections.

## Concepts

- **Source** — one MoneyMoney installation. Multiple sources can coexist
  (e.g. personal + shared household).
- **Project** — a budget context: a set of accounts and tracked categories
  from one source. Each project gets its own dashboard tile.
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
- **Sources (`/sources`)** — registered MoneyMoney sources and their upload
  freshness.

## Running it

Local development:

```bash
dotnet run
```

Production via Docker:

```bash
echo "MONEYMONEY_DB_UPLOAD_TOKEN=$(openssl rand -hex 32)" > .env
docker compose up -d
```

The container speaks plain HTTP on `:8080` and expects a TLS-terminating
reverse proxy in front. The web UI has **no authentication** — keep the app on
a private network or add auth at the proxy.

## Getting MoneyMoney data in

The `exporter/` directory ships a small macOS helper that decrypts
MoneyMoney's database and uploads it to the app. Build it with
`exporter/build.sh` and run it on the same Mac that runs MoneyMoney; the
bundle needs Full Disk Access to reach MoneyMoney's container.

## Localization

English and German. UI follows the browser's `Accept-Language`.

## Tech stack

.NET 10 / Blazor Server, Entity Framework Core + SQLite, Bootstrap 5.3,
Bootstrap Icons, Chart.js. Theme follows the OS `prefers-color-scheme`.
