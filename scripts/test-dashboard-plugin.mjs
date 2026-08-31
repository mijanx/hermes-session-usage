import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import vm from "node:vm";

const bundle = readFileSync(new URL("../dashboard/dist/index.js", import.meta.url), "utf8");

function sameDeps(left, right) {
  return left && right && left.length === right.length && left.every((value, index) => Object.is(value, right[index]));
}

class HookHarness {
  constructor(component) {
    this.component = component;
    this.states = [];
    this.memos = [];
    this.effects = [];
    this.dirty = true;
    this.tree = null;
    this.hook = 0;
    this.pendingEffects = [];
  }

  useState = initial => {
    const index = this.hook++;
    if (!(index in this.states)) this.states[index] = typeof initial === "function" ? initial() : initial;
    const setState = next => {
      const value = typeof next === "function" ? next(this.states[index]) : next;
      if (!Object.is(value, this.states[index])) {
        this.states[index] = value;
        this.dirty = true;
      }
    };
    return [this.states[index], setState];
  };

  useMemo = (factory, deps) => {
    const index = this.hook++;
    const previous = this.memos[index];
    if (!previous || !sameDeps(previous.deps, deps)) this.memos[index] = { deps, value: factory() };
    return this.memos[index].value;
  };

  useCallback = (callback, deps) => this.useMemo(() => callback, deps);

  useEffect = (effect, deps) => {
    const index = this.hook++;
    const previous = this.effects[index];
    if (!previous || !sameDeps(previous.deps, deps)) this.pendingEffects.push({ index, effect, deps });
  };

  render() {
    this.hook = 0;
    this.pendingEffects = [];
    this.dirty = false;
    this.tree = this.component();
    for (const pending of this.pendingEffects) {
      const previous = this.effects[pending.index];
      if (previous && previous.cleanup) previous.cleanup();
      this.effects[pending.index] = { deps: pending.deps, cleanup: pending.effect() };
    }
  }

  async settle() {
    for (let turn = 0; turn < 30; turn += 1) {
      if (this.dirty) this.render();
      await Promise.resolve();
      await Promise.resolve();
      await new Promise(resolve => setImmediate(resolve));
      if (!this.dirty) return;
    }
    throw new Error("Hook harness did not settle");
  }
}

function element(type, props, ...children) {
  return { type, props: { ...(props || {}), children: children.length === 1 ? children[0] : children }, children };
}

function walk(value, visit) {
  if (Array.isArray(value)) {
    for (const item of value) walk(item, visit);
    return;
  }
  if (!value || typeof value !== "object") return;
  visit(value);
  walk(value.children, visit);
}

function findByText(tree, expected) {
  let match;
  walk(tree, node => {
    if (!match && node.children && node.children.some(child => child === expected)) match = node;
  });
  assert.ok(match, `Could not find control ${expected}`);
  return match;
}

function containsText(tree, expected) {
  let found = false;
  const inspect = value => {
    if (Array.isArray(value)) return value.forEach(inspect);
    if (typeof value === "string" && value.includes(expected)) found = true;
    if (value && typeof value === "object") inspect(value.children);
  };
  inspect(tree);
  return found;
}

function findByComponentName(tree, name) {
  let match;
  walk(tree, node => {
    if (!match && typeof node.type === "function" && node.type.name === name) match = node;
  });
  return match;
}

function result(filteredFamilies = 80) {
  const line = {
    model: "gpt-5.6",
    provider: "openai-codex",
    billingMode: "subscription",
    task: "agent",
    apiCalls: 1,
    inputTokens: 10,
    cacheReadTokens: 0,
    cacheWriteTokens: 0,
    outputTokens: 5,
    reasoningTokens: 2,
    accountedTokens: 15,
    estimatedCostUsd: 0,
    apiEquivalentCostUsd: 0.01
  };
  const session = { id: "session-1", title: "Session one", source: "cli", parentSessionId: null, usageLines: [line] };
  return {
    filteredSessions: 1,
    totalSessions: 1,
    accountedTokens: 15,
    reasoningTokens: 2,
    apiCalls: 1,
    filteredFamilies,
    estimatedCostUsd: 0,
    actualCostUsd: 0,
    apiEquivalentCostUsd: 0.01,
    apiEquivalentPricedTokens: 15,
    apiEquivalentUnpricedTokens: 0,
    queryElapsedMilliseconds: 4,
    generatedAt: "2026-08-31T00:00:00Z",
    families: [{
      profile: "alpha",
      rootSessionId: "session-1",
      rootIncluded: true,
      startedAt: "2026-08-31T00:00:00Z",
      accountedTokens: 15,
      apiCalls: 1,
      estimatedCostUsd: 0,
      apiEquivalentCostUsd: 0.01,
      sessions: [session],
      usageLines: [line]
    }]
  };
}

function loadPlugin(fetchJSON) {
  let registered;
  const hookTarget = { current: null };
  const hooks = {
    useState: (...args) => hookTarget.current.useState(...args),
    useMemo: (...args) => hookTarget.current.useMemo(...args),
    useCallback: (...args) => hookTarget.current.useCallback(...args),
    useEffect: (...args) => hookTarget.current.useEffect(...args)
  };
  const components = Object.fromEntries(["Card", "CardContent", "Badge", "Button", "Input", "Select", "SelectOption"].map(name => [name, name]));
  const context = {
    console,
    Intl,
    Set,
    window: {
      setTimeout: () => 1,
      clearTimeout: () => {},
      __HERMES_PLUGINS__: { register: (_name, component) => { registered = component; } },
      __HERMES_PLUGIN_SDK__: {
        React: { createElement: element },
        fetchJSON,
        hooks,
        components
      }
    }
  };
  vm.runInNewContext(bundle, context, { filename: "dashboard/dist/index.js" });
  assert.equal(typeof registered, "function");
  return { component: registered, hookTarget };
}

function attachHooks(harness, hookTarget) {
  hookTarget.current = harness;
}

async function testRefreshReconcilesProfilesAndQueriesOnce() {
  const profileResponses = [
    [{ name: "alpha", sizeBytes: 1 }, { name: "beta", sizeBytes: 1 }],
    [{ name: "beta", sizeBytes: 1 }]
  ];
  const metricRequests = [];
  const { component, hookTarget } = loadPlugin((path, options) => {
    if (path.endsWith("/profiles")) return Promise.resolve(profileResponses.shift());
    metricRequests.push(JSON.parse(options.body));
    return Promise.resolve(result());
  });
  const harness = new HookHarness(component);
  attachHooks(harness, hookTarget);
  await harness.settle();
  assert.equal(metricRequests.length, 1);
  assert.deepEqual(metricRequests[0].profiles, ["alpha"]);

  const family = findByComponentName(harness.tree, "FamilyCard");
  assert.ok(family, "metrics response must reach the production FamilyCard path");
  const familyHarness = new HookHarness(() => family.type(family.props));
  attachHooks(familyHarness, hookTarget);
  await familyHarness.settle();
  const usage = findByComponentName(familyHarness.tree, "UsageTable");
  assert.ok(usage);
  const usageTree = usage.type(usage.props);
  assert.ok(containsText(usageTree, "openai-codex · subscription"), "provider and billing mode must render together");

  attachHooks(harness, hookTarget);
  findByText(harness.tree, "Next").props.onClick();
  await harness.settle();
  assert.equal(metricRequests.at(-1).offset, 50);

  findByText(harness.tree, "Refresh").props.onClick();
  await harness.settle();
  assert.equal(metricRequests.length, 3, "refresh must issue exactly one additional metrics query");
  assert.deepEqual(metricRequests.at(-1).profiles, ["beta"], "removed profile selection must fall back to a discovered profile");
  assert.equal(metricRequests.at(-1).offset, 0, "refresh must reset pagination");
}

async function testDiscoveryFailureIsNotReportedAsEmpty() {
  const requests = [];
  const { component, hookTarget } = loadPlugin(path => {
    requests.push(path);
    return Promise.reject(new Error("discovery offline"));
  });
  const harness = new HookHarness(component);
  attachHooks(harness, hookTarget);
  await harness.settle();
  assert.equal(requests.filter(path => path.endsWith("/metrics")).length, 0);
  assert.ok(containsText(harness.tree, "Profile discovery failed"));
  assert.ok(containsText(harness.tree, "Profile discovery unavailable: discovery offline"));
  assert.ok(!containsText(harness.tree, "No Hermes profiles discovered"));
  assert.ok(!containsText(harness.tree, "No profiles discovered"));
}

async function testFailedRefreshDoesNotQueryStaleProfiles() {
  let profileRequests = 0;
  let metrics = 0;
  const { component, hookTarget } = loadPlugin(path => {
    if (path.endsWith("/profiles")) {
      profileRequests += 1;
      return profileRequests === 1
        ? Promise.resolve([{ name: "alpha", sizeBytes: 1 }])
        : Promise.reject(new Error("refresh discovery offline"));
    }
    metrics += 1;
    return Promise.resolve(result(1));
  });
  const harness = new HookHarness(component);
  attachHooks(harness, hookTarget);
  await harness.settle();
  assert.equal(metrics, 1);

  findByText(harness.tree, "Refresh").props.onClick();
  await harness.settle();
  assert.equal(metrics, 1, "failed discovery must not query a stale selected profile");
  assert.ok(containsText(harness.tree, "Profile discovery failed"));
  assert.ok(!containsText(harness.tree, "Live ·"));
  assert.equal(findByComponentName(harness.tree, "FamilyCard"), undefined);
}

async function testFailedMetricsDoesNotLeaveLiveStaleResult() {
  let metrics = 0;
  const { component, hookTarget } = loadPlugin((path) => {
    if (path.endsWith("/profiles")) return Promise.resolve([{ name: "alpha", sizeBytes: 1 }]);
    metrics += 1;
    return metrics === 1 ? Promise.resolve(result(1)) : Promise.reject(new Error("metrics offline"));
  });
  const harness = new HookHarness(component);
  attachHooks(harness, hookTarget);
  await harness.settle();
  assert.ok(containsText(harness.tree, "Live · 4 ms"));
  assert.ok(findByComponentName(harness.tree, "FamilyCard"));

  findByText(harness.tree, "7d").props.onClick();
  await harness.settle();
  assert.equal(metrics, 2);
  assert.ok(containsText(harness.tree, "Session usage backend unavailable: metrics offline"));
  assert.ok(!containsText(harness.tree, "Live ·"));
  assert.equal(findByComponentName(harness.tree, "FamilyCard"), undefined, "failed request must not render stale families");
}

await testRefreshReconcilesProfilesAndQueriesOnce();
await testDiscoveryFailureIsNotReportedAsEmpty();
await testFailedRefreshDoesNotQueryStaleProfiles();
await testFailedMetricsDoesNotLeaveLiveStaleResult();
console.log("dashboard plugin behavior: PASS");