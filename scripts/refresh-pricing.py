#!/usr/bin/env python3
"""Refresh the checked-in direct-provider API pricing snapshot from models.dev."""

from __future__ import annotations

import argparse
import datetime as dt
import json
import pathlib
import urllib.request

SOURCE_URL = "https://models.dev/api.json"
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


def create_snapshot(catalog: dict, source_url: str) -> dict:
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
            })

    return {
        "schemaVersion": 1,
        "source": {
            "name": "models.dev direct-provider pricing",
            "url": source_url,
            "retrievedAt": dt.datetime.now(dt.timezone.utc).replace(microsecond=0).isoformat().replace("+00:00", "Z"),
        },
        "models": sorted(selected.values(), key=lambda item: item["model"].casefold()),
    }


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("--source", default=SOURCE_URL)
    parser.add_argument("--output", default="HermesSessionMetrics.Web/data/api-pricing.json")
    args = parser.parse_args()

    snapshot = create_snapshot(fetch(args.source), args.source)
    output = pathlib.Path(args.output)
    output.parent.mkdir(parents=True, exist_ok=True)
    output.write_text(json.dumps(snapshot, indent=2, ensure_ascii=False) + "\n", encoding="utf-8")
    print(f"Wrote {len(snapshot['models'])} model prices to {output}")


if __name__ == "__main__":
    main()
