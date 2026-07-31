import assert from "node:assert/strict";
import { readFileSync } from "node:fs";

const app = readFileSync(new URL("../HermesSessionMetrics.Web/wwwroot/app.js", import.meta.url), "utf8");
const css = readFileSync(new URL("../HermesSessionMetrics.Web/wwwroot/app.css", import.meta.url), "utf8");

assert.match(app, /class="model detail-tooltip" tabindex="0"[^>]*aria-label=/, "provider tooltip must be keyboard and assistive-technology accessible");
assert.match(app, /data-session-id=/, "top-level session IDs must have a copy affordance");
assert.match(app, /try[\s\S]*navigator\.clipboard\.writeText[\s\S]*catch[\s\S]*document\.createElement\("textarea"\)/, "clipboard rejection must fall back to the LAN-compatible copy path");
assert.match(app, /navigator\.clipboard[\s\S]*document\.execCommand\("copy"\)/, "session ID copy must work on secure and LAN HTTP contexts");
assert.doesNotMatch(app, /<div class="model">[^\n]+<\/div><div class="provider">/, "model/provider must not occupy two lines");
assert.match(app, /class="token-context"/, "session token context must render inline in parentheses");
assert.match(app, /class="started-context"/, "session source must render inline in parentheses");
assert.match(app, /class="session-id-copy"[^>]*title="Copy session ID[^>]*aria-live="polite"/, "session ID copy must expose hover details and announce results");
assert.match(app, /Copied session ID[\s\S]*Could not copy session ID/, "copy success and failure must update the accessible label");
assert.match(app, /aria-disabled[^\n]+return[\s\S]*aria-disabled[^\n]+true[\s\S]*removeAttribute\("aria-disabled"\)/, "copy feedback must reject overlapping asynchronous activations");
assert.doesNotMatch(app, /parentReference|parent outside result:|<br>parent:/, "nested sessions must not repeat inferred IDs or parent references");
assert.match(app, /session\.title === session\.id\s*\?\s*"Child process"/, "ID-only nested titles must be replaced by a semantic label");
assert.match(css, /\.session-title\s*\{[^}]*white-space:\s*nowrap/s, "session identity must stay on one line");
assert.match(css, /\.token-context[^}]*\.started-context|\.started-context[^}]*\.token-context/s, "inline context styling must cover tokens and start source");

console.log("compact UI contract: PASS");
