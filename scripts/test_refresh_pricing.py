#!/usr/bin/env python3

import importlib.util
import pathlib
import unittest

SCRIPT_PATH = pathlib.Path(__file__).with_name("refresh-pricing.py")
SPEC = importlib.util.spec_from_file_location("refresh_pricing", SCRIPT_PATH)
assert SPEC and SPEC.loader
refresh_pricing = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(refresh_pricing)


class CreateSnapshotTests(unittest.TestCase):
    def test_official_provider_prices_override_models_dev_and_keep_provenance(self) -> None:
        catalog = {
            "openai": {
                "models": {
                    "GPT-Test": {
                        "cost": {
                            "input": 99,
                            "cache_read": 98,
                            "cache_write": 97,
                            "output": 96,
                        }
                    },
                    "fallback-only": {
                        "cost": {
                            "input": 3,
                            "cache_read": 0.3,
                            "output": 9,
                        }
                    },
                }
            }
        }
        official = {
            "retrievedAt": "2026-07-31T09:18:47Z",
            "sources": [
                {
                    "id": "openai-pricing",
                    "name": "OpenAI API pricing",
                    "url": "https://developers.openai.com/api/docs/pricing.md",
                    "provider": "openai",
                    "basis": "standard short-context rates",
                }
            ],
            "models": [
                {
                    "model": "gpt-test",
                    "provider": "openai",
                    "inputPerMillion": 5,
                    "cacheReadPerMillion": 0.5,
                    "cacheWritePerMillion": 6.25,
                    "outputPerMillion": 30,
                    "sourceIds": ["openai-pricing"],
                    "basis": "standard",
                },
                {
                    "model": "official-only",
                    "provider": "openai",
                    "inputPerMillion": 1,
                    "cacheReadPerMillion": 0.1,
                    "cacheWritePerMillion": 1,
                    "outputPerMillion": 2,
                    "sourceIds": ["openai-pricing"],
                    "basis": "standard",
                },
            ],
        }

        snapshot = refresh_pricing.create_snapshot(
            catalog,
            "https://models.dev/api.json",
            official,
            retrieved_at="2026-07-31T09:18:47Z",
        )

        models = {entry["model"]: entry for entry in snapshot["models"]}
        self.assertEqual(5, models["gpt-test"]["inputPerMillion"])
        self.assertEqual(30, models["gpt-test"]["outputPerMillion"])
        self.assertEqual(["openai-pricing"], models["gpt-test"]["sourceIds"])
        self.assertIn("official-only", models)
        self.assertIn("fallback-only", models)
        self.assertEqual(["models-dev"], models["fallback-only"]["sourceIds"])
        self.assertEqual(3, models["fallback-only"]["cacheWritePerMillion"])
        self.assertEqual("openai", snapshot["sources"][0]["provider"])
        self.assertEqual("models.dev fallback", snapshot["sources"][-1]["name"])
        self.assertEqual("models-dev", snapshot["sources"][-1]["id"])
        self.assertEqual("merged-snapshot", snapshot["source"]["id"])
        self.assertEqual("2026-07-31T09:18:47Z", snapshot["sources"][-1]["retrievedAt"])

    def test_official_provider_file_contains_documented_rates(self) -> None:
        import json

        official_path = SCRIPT_PATH.parents[1] / "HermesSessionMetrics.Web/data/official-provider-pricing.json"
        document = json.loads(official_path.read_text(encoding="utf-8"))
        prices = {entry["model"]: entry for entry in document["models"]}

        expected = {
            "gpt-5.6-sol": (5, 0.5, 6.25, 30),
            "gpt-5.3-codex": (1.75, 0.175, 1.75, 14),
            "grok-4.5": (2, 0.3, 2, 6),
            "kimi-k2.6": (0.95, 0.16, 0.95, 4),
            "MiniMax-M3": (0.3, 0.06, 0.3, 1.2),
            "MiniMax-M2.1-highspeed": (0.6, 0.03, 0.375, 2.4),
        }
        for model, rates in expected.items():
            entry = prices[model]
            self.assertEqual(
                rates,
                (
                    entry["inputPerMillion"],
                    entry["cacheReadPerMillion"],
                    entry["cacheWritePerMillion"],
                    entry["outputPerMillion"],
                ),
                model,
            )

        source_ids = {source["id"] for source in document["sources"]}
        self.assertEqual(len(document["sources"]), len(source_ids))
        self.assertEqual(
            len(document["models"]),
            len({entry["model"].casefold() for entry in document["models"]}),
        )
        for entry in document["models"]:
            self.assertTrue(entry["basis"], entry["model"])
            self.assertTrue(entry["sourceIds"], entry["model"])
            self.assertTrue(set(entry["sourceIds"]).issubset(source_ids), entry["model"])
            for field in (
                "inputPerMillion",
                "cacheReadPerMillion",
                "cacheWritePerMillion",
                "outputPerMillion",
            ):
                self.assertGreaterEqual(entry[field], 0, f"{entry['model']}:{field}")


if __name__ == "__main__":
    unittest.main()
