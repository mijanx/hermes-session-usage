# Hermes Session Usage

A local-first, read-only dashboard for exploring Hermes session token usage, model/task breakdowns, recorded cost telemetry, and API-equivalent pricing.

## Features

- Discovers `~/.hermes/state.db` and immediate `~/.hermes/profiles/*/state.db` profile databases.
- Fixed activity windows: **24 hours**, **7 days**, and **30 days**.
- Groups each session into `(model, provider, billing mode, task)` usage lines.
- Marks child sessions and displays their parent session ID.
- Shows input, cache read/write, output, reasoning, accounted tokens, API calls, and costs.
- Switches between provider-recorded cost and **API-equivalent** cost.
- Searches, sorts, paginates, and combines multiple profiles.

The dashboard never queries `messages`. SQLite connections are opened read-only and only the `sessions` and `session_model_usage` tables are read.

## Requirements

- .NET SDK 10
- Node.js only for the optional JavaScript syntax check
- A Hermes installation with session accounting tables

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

The checked-in `HermesSessionMetrics.Web/data/api-pricing.json` file is a local snapshot of direct-provider token rates from [models.dev](https://models.dev/). Refresh it explicitly:

```bash
python3 scripts/refresh-pricing.py
git diff -- HermesSessionMetrics.Web/data/api-pricing.json
```

The refresh script retains non-zero direct API prices from OpenAI, xAI, DeepSeek, Anthropic, Google, Mistral, MiniMax, Zhipu AI, Moonshot AI, and Alibaba. The dashboard matches model IDs case-insensitively and calculates:

```text
(input × input rate
 + cache read × cache-read rate
 + cache write × cache-write rate
 + output × output rate) / 1,000,000
```

Important limitations:

- This is a **counterfactual list-price estimate**, not an invoice or subscription allocation.
- Models without a direct price match display `—` and are excluded from the equivalent-cost total. The UI reports token coverage.
- Base rates are used. Context-length tiers cannot be reconstructed because per-request context sizes are not stored in the aggregate table.
- Reasoning tokens are displayed separately and are not added again; providers commonly include them in output accounting.
- Pricing changes over time. The snapshot source and retrieval timestamp are exposed by `GET /api/pricing` and in metrics responses.

## Test and verify

```bash
dotnet test -c Release
node --check HermesSessionMetrics.Web/wwwroot/app.js
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

Allowed windows are `24h`, `7d`, and `30d`. Use `profiles=all` for every discovered live profile. Profile responses expose names and database sizes, not local filesystem paths.

## Query semantics

A session is in the selected window when either:

1. the session started after the cutoff; or
2. at least one usage line has `last_seen`/`first_seen` after the cutoff.

This includes long-running sessions active during the period. Quarantine, backup, nested, and test databases are not discovered.

```text
accounted tokens = input + cache read + cache write + output
```

Reasoning is shown separately. Recorded estimated cost comes from Hermes telemetry and can legitimately be `$0` for subscription/OAuth traffic. API-equivalent cost remains a distinct counterfactual and never overwrites recorded telemetry.

## License

MIT
