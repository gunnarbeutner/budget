# Browser sidecar (selkies)

Web logins (Amazon, PayPal and other banks that need a real browser) run on a separate container. The user sees the
real, headed Chrome through a [selkies](https://github.com/selkies-project/selkies) video stream, popups and dialogs
included, with native keyboard and mouse input. The sidecar is the only way web logins run: without it configured they
fail with a message naming the missing settings, and everything else in the app works as usual.

## Pieces

- `docker/browser/` — the image, `FROM` the selkies desktop image pinned by digest.
  - `browser-launcher` — a small HTTP service (port 9300, compose network only) that starts **one Chrome per web
    login** on the shared X display, each with its own user-data directory and DevTools port (127.0.0.1:19400+n inside,
    exposed as 9400+n via socat). It hides every window except the one of the browser currently shown and keeps the
    focus there. It refuses to start when user namespaces are missing, so Chrome never drops its sandbox.
  - `seccomp_profile.json` (see `SECCOMP.md`) — lets Chrome's sandbox work without `privileged` or `SYS_ADMIN`.
  - `compose.yaml` — a local run for development (ports on loopback only).
- App side (`src/OpenBudget/Browser/`):
  - `RemoteBrowserOptions` — configuration; the sidecar counts as configured only when all three values are set.
  - `BrowserLauncherClient` (`IBrowserLauncher`) — talks to the launcher. Chrome refuses a DevTools Host header that
    is a name, so the client resolves the sidecar to its IP address first.
  - `BrowserService` — every session (ephemeral or kept profile) asks the launcher for a Chrome and connects
    PuppeteerSharp to it over CDP. Connectors see an `IBrowserSession`.
  - `RemoteScreen` — the one screen is shown to one web login at a time (a queue). See the security model below.
  - `DesktopProxy` + `WebLoginEndpoints` — `/weblogin/desktop/...` is proxied to selkies (same path; `SELKIES_SUBFOLDER`).
  - `Shared/RemoteBrowser.razor` — shows the stream in an iframe, or "another sign-in is using the browser" while waiting.

## Profiles

Kept browser profiles live encrypted in the app's `data/browser-profiles/` (`BrowserProfileStore`), so
**the sidecar holds none of them**. When a session starts, the app decrypts
the login's profile and sends it as a zip to the launcher, which unpacks it into a fresh directory (removing stale
`Singleton*` locks). When the session ends, the app closes Chrome over CDP (so it flushes its data), the launcher packs the
directory and deletes it, and the app stores it encrypted again (with the usual cache filtering and size limit). Every
login therefore keeps its own profile (e.g. two people's Amazon logins never share one), and ephemeral sessions use an
empty directory that is deleted afterwards. The sidecar holds no data at rest; it needs no volume.

## Configuration

| App setting (env) | Example | |
|---|---|---|
| `Browser__Remote__LauncherUrl` | `http://browser:9300` | launcher |
| `Browser__Remote__StreamUrl` | `http://browser:8080` | selkies |
| `Browser__Remote__Token` | 64 hex chars | the secret; same value as the sidecar's `SELKIES_MASTER_TOKEN` |

The token protects selkies' control API and the launcher. Create it with `openssl rand -hex 32`; keep it out of the repo.
`SELKIES_ALLOWED_ORIGINS` on the sidecar must list the app's public origin.

## Security model

- Only signed-in users reach `/weblogin/desktop/` (fallback policy), and of those only **the owner of the web login
  currently on screen**, while that login is still open; everyone else gets 404. Cross-origin requests are refused.
- The proxy forwards only GET requests to the stream client and an allow-list of selkies' API (`websockets`,
  `health`, `status`); the control API (`tokens`, `sessions`, `files`, ...) is never reachable. Only selkies' own cookie
  is forwarded, never the app's sign-in cookie.
- A fresh random selkies token is minted when a web login comes on screen and revoked (empty token table) when it
  leaves; the proxy also drops its stream connections at that moment.
- One screen: web logins queue; the shown one's Chrome is raised and focused and all other Chrome windows are
  minimised (a watcher re-applies this every 200 ms, also for new windows and popups). With nothing on screen, every
  window is hidden.
- File transfers, printing, audio, sharing links and command messages are switched off in selkies; clipboard is
  client-to-server only.
- Chrome runs sandboxed (user namespaces + seccomp-bpf), as an unprivileged user; DevTools ports are only on the
  compose network.

## Local try-out

```sh
export SELKIES_MASTER_TOKEN=$(openssl rand -hex 32)
docker compose -f docker/browser/compose.yaml up -d --build --wait
cd src/OpenBudget
ASPNETCORE_URLS=http://localhost:5000 Browser__Remote__LauncherUrl=http://127.0.0.1:9300 \
  Browser__Remote__StreamUrl=http://127.0.0.1:8090 Browser__Remote__Token=$SELKIES_MASTER_TOKEN dotnet run
```

Production wiring: see `deploy/`.
