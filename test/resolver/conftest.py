"""Shared fixtures for the resolver tests."""

import pytest


@pytest.fixture(autouse=True)
def no_network(monkeypatch):
    """Keep every test offline; file:// fixtures still work."""
    monkeypatch.setenv("CATALOG_OFFLINE", "1")


@pytest.fixture
def project(tmp_path, monkeypatch):
    root = tmp_path / "proj"
    root.mkdir()
    monkeypatch.setenv("CATALOG_ROOT", str(root))
    return root


@pytest.fixture
def bare_project(project):
    """A workspace with no `.ai/` at all (same root as `project`)."""
    return project
