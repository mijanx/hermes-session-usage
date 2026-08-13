# Hermes Session Usage

A local-first, read-only dashboard for exploring Hermes session token usage, model/task breakdowns, recorded cost telemetry, and API-equivalent pricing.

## Features

- Discovers `~/.hermes/state.db` and immediate `~/.hermes/profiles/*/state.db` profile databases.
- Activity windows: **24 hours**, **7 days**, **30 days**, and **all time**.
- Groups parent sessions and their descendants into sortable, pagination-safe session families.
- Collapses usage into `(model, task)` lines while retaining every observed provider/billing label.
- Marks attached child sessions and displays their immediate parent session ID.
- Shows input, cache read/write, output, reasoning, accounted tokens, API calls, and costs.
- Switches between provider-recorded cost and **API-equivalent** cost.
- Searches, sorts, paginates, and combines multiple profiles.

The dashboard never queries `messages`. SQLite connections are opened read-only and only the `sessions` and `session_model_usage` tables are read.

## Requirements

- .NET SDK 10
- Node.js only for the optional JavaScript syntax check
- A Hermes installation with session accounting tables

The native desktop plugin does not run the .NET web server. Its read-only
FastAPI backend runs inside the Hermes gateway and uses Python's standard
SQLite driver.

## Install as a Hermes Desktop plugin

The repository contains all three plugin layers:

- `desktop/plugin.js` contributes the native Desktop Session Usage page,
  sidebar entry, command-palette action, and status-bar summary.
- `dashboard/dist/index.js` contributes a native **Session Usage** tab to
  `hermes dashboard` using the web dashboard Plugin SDK.
- `dashboard/plugin_api.py` provides the shared plugin-scoped backend mounted
  at `/api/plugins/session-usage` by Hermes.

Install all layers into the active Hermes home, enable the Python backend, and
restart the owning gateway/dashboard processes:

```bash
python scripts/install-plugin.py
hermes plugins enable session-usage
hermes gateway restart
# Restart `hermes dashboard`, or rescan after signing in:
# GET /api/dashboard/plugins/rescan
```

After dashboard restart/rescan, refresh the browser and open **Session Usage**
from the web navigation. Hermes Desktop watches `desktop-plugins/` and normally
loads its renderer within seconds. If it does not appear there, run **Reload
desktop plugins** from the Desktop command palette. The native page is available
from the **Session usage** sidebar row or **Open Session Usage** palette action.

`HERMES_HOME` is honored automatically. To target an explicit installation or
profile without changing the active shell environment:

```bash
python scripts/install-plugin.py --hermes-home /path/to/hermes/home
```

The desktop enable switch and Python backend allow-list are intentionally
separate Hermes security gates. Settings → Plugins controls the renderer;
`hermes plugins enable session-usage` permits the gateway to import the local
backend code. Restart the gateway after backend changes. Renderer edits
hot-reload.

## Run locally

```bash
dotnet run --project HermesSessionMetrics.Web --urls http://127.0.0.1:5187
```

Open <http://127.0.0.1:5187>.

Configuration can be supplied through normal ASP.NET Core configuration:

| Setting | Default | Purpose |
|---|---|---|
| `HermesRoot` | `~/.hermes` | Hermes data root containing live profile databases |
| `PricingTablePath` | `data/api-pricing.json` beside the application | Local API pricing snapshot |

## API-equivalent pricing

The checked-in `HermesSessionMetrics.Web/data/api-pricing.json` file combines audited rates from official provider documentation with [models.dev](https://models.dev/) as a fallback for models that do not have an official override. The normalized official entries live in `HermesSessionMetrics.Web/data/official-provider-pricing.json`.

| Provider | Official pricing source | Basis used |
|---|---|---|
| OpenAI | [API pricing](https://developers.openai.com/api/docs/pricing.md) and [latest-model guide](https://developers.openai.com/api/docs/guides/latest-model) | Standard, short-context text-token rates; guide-backed `gpt-5.6` alias |
| xAI | [API pricing](https://docs.x.ai/developers/pricing.md) | Standard, short-context rates below 200k prompt tokens |
| Kimi | [K2.5](https://platform.kimi.ai/docs/pricing/chat-k25.md), [K2.6](https://platform.kimi.ai/docs/pricing/chat-k26.md), [K2.7 Code](https://platform.kimi.ai/docs/pricing/chat-k27-code.md), and [K3](https://platform.kimi.ai/docs/pricing/chat-k3.md) | Cache-hit, cache-miss, and output rates |
| MiniMax | [Pay-as-you-go](https://platform.minimax.io/docs/guides/pricing-paygo.md) and [prompt caching](https://platform.minimax.io/docs/api-reference/anthropic-api-compatible-cache.md) | Standard rates; M3 uses the ≤512k tier |

Refresh the models.dev fallback and rebuild the merged snapshot explicitly:

```bash
python3 scripts/refresh-pricing.py
git diff -- HermesSessionMetrics.Web/data/api-pricing.json
```

The refresh script applies the official OpenAI, xAI, Kimi, and MiniMax entries after importing non-zero direct API prices from models.dev for OpenAI, xAI, DeepSeek, Anthropic, Google, Mistral, MiniMax, Zhipu AI, Moonshot AI, and Alibaba. Official entries therefore win on model-ID collisions. The script deliberately does not scrape vendor documentation: recheck the linked pages and update `official-provider-pricing.json` when refreshing those audited rates. The dashboard matches model IDs case-insensitively and calculates:

```text
(input × input rate
 + cache read × cache-read rate
 + cache write × cache-write rate
 + output × output rate) / 1,000,000
```

Important limitations:

- This is a **counterfactual list-price estimate**, not an invoice or subscription allocation.
- Models without a direct price match display `—` and are excluded from the equivalent-cost total. The UI reports token coverage.
- Standard base rates are used. OpenAI, xAI, and MiniMax context-length uplifts cannot be reconstructed because per-request context sizes are not stored in the aggregate table.
- If an official page publishes cache-read pricing but no separate cache-write price, cache writes use the ordinary input/cache-miss rate rather than inventing a discount.
- Reasoning tokens are displayed separately and are not added again; providers commonly include them in output accounting.
- Pricing changes over time. `GET /api/pricing` exposes every normalized model rate with its `sourceIds` and basis, plus the complete source list, retrieval timestamps, and models.dev fallback provenance.

## Test and verify

```bash
dotnet test -c Release
node --check HermesSessionMetrics.Web/wwwroot/app.js
node --check desktop/plugin.js
PYTHONPATH=/path/to/hermes-agent python -m unittest discover -s tests -v
dotnet list package --vulnerable --include-transitive
```

## Publish

```bash
dotnet publish HermesSessionMetrics.Web/HermesSessionMetrics.Web.csproj \
  -c Release -o "$HOME/.local/share/hermes-session-usage"
```

An example hardened user service is provided at `deploy/hermes-session-usage.service`:

```bash
install -Dm644 deploy/hermes-session-usage.service \
  "$HOME/.config/systemd/user/hermes-session-usage.service"
systemctl --user daemon-reload
systemctl --user enable --now hermes-session-usage.service
curl -fsS http://127.0.0.1:5187/api/health
```

The example binds to loopback only. Do not expose this unauthenticated dashboard to the public internet. If LAN access is required, use a VPN or authenticated reverse proxy, or deliberately override the bind address and systemd IP allowlist for the trusted subnet.

## HTTP API

```text
GET /api/health
GET /api/profiles
GET /api/pricing
GET /api/metrics?profiles=default&window=7d&search=compression&sort=tokens&costBasis=api-equivalent&descending=true&limit=100&offset=0
```

Allowed windows are `24h`, `7d`, `30d`, and `all`. Use `profiles=all` for every discovered live profile. The API defaults to `24h` and `costBasis=api-equivalent`; the dashboard exposes explicit **All** profile and **All time** controls. Profile responses expose names and database sizes, not local filesystem paths.

`/api/metrics` currently returns response schema **v2**, identified by `schemaVersion: 2`. V2 is intentionally family-oriented: the former top-level `sessions` page was replaced by `families`, and each family contains its member `sessions` plus family-wide `usageLines`. Consumers written for the earlier unversioned shape must migrate to `families`.

## Query semantics

A session is in the selected window when either:

1. the session started after the cutoff; or
2. at least one usage line has `last_seen`/`first_seen` after the cutoff.

This includes long-running sessions active during the period. Quarantine, backup, nested, and test databases are not discovered.

Matching sessions are grouped by their highest known ancestor from the profile's session graph before sorting and pagination; nonmatching intermediate sessions preserve lineage but are not included in family totals or member rows. If an ancestor falls outside the selected window/search result, matching descendants remain grouped and the UI labels that parent as outside the result. Family sorting uses aggregate family tokens, calls, or cost, while summary totals still count each matching session exactly once. Families are collapsed by default: the visible model/task lines and totals aggregate the full family, and the child-process toggle reveals each matching process separately.

Within each session, rows with the same case-insensitive `(model, task)` identity are merged even when attribution differs. Token, call, and cost counters are summed; distinct provider and billing labels are retained as comma-separated labels, with blank providers shown as `unattributed`.

```text
accounted tokens = input + cache read + cache write + output
```

Reasoning is shown separately. Recorded estimated cost comes from Hermes telemetry and can legitimately be `$0` for subscription/OAuth traffic. API-equivalent cost remains a distinct counterfactual and never overwrites recorded telemetry.

## License

MIT
