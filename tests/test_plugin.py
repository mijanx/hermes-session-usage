from __future__ import annotations

import importlib.util
import sqlite3
import tempfile
import unittest
from pathlib import Path
from unittest import mock

from dashboard import plugin_api

ROOT = Path(__file__).resolve().parents[1]


def _create_database(path: Path) -> None:
    with sqlite3.connect(path) as connection:
        connection.executescript(
            """
            CREATE TABLE sessions (
                id TEXT PRIMARY KEY,
                source TEXT,
                model TEXT,
                parent_session_id TEXT,
                started_at REAL,
                ended_at REAL,
                end_reason TEXT,
                message_count INTEGER,
                tool_call_count INTEGER,
                api_call_count INTEGER,
                title TEXT,
                display_name TEXT
            );
            CREATE TABLE session_model_usage (
                session_id TEXT,
                model TEXT,
                billing_provider TEXT,
                billing_mode TEXT,
                task TEXT,
                api_call_count INTEGER,
                input_tokens INTEGER,
                cache_read_tokens INTEGER,
                cache_write_tokens INTEGER,
                output_tokens INTEGER,
                reasoning_tokens INTEGER,
                estimated_cost_usd REAL,
                actual_cost_usd REAL,
                cost_status TEXT,
                cost_source TEXT,
                first_seen REAL,
                last_seen REAL
            );
            """
        )
        connection.executemany(
            """
            INSERT INTO sessions VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?)
            """,
            [
                ("root", "cli", "gpt-5.6", None, 1_999_000, 1_999_100, "completed", 4, 1, 2, "Root", None),
                ("child", "delegate", "gpt-5.6", "root", 1_999_050, None, None, 2, 1, 1, "Child", None),
            ],
        )
        connection.executemany(
            """
            INSERT INTO session_model_usage VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?)
            """,
            [
                ("root", "gpt-5.6", "openai", "api", "agent", 2, 100, 20, 0, 30, 10, 0.01, 0.0, "recorded", "telemetry", 1_999_000, 1_999_100),
                ("child", "gpt-5.6", "openai-codex", "subscription", "agent", 1, 50, 0, 0, 25, 5, 0.0, 0.0, "subscription", "telemetry", 1_999_050, 1_999_080),
            ],
        )


class PluginApiTests(unittest.TestCase):
    def test_discovers_profiles_and_groups_session_family(self) -> None:
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            _create_database(root / "state.db")
            named = root / "profiles" / "research"
            named.mkdir(parents=True)
            _create_database(named / "state.db")
            quarantined = root / "profiles" / "quarantine-bad"
            quarantined.mkdir(parents=True)
            _create_database(quarantined / "state.db")

            profiles = plugin_api.discover_profiles(root)
            self.assertEqual([item["name"] for item in profiles], ["default", "research"])

            result = plugin_api.query_metrics(
                {"profiles": ["default"], "window": "24h", "limit": 10},
                root=root,
                now=2_000_000,
            )
            self.assertEqual(result["schemaVersion"], 2)
            self.assertEqual(result["filteredSessions"], 2)
            self.assertEqual(result["filteredFamilies"], 1)
            self.assertEqual(result["accountedTokens"], 225)
            self.assertEqual(result["apiCalls"], 3)
            family = result["families"][0]
            self.assertEqual(family["rootSessionId"], "root")
            self.assertEqual([item["id"] for item in family["sessions"]], ["root", "child"])
            self.assertEqual(family["usageLines"][0]["provider"], "openai, openai-codex")

    def test_skips_inaccessible_profile_database(self) -> None:
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            _create_database(root / "state.db")
            blocked = root / "profiles" / "blocked"
            blocked.mkdir(parents=True)

            original_is_file = Path.is_file

            def is_file(path: Path) -> bool:
                if path == blocked / "state.db":
                    raise PermissionError(13, "Permission denied", str(path))
                return original_is_file(path)

            with mock.patch.object(Path, "is_file", is_file):
                profiles = plugin_api.discover_profiles(root)

            self.assertEqual([item["name"] for item in profiles], ["default"])

    def test_rejects_invalid_query_values(self) -> None:
        with self.assertRaisesRegex(ValueError, "Window"):
            plugin_api.query_metrics({"window": "forever"}, root=Path("unused"))


class InstallerTests(unittest.TestCase):
    def test_installs_both_plugin_halves(self) -> None:
        spec = importlib.util.spec_from_file_location("install_plugin", ROOT / "scripts" / "install-plugin.py")
        installer = importlib.util.module_from_spec(spec)
        assert spec.loader is not None
        spec.loader.exec_module(installer)

        with tempfile.TemporaryDirectory() as temporary:
            home = Path(temporary)
            desktop_root, backend_root = installer.install(ROOT, home)
            self.assertTrue((desktop_root / "plugin.js").is_file())
            self.assertTrue((backend_root / "plugin.yaml").is_file())
            manifest_path = backend_root / "dashboard" / "manifest.json"
            bundle_path = backend_root / "dashboard" / "dist" / "index.js"
            self.assertTrue(manifest_path.is_file())
            self.assertTrue(bundle_path.is_file())
            self.assertTrue((backend_root / "dashboard" / "plugin_api.py").is_file())
            self.assertTrue((backend_root / "HermesSessionMetrics.Web" / "data" / "api-pricing.json").is_file())

            manifest = manifest_path.read_text(encoding="utf-8")
            bundle = bundle_path.read_text(encoding="utf-8")
            self.assertIn('"path": "/session-usage"', manifest)
            self.assertIn('"entry": "dist/index.js"', manifest)
            self.assertIn('__HERMES_PLUGINS__.register("session-usage"', bundle)
            self.assertIn('const API = "/api/plugins/session-usage"', bundle)
            self.assertIn('api("/metrics", { method: "POST"', bundle)


class DashboardBundleTests(unittest.TestCase):
    def setUp(self) -> None:
        self.bundle = (ROOT / "dashboard" / "dist" / "index.js").read_text(encoding="utf-8")

    def test_preserves_null_api_equivalent_cost(self) -> None:
        self.assertIn('return value == null ? "—" : money.format(value);', self.bundle)
        self.assertIn("formatCost(apiCost ? line.apiEquivalentCostUsd : line.estimatedCostUsd)", self.bundle)
        self.assertNotIn("money.format((apiCost ? line.apiEquivalentCostUsd : line.estimatedCostUsd) || 0)", self.bundle)

    def test_reads_api_equivalent_pricing_coverage_fields(self) -> None:
        self.assertIn("result.apiEquivalentPricedTokens", self.bundle)
        self.assertIn("result.apiEquivalentUnpricedTokens", self.bundle)
        self.assertNotRegex(self.bundle, r"(?<![A-Za-z])result\.pricedTokens")
        self.assertNotRegex(self.bundle, r"(?<![A-Za-z])result\.pricingEligibleTokens")

    def test_finishes_loading_when_profiles_are_empty(self) -> None:
        self.assertIn("if (!list.length) setLoading(false);", self.bundle)
        self.assertIn("if (profilesLoaded) setLoading(false);", self.bundle)
        self.assertIn("No profiles discovered", self.bundle)
        self.assertIn("}, [refreshKey]);", self.bundle)


if __name__ == "__main__":
    unittest.main()
