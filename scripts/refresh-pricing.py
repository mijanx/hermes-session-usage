#!/usr/bin/env python3
"""Refresh models.dev fallbacks, then apply audited official-provider prices."""

from __future__ import annotations

import argparse
import datetime as dt
import json
import pathlib
import urllib.request

SOURCE_URL = "https://models.dev/api.json"
OFFICIAL_PRICING_PATH = pathlib.Path("HermesSessionMetrics.Web/data/official-provider-pricing.json")
# First match wins when the same model is offered by multiple direct providers.
DIRECT_PROVIDERS = (
    "openai",
    "xai",
    "deepseek",
    "anthropic",
    "google",
    "mistral",
    "minimax",
    "zhipuai",
    "moonshotai",
    "alibaba",
)


def fetch(url: str) -> dict:
    request = urllib.request.Request(url, headers={"User-Agent": "hermes-session-usage-pricing/1.0"})
    with urllib.request.urlopen(request, timeout=60) as response:
        return json.load(response)


def _validate_official(official: dict) -> None:
    sources = official.get("sources", [])
    source_ids = [source.get("id") for source in sources]
    normalized_source_ids = [source_id.casefold() for source_id in source_ids if source_id]
    if any(not source_id for source_id in source_ids) or len(normalized_source_ids) != len(set(normalized_source_ids)):
        raise ValueError("Official pricing sources must have unique non-empty IDs case-insensitively")

    known_sources = set(normalized_source_ids)
    model_ids: set[str] = set()
    required_rates = (
        "inputPerMillion",
        "cacheReadPerMillion",
        "cacheWritePerMillion",
        "outputPerMillion",
    )
    for entry in official.get("models", []):
        model_id = entry.get("model", "").casefold()
        if not model_id or model_id in model_ids:
            raise ValueError("Official pricing model IDs must be unique case-insensitively")
        model_ids.add(model_id)
        if not entry.get("provider") or not entry.get("basis"):
            raise ValueError(f"Official pricing entry {model_id} lacks provider or basis")
        referenced_sources = {source_id.casefold() for source_id in entry.get("sourceIds", [])}
        if not referenced_sources or not referenced_sources.issubset(known_sources):
            raise ValueError(f"Official pricing entry {model_id} has invalid source IDs")
        if any(not isinstance(entry.get(rate), (int, float)) or entry[rate] < 0 for rate in required_rates):
            raise ValueError(f"Official pricing entry {model_id} has invalid rates")


def create_snapshot(
    catalog: dict,
    source_url: str,
    official: dict | None = None,
    retrieved_at: str | None = None,
) -> dict:
    selected: dict[str, dict] = {}
    for provider_id in DIRECT_PROVIDERS:
        provider = catalog.get(provider_id, {})
        for model_id, model in provider.get("models", {}).items():
            cost = model.get("cost") or {}
            input_rate = cost.get("input")
            output_rate = cost.get("output")
            if input_rate is None or output_rate is None or (input_rate == 0 and output_rate == 0):
                continue
            selected.setdefault(model_id.casefold(), {
                "model": model_id,
                "provider": provider_id,
                "inputPerMillion": input_rate,
                "cacheReadPerMillion": cost.get("cache_read", input_rate),
                "cacheWritePerMillion": cost.get("cache_write", input_rate),
                "outputPerMillion": output_rate,
                "sourceIds": ["models-dev"],
                "basis": "Direct API rates from the models.dev fallback catalog",
            })

    timestamp = retrieved_at or dt.datetime.now(dt.timezone.utc).replace(microsecond=0).isoformat().replace("+00:00", "Z")
    official = official or {"sources": [], "models": []}
    if official.get("sources") or official.get("models"):
        _validate_official(official)
    for entry in official.get("models", []):
        selected[entry["model"].casefold()] = entry

    sources = [
        {**source, "retrievedAt": official.get("retrievedAt", timestamp)}
        for source in official.get("sources", [])
    ]
    sources.append({
        "id": "models-dev",
        "name": "models.dev fallback",
        "url": source_url,
        "retrievedAt": timestamp,
        "basis": "used only for models without an official-provider override",
    })

    return {
        "schemaVersion": 2,
        "source": {
            "id": "merged-snapshot",
            "name": "Official provider pricing with models.dev fallback",
            "url": "data/api-pricing.json",
            "retrievedAt": timestamp,
            "basis": "Compatibility summary; inspect sources and each model's sourceIds for provenance",
        },
        "sources": sources,
        "models": sorted(selected.values(), key=lambda item: item["model"].casefold()),
    }


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("--source", default=SOURCE_URL)
    parser.add_argument("--official", default=str(OFFICIAL_PRICING_PATH))
    parser.add_argument("--output", default="HermesSessionMetrics.Web/data/api-pricing.json")
    args = parser.parse_args()

    official = json.loads(pathlib.Path(args.official).read_text(encoding="utf-8"))
    snapshot = create_snapshot(fetch(args.source), args.source, official)
    output = pathlib.Path(args.output)
    output.parent.mkdir(parents=True, exist_ok=True)
    output.write_text(json.dumps(snapshot, indent=2, ensure_ascii=False) + "\n", encoding="utf-8")
    print(f"Wrote {len(snapshot['models'])} model prices to {output}")


if __name__ == "__main__":
    main()
