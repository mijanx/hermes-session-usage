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

    def test_official_source_ids_are_case_insensitively_unique(self) -> None:
        official = {
            "sources": [
                {"id": "OpenAI", "name": "one", "url": "https://one.test"},
                {"id": "openai", "name": "two", "url": "https://two.test"},
            ],
            "models": [],
        }

        with self.assertRaisesRegex(ValueError, "case-insensitively"):
            refresh_pricing.create_snapshot({}, "https://models.dev/api.json", official)

    def test_official_source_ids_cannot_collide_with_fallback(self) -> None:
        for source_id in ("MODELS-DEV", " models-dev "):
            with self.subTest(source_id=source_id):
                official = {
                    "sources": [{"id": source_id, "name": "reserved", "url": "https://one.test"}],
                    "models": [],
                }

                with self.assertRaises(ValueError):
                    refresh_pricing.create_snapshot({}, "https://models.dev/api.json", official)

    def test_official_model_source_ids_must_be_a_string_list(self) -> None:
        official = {
            "sources": [{"id": "official", "name": "one", "url": "https://one.test"}],
            "models": [
                {
                    "model": "test",
                    "provider": "test",
                    "inputPerMillion": 1,
                    "cacheReadPerMillion": 1,
                    "cacheWritePerMillion": 1,
                    "outputPerMillion": 1,
                    "sourceIds": "official",
                    "basis": "standard",
                }
            ],
        }

        with self.assertRaisesRegex(ValueError, "invalid source IDs"):
            refresh_pricing.create_snapshot({}, "https://models.dev/api.json", official)

    def test_official_provider_file_contains_documented_rates(self) -> None:
        import json

        official_path = SCRIPT_PATH.parents[1] / "HermesSessionMetrics.Web/data/official-provider-pricing.json"
        document = json.loads(official_path.read_text(encoding="utf-8"))
        prices = {entry["model"]: entry for entry in document["models"]}

        expected = {
            "gpt-6-astra": (10, 1, 12.5, 50),
            "gpt-6-sol": (2, 0.2, 2.5, 10),
            "gpt-6-luna": (0.1, 0.01, 0.125, 0.5),
            "gpt-daybreak-blue-latest": (4, 0.4, 5, 20),
            "gpt-5.6-cyber": (12.5, 1.25, 15.625, 75),
            "gpt-daybreak-red-latest": (12.5, 1.25, 15.625, 75),
            "gpt-6-luna-900k": (0.1, 0.01, 0.125, 0.5),
            "gpt-5.6-sol": (4, 0.4, 5, 20),
            "gpt-5.3-codex": (1.75, 0.175, 1.75, 14),
            "grok-4.7": (2, 0.5, 2, 6),
            "grok-4.6": (2, 0.5, 2, 6),
            "grok-4.5": (2, 0.3, 2, 6),
            "kimi-k3": (3, 0.3, 3, 15),
            "kimi-k2.6": (0.95, 0.16, 0.95, 4),
            "deepseek-flash": (0.3, 0.006, 0.3, 1.2),
            "deepseek-v4.1-flash": (0.3, 0.006, 0.3, 1.2),
            "deepseek-v4-flash": (0.3, 0.006, 0.3, 1.2),
            "deepseek-v4-flash-vision-exp": (0.3, 0.006, 0.3, 1.2),
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

        self.assertNotIn("kimi-k2.5", prices)
        self.assertNotIn("MiniMax-M2-Stable", prices)
        self.assertEqual(["kimi-pricing", "kimi-model-list"], prices["kimi-k2.6"]["sourceIds"])
        self.assertIn("5-minute TTL", prices["kimi-k3"]["basis"])
        self.assertIn("gpt-5.6-sol", prices["gpt-daybreak-blue-latest"]["basis"])
        self.assertIn("gpt-5.6-cyber", prices["gpt-daybreak-red-latest"]["basis"])
        self.assertIn("long-context pricing threshold", prices["gpt-6-luna-900k"]["basis"])
        self.assertIn("gpt-5.6-luna-900k", prices)
        self.assertEqual(
            (0.2, 0.02, 0.25, 1.2),
            tuple(prices["gpt-5.6-luna-900k"][key] for key in (
                "inputPerMillion", "cacheReadPerMillion", "cacheWritePerMillion", "outputPerMillion"
            )),
        )
        self.assertIn("272k", prices["gpt-5.6-luna-900k"]["basis"])
        self.assertIn("kimi-k2.7", prices)
        self.assertEqual(
            (0.95, 0.19, 0.95, 4),
            tuple(prices["kimi-k2.7"][key] for key in (
                "inputPerMillion", "cacheReadPerMillion", "cacheWritePerMillion", "outputPerMillion"
            )),
        )
        self.assertIn("not subscription spend", prices["kimi-k2.7"]["basis"])
        self.assertIn("50% lower", prices["deepseek-flash"]["basis"])
        self.assertIn("published cache-write rate", prices["gpt-5.6"]["basis"])
        self.assertIn("published cache-write rate", prices["gpt-5.6-sol"]["basis"])

        source_ids = {source["id"] for source in document["sources"]}
        self.assertIn("deepseek-pricing", source_ids)
        self.assertIn("openai-model-gpt-6-astra", source_ids)
        self.assertIn("openai-model-gpt-5.6-luna", source_ids)
        self.assertIn("openai-model-gpt-6-sol", source_ids)
        self.assertIn("openai-model-gpt-6-luna", source_ids)
        self.assertIn("kimi-k27-code-pricing", source_ids)
        self.assertEqual(["openai-model-gpt-6-luna"], prices["gpt-6-luna"]["sourceIds"])
        self.assertIn("272k", prices["gpt-6-luna"]["basis"])
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
