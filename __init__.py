"""Hermes plugin registration.

The plugin's Python surface is its dashboard API router. The desktop renderer is
installed separately under ``desktop-plugins/session-usage``.
"""


def register(ctx):
    """Register no agent tools; API routes are loaded from dashboard/manifest.json."""
    del ctx
