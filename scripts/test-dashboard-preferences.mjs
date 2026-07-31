import assert from "node:assert/strict";
import { readFileSync } from "node:fs";

const app = readFileSync(new URL("../HermesSessionMetrics.Web/wwwroot/app.js", import.meta.url), "utf8");
const index = readFileSync(new URL("../HermesSessionMetrics.Web/wwwroot/index.html", import.meta.url), "utf8");
const program = readFileSync(new URL("../HermesSessionMetrics.Web/Program.cs", import.meta.url), "utf8");
const contracts = readFileSync(new URL("../HermesSessionMetrics.Web/Metrics/Contracts.cs", import.meta.url), "utf8");

assert.match(app, /minimumFractionDigits:\s*2,\s*maximumFractionDigits:\s*2/, "currency must always use two decimal places");
assert.doesNotMatch(app, /subscription traffic may report \$0(?!\.00)/, "visible zero-currency notes must use two decimals");
assert.match(app, /costBasis:\s*"api-equivalent"/, "API equivalent must be the client default");
assert.match(index, /data-window="all">All time<\/button>/, "window picker must expose All time");
assert.match(index, /data-cost-basis="api-equivalent" class="active"/, "API equivalent control must be active initially");
assert.doesNotMatch(index, /data-cost-basis="recorded" class="active"/, "Recorded must not be active initially");
assert.match(index, /id="costHeader"[^>]*>API equivalent<\/th>/, "initial cost header must match the default basis");
assert.match(app, /data-profile="all"[^>]*>All<\/button>/, "profile picker must expose All");
assert.match(app, /state\.profiles\s*=\s*new Set\(\["all"\]\)/, "All profile selection must map to the API's all selector");
assert.match(program, /"all"\s*=>\s*\(int\?\)null/, "API must accept the all-time window");
assert.match(program, /Window must be 24h, 7d, 30d, or all/, "invalid-window guidance must list all supported windows");
assert.match(program, /costBasis\s*\?\?\s*"api-equivalent"/, "API equivalent must be the server default");
assert.match(contracts, /string CostBasis\s*=\s*"api-equivalent"/, "API equivalent must be the query-contract default");

console.log("dashboard preferences contract: PASS");
