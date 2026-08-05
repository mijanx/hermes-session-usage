"""Read-only Hermes session-usage API for the desktop plugin.

The module deliberately uses only the Python standard library plus FastAPI,
which is already provided by Hermes. SQLite connections use mode=ro and the
queries never read message contents.
"""

from __future__ import annotations

import json
import sqlite3
import time
from collections import defaultdict
from contextlib import closing
from datetime import datetime, timezone
from functools import lru_cache
from pathlib import Path
from typing import Any, Iterable

from fastapi import APIRouter, HTTPException
from hermes_constants import get_hermes_home

router = APIRouter()

_ALLOWED_WINDOWS: dict[str, int | None] = {
    "24h": 24,
    "7d": 168,
    "30d": 720,
    "all": None,
}
_ALLOWED_SORTS = {"tokens", "started", "cost", "calls"}
_ALLOWED_COST_BASES = {"api-equivalent", "recorded"}
_PLUGIN_ROOT = Path(__file__).resolve().parent.parent
_PRICING_PATH = _PLUGIN_ROOT / "HermesSessionMetrics.Web" / "data" / "api-pricing.json"


def _base_hermes_home() -> Path:
    """Return the root containing state.db and profiles/, even in a named profile."""
    home = Path(get_hermes_home()).resolve()
    if home.parent.name.casefold() == "profiles":
        return home.parent.parent
    return home


def discover_profiles(root: Path | None = None) -> list[dict[str, Any]]:
    """Discover only live default and immediate named-profile databases."""
    root = (root or _base_hermes_home()).resolve()
    found: list[dict[str, Any]] = []

    def add(name: str, path: Path) -> None:
        if path.is_file():
            found.append({"name": name, "path": path.resolve(), "sizeBytes": path.stat().st_size})

    add("default", root / "state.db")
    profiles_root = root / "profiles"
    if profiles_root.is_dir():
        for directory in profiles_root.iterdir():
            if not directory.is_dir() or "quarantine" in directory.name.casefold():
                continue
            add(directory.name, directory / "state.db")

    return sorted(found, key=lambda item: (item["name"] != "default", item["name"].casefold()))


@lru_cache(maxsize=4)
def _load_pricing_cached(path: str, modified_ns: int) -> dict[str, Any]:
    del modified_ns
    document = json.loads(Path(path).read_text(encoding="utf-8"))
    if document.get("schemaVersion") not in {1, 2}:
        raise ValueError(f"Unsupported pricing schema {document.get('schemaVersion')!r}")

    sources = document.get("sources") or []
    source_ids = [source.get("id") for source in sources]
    normalized_ids = [value.casefold() for value in source_ids if isinstance(value, str) and value]
    if len(normalized_ids) != len(set(normalized_ids)):
        raise ValueError("Pricing source IDs must be unique")

    known_ids = set(normalized_ids)
    entries: dict[str, dict[str, Any]] = {}
    for entry in document.get("models") or []:
        model = str(entry.get("model") or "").strip()
        rates = [
            entry.get("inputPerMillion"),
            entry.get("cacheReadPerMillion"),
            entry.get("cacheWritePerMillion"),
            entry.get("outputPerMillion"),
        ]
        if not model or any(not isinstance(rate, (int, float)) or rate < 0 for rate in rates):
            raise ValueError("Pricing document contains an invalid model entry")
        references = entry.get("sourceIds") or []
        if any(str(reference).casefold() not in known_ids for reference in references):
            raise ValueError(f"Pricing model {model!r} references an unknown source")
        if document["schemaVersion"] == 2 and (not references or not entry.get("basis")):
            raise ValueError(f"Pricing model {model!r} lacks provenance")
        key = model.casefold()
        if key in entries:
            raise ValueError(f"Duplicate pricing model {model!r}")
        entries[key] = entry

    return {"document": document, "entries": entries}


def load_pricing(path: Path | None = None) -> dict[str, Any]:
    path = (path or _PRICING_PATH).resolve()
    return _load_pricing_cached(str(path), path.stat().st_mtime_ns)


def _estimate(
    pricing: dict[str, Any],
    model: str,
    input_tokens: int,
    cache_read_tokens: int,
    cache_write_tokens: int,
    output_tokens: int,
) -> tuple[float, str] | None:
    entry = pricing["entries"].get(model.casefold())
    if entry is None:
        return None
    cost = (
        input_tokens * entry["inputPerMillion"]
        + cache_read_tokens * entry["cacheReadPerMillion"]
        + cache_write_tokens * entry["cacheWritePerMillion"]
        + output_tokens * entry["outputPerMillion"]
    ) / 1_000_000
    return float(cost), str(entry.get("provider") or "")


def _iso_timestamp(value: float | int | None) -> str | None:
    if value is None:
        return None
    return datetime.fromtimestamp(float(value), timezone.utc).isoformat().replace("+00:00", "Z")


def _labels(values: Iterable[str | None], *, unattributed: bool = False) -> str:
    by_key: dict[str, str] = {}
    for value in values:
        parts = [part.strip() for part in str(value or "").split(",") if part.strip()]
        if not parts and unattributed:
            parts = ["unattributed"]
        for part in parts:
            by_key.setdefault(part.casefold(), part)
    return ", ".join(
        sorted(by_key.values(), key=lambda label: (label.casefold() == "unattributed", label.casefold()))
    )


def _optional_labels(values: Iterable[str | None]) -> str | None:
    merged = _labels(values)
    return merged or None


def _session_totals_line(api_calls: int) -> dict[str, Any]:
    return {
        "model": "session totals",
        "provider": "unattributed",
        "billingMode": "",
        "task": "unattributed",
        "apiCalls": api_calls,
        "inputTokens": 0,
        "cacheReadTokens": 0,
        "cacheWriteTokens": 0,
        "outputTokens": 0,
        "reasoningTokens": 0,
        "accountedTokens": 0,
        "estimatedCostUsd": 0.0,
        "actualCostUsd": 0.0,
        "apiEquivalentCostUsd": None,
        "apiEquivalentPricingProvider": None,
        "costStatus": "session-only",
        "costSource": "sessions.api_call_count",
    }


def _merge_usage(lines: Iterable[dict[str, Any]]) -> list[dict[str, Any]]:
    groups: dict[tuple[str, str], list[dict[str, Any]]] = defaultdict(list)
    order: dict[tuple[str, str], tuple[str, str]] = {}
    for line in lines:
        key = (line["model"].casefold(), line["task"].casefold())
        groups[key].append(line)
        order.setdefault(key, (line["model"], line["task"]))

    merged: list[dict[str, Any]] = []
    for key, members in groups.items():
        model, task = order[key]
        priced = all(member.get("apiEquivalentCostUsd") is not None for member in members)
        merged.append(
            {
                "model": model,
                "provider": _labels((member.get("provider") for member in members), unattributed=True),
                "billingMode": _labels(member.get("billingMode") for member in members),
                "task": task,
                "apiCalls": sum(int(member.get("apiCalls") or 0) for member in members),
                "inputTokens": sum(int(member.get("inputTokens") or 0) for member in members),
                "cacheReadTokens": sum(int(member.get("cacheReadTokens") or 0) for member in members),
                "cacheWriteTokens": sum(int(member.get("cacheWriteTokens") or 0) for member in members),
                "outputTokens": sum(int(member.get("outputTokens") or 0) for member in members),
                "reasoningTokens": sum(int(member.get("reasoningTokens") or 0) for member in members),
                "accountedTokens": sum(int(member.get("accountedTokens") or 0) for member in members),
                "estimatedCostUsd": sum(float(member.get("estimatedCostUsd") or 0) for member in members),
                "actualCostUsd": sum(float(member.get("actualCostUsd") or 0) for member in members),
                "apiEquivalentCostUsd": (
                    sum(float(member["apiEquivalentCostUsd"]) for member in members) if priced else None
                ),
                "apiEquivalentPricingProvider": _optional_labels(
                    member.get("apiEquivalentPricingProvider") for member in members
                ),
                "costStatus": _optional_labels(member.get("costStatus") for member in members),
                "costSource": _optional_labels(member.get("costSource") for member in members),
            }
        )
    return sorted(merged, key=lambda line: (-line["accountedTokens"], line["model"], line["task"]))


def _open_readonly(path: Path) -> sqlite3.Connection:
    connection = sqlite3.connect(f"file:{path.as_posix()}?mode=ro", uri=True, timeout=5)
    connection.row_factory = sqlite3.Row
    connection.execute("PRAGMA query_only=ON")
    connection.execute("PRAGMA busy_timeout=5000")
    return connection


def _has_table(connection: sqlite3.Connection, name: str) -> bool:
    row = connection.execute(
        "SELECT 1 FROM sqlite_master WHERE type='table' AND name=? LIMIT 1", (name,)
    ).fetchone()
    return row is not None


def _build_session(builder: dict[str, Any]) -> dict[str, Any]:
    lines = _merge_usage(builder.pop("rawUsage"))
    residual_calls = max(0, builder.pop("sessionApiCalls") - sum(line["apiCalls"] for line in lines))
    if residual_calls:
        lines = _merge_usage([*lines, _session_totals_line(residual_calls)])

    ended_at = builder["endedAt"]
    end_reason = builder.pop("endReason")
    builder.update(
        {
            "isChild": bool(builder["parentSessionId"]),
            "status": "active" if ended_at is None else (end_reason or "completed"),
            "accountedTokens": sum(line["accountedTokens"] for line in lines),
            "reasoningTokens": sum(line["reasoningTokens"] for line in lines),
            "apiCalls": sum(line["apiCalls"] for line in lines),
            "estimatedCostUsd": sum(line["estimatedCostUsd"] for line in lines),
            "actualCostUsd": sum(line["actualCostUsd"] for line in lines),
            "apiEquivalentCostUsd": sum(
                line["apiEquivalentCostUsd"] or 0 for line in lines
            ),
            "apiEquivalentPricedTokens": sum(
                line["accountedTokens"] for line in lines if line["apiEquivalentCostUsd"] is not None
            ),
            "apiEquivalentUnpricedTokens": sum(
                line["accountedTokens"] for line in lines if line["apiEquivalentCostUsd"] is None
            ),
            "usageLines": lines,
        }
    )
    return builder


def _query_profile(
    profile: dict[str, Any], cutoff: float | None, search: str, pricing: dict[str, Any]
) -> tuple[int, list[dict[str, Any]], dict[str, str | None]]:
    # sqlite3.Connection's context manager commits/rolls back but does not
    # close. Closing explicitly matters on Windows, where an open read handle
    # prevents profile databases (and test fixtures) from being moved/deleted.
    with closing(_open_readonly(profile["path"])) as connection:
        if not _has_table(connection, "sessions") or not _has_table(connection, "session_model_usage"):
            return 0, [], {}

        parent_index = {
            str(row["id"]): row["parent_session_id"]
            for row in connection.execute("SELECT id, parent_session_id FROM sessions")
        }
        pattern = f"%{search}%"
        rows = connection.execute(
            """
            WITH matching_sessions AS (
                SELECT s.id, s.source, s.model, s.parent_session_id, s.started_at, s.ended_at,
                       s.end_reason, s.message_count, s.tool_call_count, s.api_call_count,
                       COALESCE(NULLIF(s.title, ''), NULLIF(s.display_name, ''), s.id) AS title
                FROM sessions s
                WHERE (
                    :cutoff IS NULL OR s.started_at >= :cutoff OR EXISTS (
                        SELECT 1 FROM session_model_usage recent
                        WHERE recent.session_id = s.id
                          AND COALESCE(recent.last_seen, recent.first_seen, s.started_at) >= :cutoff
                    )
                )
                AND (
                    :search = '' OR s.id LIKE :pattern OR COALESCE(s.title, '') LIKE :pattern
                    OR COALESCE(s.display_name, '') LIKE :pattern OR COALESCE(s.source, '') LIKE :pattern
                    OR COALESCE(s.model, '') LIKE :pattern OR EXISTS (
                        SELECT 1 FROM session_model_usage searched
                        WHERE searched.session_id = s.id
                          AND (searched.model LIKE :pattern OR searched.task LIKE :pattern
                               OR searched.billing_provider LIKE :pattern)
                    )
                )
            )
            SELECT s.id, s.source, s.model AS primary_model, s.parent_session_id, s.started_at,
                   s.ended_at, s.end_reason, s.message_count, s.tool_call_count,
                   s.api_call_count AS session_api_calls, s.title,
                   u.model, u.billing_provider, u.billing_mode, u.task,
                   u.api_call_count AS usage_api_calls, u.input_tokens, u.cache_read_tokens,
                   u.cache_write_tokens, u.output_tokens, u.reasoning_tokens,
                   u.estimated_cost_usd, u.actual_cost_usd, u.cost_status, u.cost_source
            FROM matching_sessions s
            LEFT JOIN session_model_usage u ON u.session_id = s.id
            ORDER BY s.started_at DESC
            """,
            {"cutoff": cutoff, "search": search, "pattern": pattern},
        )

        builders: dict[str, dict[str, Any]] = {}
        for row in rows:
            session_id = str(row["id"])
            if session_id not in builders:
                builders[session_id] = {
                    "profile": profile["name"],
                    "id": session_id,
                    "source": row["source"] or "unknown",
                    "title": row["title"] or session_id,
                    "primaryModel": row["primary_model"] or "unknown",
                    "parentSessionId": row["parent_session_id"],
                    "startedAt": _iso_timestamp(row["started_at"]),
                    "startedAtUnix": float(row["started_at"]),
                    "endedAt": _iso_timestamp(row["ended_at"]),
                    "endReason": row["end_reason"],
                    "messageCount": int(row["message_count"] or 0),
                    "toolCallCount": int(row["tool_call_count"] or 0),
                    "sessionApiCalls": int(row["session_api_calls"] or 0),
                    "rawUsage": [],
                }
            if row["model"] is None:
                continue
            input_tokens = int(row["input_tokens"] or 0)
            cache_read_tokens = int(row["cache_read_tokens"] or 0)
            cache_write_tokens = int(row["cache_write_tokens"] or 0)
            output_tokens = int(row["output_tokens"] or 0)
            estimate = _estimate(
                pricing,
                str(row["model"]),
                input_tokens,
                cache_read_tokens,
                cache_write_tokens,
                output_tokens,
            )
            builders[session_id]["rawUsage"].append(
                {
                    "model": str(row["model"]),
                    "provider": row["billing_provider"] or "",
                    "billingMode": row["billing_mode"] or "",
                    "task": (row["task"] or "").strip() or "agent",
                    "apiCalls": int(row["usage_api_calls"] or 0),
                    "inputTokens": input_tokens,
                    "cacheReadTokens": cache_read_tokens,
                    "cacheWriteTokens": cache_write_tokens,
                    "outputTokens": output_tokens,
                    "reasoningTokens": int(row["reasoning_tokens"] or 0),
                    "accountedTokens": input_tokens
                    + cache_read_tokens
                    + cache_write_tokens
                    + output_tokens,
                    "estimatedCostUsd": float(row["estimated_cost_usd"] or 0),
                    "actualCostUsd": float(row["actual_cost_usd"] or 0),
                    "apiEquivalentCostUsd": estimate[0] if estimate else None,
                    "apiEquivalentPricingProvider": estimate[1] if estimate else None,
                    "costStatus": row["cost_status"],
                    "costSource": row["cost_source"],
                }
            )

    return len(parent_index), [_build_session(builder) for builder in builders.values()], parent_index


def _resolve_root(
    session_id: str, parent_index: dict[str, str | None], cache: dict[str, str]
) -> str:
    if session_id in cache:
        return cache[session_id]
    path: list[str] = []
    positions: dict[str, int] = {}
    current = session_id
    while True:
        if current in cache:
            root = cache[current]
            break
        if current in positions:
            root = min(path[positions[current] :])
            break
        positions[current] = len(path)
        path.append(current)
        parent = parent_index.get(current)
        if not parent:
            root = current
            break
        current = parent
    for item in path:
        cache[item] = root
    return root


def _order_family_members(root_id: str, members: list[dict[str, Any]]) -> list[dict[str, Any]]:
    by_id = {member["id"]: member for member in members}
    by_parent: dict[str, list[dict[str, Any]]] = defaultdict(list)
    for member in members:
        if member["parentSessionId"]:
            by_parent[member["parentSessionId"]].append(member)
    for children in by_parent.values():
        children.sort(key=lambda child: (child["startedAtUnix"], child["id"]))

    ordered: list[dict[str, Any]] = []
    visited: set[str] = set()

    def visit(member: dict[str, Any]) -> None:
        if member["id"] in visited:
            return
        visited.add(member["id"])
        ordered.append(member)
        for child in by_parent.get(member["id"], []):
            visit(child)

    if root_id in by_id:
        visit(by_id[root_id])
    else:
        for child in by_parent.get(root_id, []):
            visit(child)
    for member in sorted(members, key=lambda item: (item["startedAtUnix"], item["id"])):
        visit(member)
    return ordered


def _build_families(
    sessions: list[dict[str, Any]], parent_indexes: dict[str, dict[str, str | None]]
) -> list[dict[str, Any]]:
    by_profile: dict[str, list[dict[str, Any]]] = defaultdict(list)
    for session in sessions:
        by_profile[session["profile"]].append(session)

    families: list[dict[str, Any]] = []
    for profile, profile_sessions in by_profile.items():
        cache: dict[str, str] = {}
        grouped: dict[str, list[dict[str, Any]]] = defaultdict(list)
        for session in profile_sessions:
            grouped[_resolve_root(session["id"], parent_indexes[profile], cache)].append(session)
        for root_id, unordered in grouped.items():
            members = _order_family_members(root_id, unordered)
            root = next((member for member in members if member["id"] == root_id), None)
            usage = _merge_usage(line for member in members for line in member["usageLines"])
            api_calls = sum(member["apiCalls"] for member in members)
            residual_calls = max(0, api_calls - sum(line["apiCalls"] for line in usage))
            if residual_calls:
                usage = _merge_usage([*usage, _session_totals_line(residual_calls)])
            families.append(
                {
                    "profile": profile,
                    "rootSessionId": root_id,
                    "rootIncluded": root is not None,
                    "startedAt": root["startedAt"] if root else min(members, key=lambda x: x["startedAtUnix"])["startedAt"],
                    "startedAtUnix": root["startedAtUnix"] if root else min(member["startedAtUnix"] for member in members),
                    "accountedTokens": sum(member["accountedTokens"] for member in members),
                    "reasoningTokens": sum(member["reasoningTokens"] for member in members),
                    "apiCalls": api_calls,
                    "estimatedCostUsd": sum(member["estimatedCostUsd"] for member in members),
                    "actualCostUsd": sum(member["actualCostUsd"] for member in members),
                    "apiEquivalentCostUsd": sum(member["apiEquivalentCostUsd"] for member in members),
                    "apiEquivalentPricedTokens": sum(member["apiEquivalentPricedTokens"] for member in members),
                    "apiEquivalentUnpricedTokens": sum(member["apiEquivalentUnpricedTokens"] for member in members),
                    "sessions": members,
                    "usageLines": usage,
                }
            )
    return families


def _order_families(
    families: list[dict[str, Any]], sort: str, descending: bool, cost_basis: str
) -> list[dict[str, Any]]:
    metric = {
        "started": lambda family: family["startedAtUnix"],
        "cost": lambda family: (
            family["apiEquivalentCostUsd"]
            if cost_basis == "api-equivalent"
            else family["estimatedCostUsd"]
        ),
        "calls": lambda family: family["apiCalls"],
        "tokens": lambda family: family["accountedTokens"],
    }[sort]
    ordered = sorted(families, key=lambda family: (family["profile"], family["rootSessionId"]))
    ordered = sorted(ordered, key=lambda family: family["startedAtUnix"], reverse=descending)
    return sorted(ordered, key=metric, reverse=descending)


def query_metrics(body: dict[str, Any], root: Path | None = None, now: float | None = None) -> dict[str, Any]:
    """Query session families. This is also the testable core behind POST /metrics."""
    started = time.perf_counter()
    window = str(body.get("window") or "24h").casefold()
    sort = str(body.get("sort") or "tokens").casefold()
    cost_basis = str(body.get("costBasis") or "api-equivalent").casefold()
    if window not in _ALLOWED_WINDOWS:
        raise ValueError("Window must be 24h, 7d, 30d, or all.")
    if sort not in _ALLOWED_SORTS:
        raise ValueError("Sort must be tokens, started, cost, or calls.")
    if cost_basis not in _ALLOWED_COST_BASES:
        raise ValueError("Cost basis must be api-equivalent or recorded.")

    available = discover_profiles(root)
    requested = body.get("profiles")
    if isinstance(requested, list):
        requested_names = {str(name).casefold() for name in requested}
    else:
        default = available[0]["name"] if available else ""
        requested_names = {
            name.strip().casefold() for name in str(requested or default).split(",") if name.strip()
        }
    selected = available if "all" in requested_names else [
        profile for profile in available if profile["name"].casefold() in requested_names
    ]
    if not selected:
        raise ValueError("No valid profiles selected.")

    current = float(now if now is not None else time.time())
    hours = _ALLOWED_WINDOWS[window]
    cutoff = current - hours * 3600 if hours is not None else None
    search = str(body.get("search") or "").strip()
    limit = min(1000, max(1, int(body.get("limit") or 100)))
    offset = max(0, int(body.get("offset") or 0))
    descending = bool(body.get("descending", True))
    pricing = load_pricing()

    total_sessions = 0
    sessions: list[dict[str, Any]] = []
    parent_indexes: dict[str, dict[str, str | None]] = {}
    for profile in selected:
        total, matches, parents = _query_profile(profile, cutoff, search, pricing)
        total_sessions += total
        sessions.extend(matches)
        parent_indexes[profile["name"]] = parents

    families = _order_families(_build_families(sessions, parent_indexes), sort, descending, cost_basis)
    page = families[offset : offset + limit]
    for family in page:
        family.pop("startedAtUnix", None)
        for session in family["sessions"]:
            session.pop("startedAtUnix", None)

    document = pricing["document"]
    elapsed_ms = round((time.perf_counter() - started) * 1000)
    return {
        "schemaVersion": 2,
        "generatedAt": _iso_timestamp(current),
        "cutoff": _iso_timestamp(cutoff),
        "hours": hours,
        "profiles": [profile["name"] for profile in selected],
        "totalSessions": total_sessions,
        "filteredSessions": len(sessions),
        "filteredFamilies": len(families),
        "accountedTokens": sum(session["accountedTokens"] for session in sessions),
        "reasoningTokens": sum(session["reasoningTokens"] for session in sessions),
        "apiCalls": sum(session["apiCalls"] for session in sessions),
        "estimatedCostUsd": sum(session["estimatedCostUsd"] for session in sessions),
        "actualCostUsd": sum(session["actualCostUsd"] for session in sessions),
        "apiEquivalentCostUsd": sum(session["apiEquivalentCostUsd"] for session in sessions),
        "apiEquivalentPricedTokens": sum(session["apiEquivalentPricedTokens"] for session in sessions),
        "apiEquivalentUnpricedTokens": sum(session["apiEquivalentUnpricedTokens"] for session in sessions),
        "apiPricingSource": document.get("source"),
        "families": page,
        "queryElapsedMilliseconds": elapsed_ms,
    }


@router.get("/health")
def health() -> dict[str, Any]:
    return {"status": "ok", "time": _iso_timestamp(time.time())}


@router.get("/profiles")
def profiles() -> list[dict[str, Any]]:
    return [
        {"name": profile["name"], "sizeBytes": profile["sizeBytes"]}
        for profile in discover_profiles()
    ]


@router.get("/pricing")
def pricing() -> dict[str, Any]:
    document = load_pricing()["document"]
    return {
        "source": document.get("source"),
        "sources": document.get("sources") or [],
        "models": sorted(document.get("models") or [], key=lambda item: item["model"].casefold()),
        "modelCount": len(document.get("models") or []),
    }


@router.post("/metrics")
def metrics(body: dict[str, Any]) -> dict[str, Any]:
    try:
        return query_metrics(body)
    except (ValueError, TypeError, OverflowError) as exc:
        raise HTTPException(status_code=400, detail=str(exc)) from exc
    except (OSError, sqlite3.Error, json.JSONDecodeError) as exc:
        raise HTTPException(status_code=503, detail=f"Session usage data is unavailable: {exc}") from exc
