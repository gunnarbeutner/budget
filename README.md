# OpenBudget

A self-hosted budgeting and cashflow app. Feed it your accounts and
transactions and you get per-project budgets, year-to-date variance reports,
and forward-looking balance projections.

## Concepts

- **Source** — one imported data set. Multiple sources can coexist
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
- **Sources (`/sources`)** — registered sources and their upload
  freshness.

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
reverse proxy in front. The web UI has **no authentication** — keep the app on
a private network or add auth at the proxy.

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
`Authorization: Bearer <OPENBUDGET_UPLOAD_TOKEN>`:

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

.NET 10 / Blazor Server, Entity Framework Core + SQLite, Bootstrap 5.3,
Bootstrap Icons, Chart.js. Theme follows the OS `prefers-color-scheme`.
