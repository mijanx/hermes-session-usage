(function () {
  "use strict";

  const SDK = window.__HERMES_PLUGIN_SDK__;
  if (!SDK || !window.__HERMES_PLUGINS__) {
    console.warn("Session Usage: Hermes dashboard plugin SDK is unavailable");
    return;
  }

  const { React, fetchJSON } = SDK;
  const { useCallback, useEffect, useMemo, useState } = SDK.hooks;
  const { Card, CardContent, Badge, Button, Input, Select, SelectOption } = SDK.components;
  const h = React.createElement;
  const API = "/api/plugins/session-usage";
  const number = new Intl.NumberFormat(undefined, { maximumFractionDigits: 0 });
  const short = new Intl.NumberFormat(undefined, { notation: "compact", maximumFractionDigits: 1 });
  const money = new Intl.NumberFormat(undefined, {
    style: "currency", currency: "USD", minimumFractionDigits: 2, maximumFractionDigits: 4
  });
  const dateTime = new Intl.DateTimeFormat(undefined, {
    month: "short", day: "2-digit", hour: "2-digit", minute: "2-digit"
  });

  const css = {
    page: { display: "flex", flexDirection: "column", gap: "1rem", minWidth: 0 },
    heading: { display: "flex", justifyContent: "space-between", alignItems: "flex-start", gap: "1rem", flexWrap: "wrap" },
    title: { margin: 0, fontSize: "1.5rem", fontWeight: 700 },
    muted: { color: "var(--color-muted-foreground)", fontSize: ".8rem" },
    controls: { display: "flex", flexWrap: "wrap", gap: ".5rem", alignItems: "center" },
    profiles: { display: "flex", flexWrap: "wrap", gap: ".35rem", alignItems: "center" },
    metrics: { display: "grid", gridTemplateColumns: "repeat(auto-fit, minmax(145px, 1fr))", gap: ".75rem" },
    metricValue: { fontSize: "1.35rem", lineHeight: 1.2, fontWeight: 700, fontVariantNumeric: "tabular-nums" },
    metricLabel: { color: "var(--color-muted-foreground)", fontSize: ".72rem", textTransform: "uppercase", letterSpacing: ".04em" },
    familyHead: { display: "flex", justifyContent: "space-between", alignItems: "flex-start", gap: ".75rem", flexWrap: "wrap" },
    familyTitle: { display: "flex", alignItems: "center", gap: ".45rem", flexWrap: "wrap", overflowWrap: "anywhere" },
    tableWrap: { overflowX: "auto", marginTop: ".75rem" },
    table: { width: "100%", borderCollapse: "collapse", fontSize: ".78rem", fontVariantNumeric: "tabular-nums" },
    th: { padding: ".45rem .5rem", borderBottom: "1px solid var(--color-border)", color: "var(--color-muted-foreground)", textAlign: "right", whiteSpace: "nowrap", fontWeight: 600 },
    td: { padding: ".45rem .5rem", borderBottom: "1px solid var(--color-border)", textAlign: "right", whiteSpace: "nowrap" },
    left: { textAlign: "left" },
    error: { padding: ".8rem", border: "1px solid var(--color-destructive)", borderRadius: "var(--radius)", color: "var(--color-destructive)" },
    pagination: { display: "flex", justifyContent: "space-between", alignItems: "center", gap: ".75rem", flexWrap: "wrap" }
  };

  function api(path, options) {
    return fetchJSON(API + path, options);
  }

  function formatCost(value) {
    return value == null ? "—" : money.format(value);
  }

  function pricingCoverageNote(result) {
    const priced = result.apiEquivalentPricedTokens || 0;
    const unpriced = result.apiEquivalentUnpricedTokens || 0;
    return number.format(priced) + " / " + number.format(priced + unpriced) + " eligible tokens priced";
  }

  function Metric(props) {
    return h(Card, null, h(CardContent, { className: "pt-4 pb-4" },
      h("div", { style: css.metricLabel }, props.label),
      h("div", { style: css.metricValue }, props.value),
      props.note ? h("div", { style: css.muted }, props.note) : null
    ));
  }

  function ToggleButton({ active, disabled, onClick, children, title }) {
    return h(Button, {
      type: "button",
      variant: active ? "default" : "outline",
      size: "sm",
      disabled: Boolean(disabled),
      onClick,
      title
    }, children);
  }

  function UsageTable({ lines, costBasis }) {
    const apiCost = costBasis === "api-equivalent";
    return h("div", { style: css.tableWrap }, h("table", { style: css.table },
      h("thead", null, h("tr", null,
        h("th", { style: { ...css.th, ...css.left } }, "Task"),
        h("th", { style: { ...css.th, ...css.left } }, "Model / provider"),
        h("th", { style: css.th }, "Calls"),
        h("th", { style: css.th }, "Input"),
        h("th", { style: css.th }, "Cache"),
        h("th", { style: css.th }, "Output"),
        h("th", { style: css.th }, "Reasoning"),
        h("th", { style: css.th }, "Accounted"),
        h("th", { style: css.th }, apiCost ? "API equivalent" : "Recorded")
      )),
      h("tbody", null, (lines || []).map((line, index) => h("tr", { key: line.model + ":" + line.task + ":" + index },
        h("td", { style: { ...css.td, ...css.left } }, line.task || "agent"),
        h("td", { style: { ...css.td, ...css.left } },
          h("div", null, line.model || "unknown"),
          h("div", { style: css.muted }, line.provider || "unattributed")
        ),
        h("td", { style: css.td }, number.format(line.apiCalls || 0)),
        h("td", { style: css.td }, short.format(line.inputTokens || 0)),
        h("td", { style: css.td }, short.format((line.cacheReadTokens || 0) + (line.cacheWriteTokens || 0))),
        h("td", { style: css.td }, short.format(line.outputTokens || 0)),
        h("td", { style: css.td }, short.format(line.reasoningTokens || 0)),
        h("td", { style: css.td }, short.format(line.accountedTokens || 0)),
        h("td", { style: css.td }, formatCost(apiCost ? line.apiEquivalentCostUsd : line.estimatedCostUsd))
      )))
    ));
  }

  function SessionBlock({ session, costBasis }) {
    return h("div", { style: { marginTop: ".75rem", paddingLeft: ".75rem", borderLeft: "2px solid var(--color-border)" } },
      h("div", { style: css.familyTitle },
        h("strong", null, session.title || session.id),
        h(Badge, { variant: "secondary" }, session.source || "unknown"),
        session.parentSessionId ? h("span", { style: css.muted }, "child of " + session.parentSessionId) : null
      ),
      h(UsageTable, { lines: session.usageLines, costBasis })
    );
  }

  function FamilyCard({ family, costBasis }) {
    const [expanded, setExpanded] = useState(false);
    const lead = family.sessions && family.sessions[0];
    const displayedCost = costBasis === "api-equivalent" ? family.apiEquivalentCostUsd : family.estimatedCostUsd;
    return h(Card, null, h(CardContent, { className: "pt-4 pb-4" },
      h("div", { style: css.familyHead },
        h("div", { style: { minWidth: 0 } },
          h("div", { style: css.familyTitle },
            h("strong", null, (lead && lead.title) || family.rootSessionId),
            h(Badge, { variant: "outline" }, family.profile),
            family.sessions.length > 1 ? h(Badge, { variant: "secondary" }, family.sessions.length + " processes") : null,
            !family.rootIncluded ? h(Badge, { variant: "outline" }, "root outside result") : null
          ),
          h("div", { style: css.muted },
            dateTime.format(new Date(family.startedAt)) + " · " + short.format(family.accountedTokens || 0) +
            " tokens · " + short.format(family.apiCalls || 0) + " calls · " + formatCost(displayedCost)
          )
        ),
        family.sessions.length > 1 ? h(ToggleButton, {
          active: expanded,
          onClick: function () { setExpanded(function (value) { return !value; }); }
        }, expanded ? "Collapse processes" : "Show processes") : null
      ),
      expanded
        ? family.sessions.map(session => h(SessionBlock, { key: session.id, session, costBasis }))
        : h(UsageTable, { lines: family.usageLines, costBasis })
    ));
  }

  function SessionUsagePage() {
    const [profiles, setProfiles] = useState([]);
    const [selected, setSelected] = useState([]);
    const [windowName, setWindowName] = useState("24h");
    const [costBasis, setCostBasis] = useState("api-equivalent");
    const [sort, setSort] = useState("tokens");
    const [searchDraft, setSearchDraft] = useState("");
    const [search, setSearch] = useState("");
    const [offset, setOffset] = useState(0);
    const [result, setResult] = useState(null);
    const [error, setError] = useState("");
    const [loading, setLoading] = useState(true);
    const [profilesLoaded, setProfilesLoaded] = useState(false);
    const [refreshKey, setRefreshKey] = useState(0);
    const limit = 50;

    useEffect(function () {
      let live = true;
      setProfilesLoaded(false);
      setLoading(true);
      api("/profiles").then(function (data) {
        if (!live) return;
        const list = data || [];
        setProfiles(list);
        setSelected(function (current) {
          return current.length ? current : (list.length ? [list[0].name] : []);
        });
        setProfilesLoaded(true);
        if (!list.length) setLoading(false);
      }).catch(function (reason) {
        if (!live) return;
        setError(reason.message || String(reason));
        setProfilesLoaded(true);
        setLoading(false);
      });
      return function () { live = false; };
    }, [refreshKey]);

    useEffect(function () {
      const handle = window.setTimeout(function () {
        setSearch(searchDraft.trim());
        setOffset(0);
      }, 300);
      return function () { window.clearTimeout(handle); };
    }, [searchDraft]);

    const request = useMemo(function () {
      return { profiles: selected, window: windowName, search, sort, costBasis, descending: true, limit, offset };
    }, [selected, windowName, search, sort, costBasis, offset]);

    useEffect(function () {
      if (!selected.length) {
        if (profilesLoaded) setLoading(false);
        return undefined;
      }
      let live = true;
      setLoading(true);
      setError("");
      api("/metrics", { method: "POST", body: JSON.stringify(request), headers: { "Content-Type": "application/json" } })
        .then(function (data) { if (live) setResult(data); })
        .catch(function (reason) { if (live) setError(reason.message || String(reason)); })
        .finally(function () { if (live) setLoading(false); });
      return function () { live = false; };
    }, [request, refreshKey, profilesLoaded]);

    const toggleProfile = useCallback(function (name) {
      setOffset(0);
      setSelected(function (current) {
        if (name === "all") return ["all"];
        if (current.includes("all")) return [name];
        if (current.includes(name)) return current.length > 1 ? current.filter(item => item !== name) : current;
        return current.concat(name);
      });
    }, []);

    const first = result && result.filteredFamilies ? offset + 1 : 0;
    const last = result ? Math.min(offset + result.families.length, result.filteredFamilies) : 0;
    const displayedCost = result ? (costBasis === "api-equivalent" ? result.apiEquivalentCostUsd : result.estimatedCostUsd) : null;

    return h("section", { style: css.page },
      h("header", { style: css.heading },
        h("div", null,
          h("h1", { style: css.title }, "Session Usage"),
          h("div", { style: css.muted }, "Read-only local token, model/task, and API-equivalent pricing telemetry")
        ),
        h("div", { style: css.muted }, loading ? "Refreshing…" : (result ? "Live · " + result.queryElapsedMilliseconds + " ms" : (profilesLoaded && !profiles.length ? "No profiles discovered" : "Connecting…")))
      ),
      h(Card, null, h(CardContent, { className: "pt-4 pb-4", style: css.controls },
        h("div", { style: css.profiles },
          h("span", { style: css.metricLabel }, "Profiles"),
          h(ToggleButton, { active: selected.includes("all"), onClick: function () { toggleProfile("all"); } }, "All"),
          profiles.map(profile => h(ToggleButton, {
            key: profile.name,
            active: selected.includes(profile.name),
            title: (profile.sizeBytes / 1000000).toFixed(1) + " MB",
            onClick: function () { toggleProfile(profile.name); }
          }, profile.name))
        ),
        ["24h", "7d", "30d", "all"].map(value => h(ToggleButton, {
          key: value,
          active: windowName === value,
          onClick: function () { setWindowName(value); setOffset(0); }
        }, value === "all" ? "All time" : value)),
        h(ToggleButton, { active: costBasis === "api-equivalent", onClick: function () { setCostBasis("api-equivalent"); setOffset(0); } }, "API equivalent"),
        h(ToggleButton, { active: costBasis === "recorded", onClick: function () { setCostBasis("recorded"); setOffset(0); } }, "Recorded"),
        h(Input, {
          value: searchDraft,
          onChange: function (event) { setSearchDraft(event.target.value); },
          placeholder: "Search sessions, models, tasks…",
          "aria-label": "Search session usage",
          style: { width: "min(100%, 260px)" }
        }),
        h(Select, {
          value: sort,
          onValueChange: function (value) { setSort(value || "tokens"); setOffset(0); },
          onChange: function (event) {
            const value = event && event.target ? event.target.value : event;
            setSort(value || "tokens");
            setOffset(0);
          },
          "aria-label": "Sort session families"
        },
          h(SelectOption, { value: "tokens" }, "Sort: tokens"),
          h(SelectOption, { value: "cost" }, "Sort: cost"),
          h(SelectOption, { value: "calls" }, "Sort: calls"),
          h(SelectOption, { value: "started" }, "Sort: started")
        ),
        h(Button, { type: "button", variant: "outline", size: "sm", onClick: function () { setRefreshKey(value => value + 1); } }, "Refresh")
      )),
      error ? h("div", { role: "alert", style: css.error }, "Session usage backend unavailable: " + error) : null,
      result ? h("div", { style: css.metrics },
        h(Metric, { label: "Sessions", value: number.format(result.filteredSessions), note: number.format(result.totalSessions) + " stored" }),
        h(Metric, { label: "Accounted tokens", value: short.format(result.accountedTokens), note: "input + cache + output" }),
        h(Metric, { label: "Reasoning", value: short.format(result.reasoningTokens), note: "reported separately" }),
        h(Metric, { label: "API calls", value: short.format(result.apiCalls), note: number.format(result.filteredFamilies) + " families" }),
        h(Metric, {
          label: costBasis === "api-equivalent" ? "API equivalent" : "Recorded estimate",
          value: formatCost(displayedCost),
          note: costBasis === "api-equivalent"
            ? pricingCoverageNote(result)
            : money.format(result.actualCostUsd || 0) + " actual"
        })
      ) : null,
      profilesLoaded && !profiles.length ? h(Card, null, h(CardContent, { className: "pt-4 pb-4" }, "No Hermes profiles discovered. Refresh to retry profile discovery.")) : null,
      result && !result.families.length ? h(Card, null, h(CardContent, { className: "pt-4 pb-4" }, "No session families match this window and filter.")) : null,
      result ? result.families.map(family => h(FamilyCard, {
        key: family.profile + ":" + family.rootSessionId,
        family,
        costBasis
      })) : null,
      result ? h("footer", { style: css.pagination },
        h("div", { style: css.muted }, number.format(first) + "–" + number.format(last) + " of " + number.format(result.filteredFamilies) + " families · snapshot " + dateTime.format(new Date(result.generatedAt))),
        h("div", { style: css.controls },
          h(Button, { type: "button", variant: "outline", size: "sm", disabled: offset === 0, onClick: function () { setOffset(Math.max(0, offset - limit)); } }, "Previous"),
          h(Button, { type: "button", variant: "outline", size: "sm", disabled: offset + limit >= result.filteredFamilies, onClick: function () { setOffset(offset + limit); } }, "Next")
        )
      ) : null
    );
  }

  window.__HERMES_PLUGINS__.register("session-usage", SessionUsagePage);
})();
