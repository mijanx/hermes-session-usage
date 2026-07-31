const state = {
  profiles: new Set(),
  window: "24h",
  search: "",
  sort: "tokens",
  costBasis: "recorded",
  descending: true,
  limit: 100,
  offset: 0,
  loading: false,
  result: null,
  controller: null
};

const $ = id => document.getElementById(id);
const number = new Intl.NumberFormat("en", { maximumFractionDigits: 0 });
const compact = new Intl.NumberFormat("en", { notation: "compact", maximumFractionDigits: 2 });
const usd = new Intl.NumberFormat("en", { style: "currency", currency: "USD", minimumFractionDigits: 0, maximumFractionDigits: 4 });
const dateTime = new Intl.DateTimeFormat(undefined, { month: "short", day: "2-digit", hour: "2-digit", minute: "2-digit" });

function escapeHtml(value) {
  return String(value ?? "")
    .replaceAll("&", "&amp;")
    .replaceAll("<", "&lt;")
    .replaceAll(">", "&gt;")
    .replaceAll('"', "&quot;")
    .replaceAll("'", "&#039;");
}

function setStatus(kind, text) {
  $("statusDot").className = `status-dot ${kind}`;
  $("statusText").textContent = text;
}

async function loadProfiles() {
  const response = await fetch("/api/profiles");
  if (!response.ok) throw new Error(`Profiles request failed: ${response.status}`);
  const profiles = await response.json();
  if (![...state.profiles].some(name => profiles.some(x => x.name === name)) && profiles.length) state.profiles = new Set([profiles[0].name]);
  $("profileChips").innerHTML = profiles.map(profile => {
    const active = state.profiles.has(profile.name) ? "active" : "";
    const size = `${(profile.sizeBytes / 1e9).toFixed(1)} GB`;
    return `<button class="chip ${active}" data-profile="${escapeHtml(profile.name)}" title="${size}">${escapeHtml(profile.name)}</button>`;
  }).join("");
  $("profileChips").querySelectorAll("button").forEach(button => button.addEventListener("click", () => {
    const name = button.dataset.profile;
    if (state.profiles.has(name) && state.profiles.size > 1) state.profiles.delete(name);
    else if (!state.profiles.has(name)) state.profiles.add(name);
    button.classList.toggle("active", state.profiles.has(name));
    state.offset = 0;
    loadMetrics();
  }));
}

function queryUrl() {
  const params = new URLSearchParams({
    profiles: [...state.profiles].join(","),
    window: state.window,
    search: state.search,
    sort: state.sort,
    costBasis: state.costBasis,
    descending: String(state.descending),
    limit: String(state.limit),
    offset: String(state.offset)
  });
  return `/api/metrics?${params}`;
}

async function loadMetrics() {
  state.controller?.abort();
  state.controller = new AbortController();
  state.loading = true;
  setStatus("", "Querying…");
  $("refreshButton").disabled = true;
  const started = performance.now();
  try {
    const response = await fetch(queryUrl(), { signal: state.controller.signal });
    if (!response.ok) {
      const problem = await response.json().catch(() => ({}));
      throw new Error(problem.error || `Metrics request failed: ${response.status}`);
    }
    state.result = await response.json();
    render();
    const roundTrip = Math.round(performance.now() - started);
    setStatus("ok", `Live · ${roundTrip} ms`);
    $("errorBanner").hidden = true;
  } catch (error) {
    if (error.name === "AbortError") return;
    setStatus("error", "Query failed");
    $("errorBanner").textContent = error.message;
    $("errorBanner").hidden = false;
  } finally {
    state.loading = false;
    $("refreshButton").disabled = false;
  }
}

function metricCard(label, value, note) {
  return `<article class="metric-card"><div class="metric-label">${label}</div><div class="metric-value">${value}</div><div class="metric-note">${note}</div></article>`;
}

function renderSummary(result) {
  const apiEquivalent = state.costBasis === "api-equivalent";
  const priced = result.apiEquivalentPricedTokens || 0;
  const totalPricingTokens = priced + (result.apiEquivalentUnpricedTokens || 0);
  const coverage = totalPricingTokens ? (priced / totalPricingTokens * 100).toFixed(1) : "0.0";
  const pricingDate = result.apiPricingSource?.retrievedAt
    ? dateTime.format(new Date(result.apiPricingSource.retrievedAt))
    : "unknown date";
  const costValue = apiEquivalent ? result.apiEquivalentCostUsd : result.estimatedCostUsd;
  const costNote = apiEquivalent
    ? `${coverage}% token coverage · rates ${pricingDate}`
    : (result.actualCostUsd > 0 ? `${usd.format(result.actualCostUsd)} actual` : "subscription traffic may report $0");
  $("summaryCards").innerHTML = [
    metricCard("Sessions", number.format(result.filteredSessions), `${number.format(result.totalSessions)} stored in selected profiles`),
    metricCard("Accounted tokens", compact.format(result.accountedTokens), "input + cache + output"),
    metricCard("Reasoning", compact.format(result.reasoningTokens), "shown separately"),
    metricCard("API calls", compact.format(result.apiCalls), "across all model/task lines"),
    metricCard(apiEquivalent ? "API equivalent" : "Recorded estimate", usd.format(costValue), costNote)
  ].join("");
}

function taskBadge(task) {
  const auxiliary = task !== "agent";
  return `<span class="task-badge ${auxiliary ? "aux" : ""}">${escapeHtml(task)}</span>`;
}

function renderSessionRows(sessions) {
  if (!sessions.length) {
    $("sessionRows").innerHTML = `<tr><td colspan="13" class="empty">No sessions match this window and filter.</td></tr>`;
    return;
  }

  const rows = [];
  sessions.forEach(session => {
    const lines = session.usageLines.length ? session.usageLines : [{ model: session.primaryModel, provider: "", task: "agent", apiCalls: session.apiCalls, inputTokens: 0, cacheReadTokens: 0, cacheWriteTokens: 0, outputTokens: 0, reasoningTokens: session.reasoningTokens, accountedTokens: session.accountedTokens, estimatedCostUsd: session.estimatedCostUsd, apiEquivalentCostUsd: null }];
    const rowSpan = lines.length;
    lines.forEach((line, index) => {
      const first = index === 0;
      const childBadge = session.isChild
        ? `<span class="child-badge" title="Parent: ${escapeHtml(session.parentSessionId)}">CHILD</span>`
        : "";
      const sessionCells = first ? `
        <td rowspan="${rowSpan}" class="session-cell">
          <div class="session-title"><span class="profile-badge">${escapeHtml(session.profile)}</span>${childBadge}${escapeHtml(session.title)}</div>
          <div class="session-id">${escapeHtml(session.id)}${session.isChild ? `<br>parent: ${escapeHtml(session.parentSessionId)}` : ""}</div>
        </td>
        <td rowspan="${rowSpan}">${dateTime.format(new Date(session.startedAt))}<div class="provider">${escapeHtml(session.source)}</div></td>
        <td rowspan="${rowSpan}"><span class="status-badge ${session.status === "active" ? "" : "closed"}">${escapeHtml(session.status)}</span></td>
        <td rowspan="${rowSpan}" class="number"><strong>${number.format(session.accountedTokens)}</strong><div class="provider">${number.format(session.apiCalls)} calls</div></td>` : "";
      const cache = (line.cacheReadTokens || 0) + (line.cacheWriteTokens || 0);
      const displayedCost = state.costBasis === "api-equivalent" ? line.apiEquivalentCostUsd : line.estimatedCostUsd;
      const costClass = displayedCost > 0 ? "cost-metered" : "";
      const costText = displayedCost == null ? "—" : usd.format(displayedCost);
      rows.push(`<tr class="${first ? "group-start" : ""}">
        ${sessionCells}
        <td>${taskBadge(line.task)}</td>
        <td><div class="model">${escapeHtml(line.model)}</div><div class="provider">${escapeHtml(line.provider || "unattributed")}${line.billingMode ? ` · ${escapeHtml(line.billingMode)}` : ""}</div></td>
        <td class="number">${number.format(line.apiCalls || 0)}</td>
        <td class="number">${number.format(line.inputTokens || 0)}</td>
        <td class="number">${number.format(cache)}</td>
        <td class="number">${number.format(line.outputTokens || 0)}</td>
        <td class="number">${number.format(line.reasoningTokens || 0)}</td>
        <td class="number"><strong>${number.format(line.accountedTokens || 0)}</strong></td>
        <td class="number ${costClass}" title="${state.costBasis === "api-equivalent" && line.apiEquivalentPricingProvider ? `Direct ${escapeHtml(line.apiEquivalentPricingProvider)} API rate` : ""}">${costText}</td>
      </tr>`);
    });
  });
  $("sessionRows").innerHTML = rows.join("");
}

function render() {
  const result = state.result;
  renderSummary(result);
  renderSessionRows(result.sessions);
  $("costHeader").textContent = state.costBasis === "api-equivalent" ? "API equivalent" : "Recorded cost";
  const first = result.filteredSessions ? state.offset + 1 : 0;
  const last = Math.min(state.offset + result.sessions.length, result.filteredSessions);
  $("resultMeta").textContent = `${number.format(first)}–${number.format(last)} of ${number.format(result.filteredSessions)} sessions · ${result.queryElapsedMilliseconds} ms database query · snapshot ${dateTime.format(new Date(result.generatedAt))}`;
  $("pageStatus").textContent = `Page ${Math.floor(state.offset / state.limit) + 1}`;
  $("previousPage").disabled = state.offset === 0;
  $("nextPage").disabled = state.offset + result.sessions.length >= result.filteredSessions;
}

function debounce(fn, milliseconds) {
  let handle;
  return (...args) => {
    clearTimeout(handle);
    handle = setTimeout(() => fn(...args), milliseconds);
  };
}

$("windowPicker").querySelectorAll("button").forEach(button => button.addEventListener("click", () => {
  state.window = button.dataset.window;
  state.offset = 0;
  $("windowPicker").querySelectorAll("button").forEach(x => x.classList.toggle("active", x === button));
  loadMetrics();
}));
$("searchInput").addEventListener("input", debounce(event => {
  state.search = event.target.value.trim();
  state.offset = 0;
  loadMetrics();
}, 280));
$("sortSelect").addEventListener("change", event => {
  state.sort = event.target.value;
  state.offset = 0;
  loadMetrics();
});
$("costBasisPicker").querySelectorAll("button").forEach(button => button.addEventListener("click", () => {
  state.costBasis = button.dataset.costBasis;
  state.offset = 0;
  $("costBasisPicker").querySelectorAll("button").forEach(x => x.classList.toggle("active", x === button));
  loadMetrics();
}));
$("refreshButton").addEventListener("click", loadMetrics);
$("previousPage").addEventListener("click", () => { state.offset = Math.max(0, state.offset - state.limit); loadMetrics(); });
$("nextPage").addEventListener("click", () => { state.offset += state.limit; loadMetrics(); });

(async function start() {
  try {
    await loadProfiles();
    await loadMetrics();
  } catch (error) {
    setStatus("error", "Startup failed");
    $("errorBanner").textContent = error.message;
    $("errorBanner").hidden = false;
  }
})();
