#!/usr/bin/env python
"""Install the Session Usage backend and desktop renderer into one Hermes home."""

from __future__ import annotations

import argparse
import os
import shutil
from pathlib import Path

PLUGIN_ID = "session-usage"


def _default_hermes_home() -> Path:
    configured = os.environ.get("HERMES_HOME")
    return Path(configured).expanduser() if configured else Path.home() / ".hermes"


def _copy(source: Path, destination: Path) -> None:
    destination.parent.mkdir(parents=True, exist_ok=True)
    shutil.copy2(source, destination)
    print(f"installed {destination}")


def install(source_root: Path, hermes_home: Path) -> tuple[Path, Path]:
    source_root = source_root.resolve()
    hermes_home = hermes_home.expanduser().resolve()
    desktop_root = hermes_home / "desktop-plugins" / PLUGIN_ID
    backend_root = hermes_home / "plugins" / PLUGIN_ID

    files = {
        source_root / "desktop" / "plugin.js": desktop_root / "plugin.js",
        source_root / "plugin.yaml": backend_root / "plugin.yaml",
        source_root / "__init__.py": backend_root / "__init__.py",
        source_root / "dashboard" / "manifest.json": backend_root / "dashboard" / "manifest.json",
        source_root / "dashboard" / "plugin_api.py": backend_root / "dashboard" / "plugin_api.py",
        source_root / "HermesSessionMetrics.Web" / "data" / "api-pricing.json": (
            backend_root / "HermesSessionMetrics.Web" / "data" / "api-pricing.json"
        ),
    }
    missing = [str(path) for path in files if not path.is_file()]
    if missing:
        raise FileNotFoundError("Missing plugin source files: " + ", ".join(missing))

    for source, destination in files.items():
        _copy(source, destination)
    return desktop_root, backend_root


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument(
        "--hermes-home",
        type=Path,
        default=_default_hermes_home(),
        help="Hermes data directory (defaults to HERMES_HOME or ~/.hermes)",
    )
    args = parser.parse_args()
    source_root = Path(__file__).resolve().parent.parent
    desktop_root, backend_root = install(source_root, args.hermes_home)
    print(f"\nDesktop renderer: {desktop_root}")
    print(f"Python backend:   {backend_root}")
    print("Next: hermes plugins enable session-usage, then restart the gateway.")
    print("Hermes Desktop hot-reloads the renderer; use Reload desktop plugins if needed.")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
