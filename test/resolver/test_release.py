"""Tests for `catalog.py release` — newer-release detection for pinned sources.

The resolver that ships with the extension is vendored under `tooling/`, so these
tests exercise the copy the VSIX actually runs. Every test works in `tmp_path`
against a `file://` git repository: no network, and nothing outside the test's
own temporary tree.
"""

import importlib.util
import json
import subprocess
import sys
from pathlib import Path

import pytest

SCRIPT = Path(__file__).resolve().parents[2] / "tooling" / ".ai" / "scripts" / "catalog.py"

# Importing the resolver would otherwise drop a __pycache__ into the snapshot
# that the VSIX ships.
sys.dont_write_bytecode = True


def _load_catalog():
    spec = importlib.util.spec_from_file_location("catalog_release", SCRIPT)
    module = importlib.util.module_from_spec(spec)
    sys.modules[spec.name] = module
    spec.loader.exec_module(module)
    return module


catalog = _load_catalog()


# ─── Helpers ─────────────────────────────────────────────────────────────────

def git(repo: Path, *args: str) -> str:
    result = subprocess.run(
        ["git", "-C", str(repo), "-c", "user.name=Test", "-c", "user.email=t@example.com", *args],
        capture_output=True, text=True, check=True,
    )
    return result.stdout.strip()


def make_source(tmp_path: Path) -> Path:
    source = tmp_path / "src"
    skill = source / ".ai" / "skills" / "alpha"
    skill.mkdir(parents=True)
    (skill / "SKILL.md").write_text(
        "---\nname: alpha\ndescription: a\n---\n", encoding="utf-8")
    git(source, "init", "-q", "-b", "main")
    git(source, "add", "-A")
    git(source, "commit", "-q", "-m", "v1")
    return source


def write_manifest(root: Path, source: Path, ref: str, repo: str | None = None) -> Path:
    path = Path(root) / ".ai" / "ai.json"
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(json.dumps({
        "version": 1,
        "sources": {
            "template": {"repo": repo or source.as_uri(), "ref": ref, "resolve": "remote"},
        },
        "skills": ["alpha"],
        "agents": [],
        "materialize": "copy",
        "exclude": [],
    }, indent=2), encoding="utf-8")
    return path


def run(*argv):
    """Run one CLI command and return its exit code (no sys.exit escapes)."""
    args = catalog.build_parser().parse_args([str(value) for value in argv])
    try:
        return args.handler(args)
    except SystemExit as exc:
        return exc.code if isinstance(exc.code, int) else 1


def run_json(capsys, *argv):
    capsys.readouterr()
    assert run(*argv) == 0
    return json.loads(capsys.readouterr().out)


@pytest.fixture(autouse=True)
def no_network(monkeypatch):
    # http(s) is refused; the file:// fixtures below stay usable.
    monkeypatch.setenv("CATALOG_OFFLINE", "1")


@pytest.fixture
def project(tmp_path, monkeypatch):
    root = tmp_path / "proj"
    root.mkdir()
    monkeypatch.setenv("CATALOG_ROOT", str(root))
    return root


# ─── Semver ordering ─────────────────────────────────────────────────────────

def test_parse_version_orders_numerically():
    assert catalog.parse_version("v1.10.0") > catalog.parse_version("v1.9.9")
    assert catalog.parse_version("1.6.0") == catalog.parse_version("v1.6.0")
    assert catalog.parse_version("v1.6.0+build.5") == catalog.parse_version("v1.6.0")


def test_a_prerelease_sorts_below_its_release():
    assert catalog.parse_version("v1.6.0-rc1") < catalog.parse_version("v1.6.0")


def test_parse_version_rejects_what_is_not_semver():
    for tag in ("main", "latest", "nightly", "v1.6", "v1.6.0.1", ""):
        assert catalog.parse_version(tag) is None, tag


def test_calver_release_tags_sort_by_number():
    """ci.yml tags releases as `yyyy.1MMdd.1HHmm` (3 numeric parts, no `v`)."""
    assert catalog.parse_version("2026.11027.12343") == (2026, 11027, 12343, 1, "")
    assert catalog.parse_version("2026.11027.12343") > catalog.parse_version("2026.11026.23559")
    assert catalog.release_tags(
        ["2026.11026.23559", "nightly", "2026.11027.12343"]
    ) == ["2026.11027.12343", "2026.11026.23559"]


def test_release_tags_orders_newest_first_and_drops_non_semver():
    tags = ["v1.5.0", "nightly", "v1.10.0", "latest", "v1.9.0", "main"]

    assert catalog.release_tags(tags) == ["v1.10.0", "v1.9.0", "v1.5.0"]


# ─── Release rows ────────────────────────────────────────────────────────────

def test_release_reports_a_newer_stable_tag(project, tmp_path, capsys):
    source = make_source(tmp_path)
    for tag in ("v1.5.0", "v1.7.0", "v1.9.0-rc1", "nightly"):
        git(source, "tag", tag)
    write_manifest(project, source, ref="v1.5.0")

    payload = run_json(capsys, "release", "--json")

    assert payload["command"] == "release"
    row = payload["sources"][0]
    assert row["id"] == "template"
    assert row["checked"] is True
    assert row["pinnedTag"] is True
    assert row["current"] == "v1.5.0"
    # v1.7.0 is the newest *release*; v1.9.0-rc1 is a prerelease and must not
    # be advertised to someone on a stable pin.
    assert row["latest"] == "v1.7.0"
    assert row["latestIsPrerelease"] is False
    assert row["newer"] is True
    assert "nightly" not in row["tags"]
    assert payload["newer"] == ["template"]


def test_release_does_not_call_a_prerelease_a_newer_release(project, tmp_path, capsys):
    source = make_source(tmp_path)
    for tag in ("v1.8.0", "v1.9.0-rc1"):
        git(source, "tag", tag)
    write_manifest(project, source, ref="v1.8.0")

    row = run_json(capsys, "release", "--json")["sources"][0]

    assert row["latest"] == "v1.8.0"
    assert row["latestIsPrerelease"] is False
    assert row["newer"] is False
    assert row["tags"][0] == "v1.9.0-rc1"   # still listed, just not the headline


def test_release_reports_a_prerelease_when_it_is_all_there_is(project, tmp_path, capsys):
    source = make_source(tmp_path)
    git(source, "tag", "v2.0.0-rc1")
    write_manifest(project, source, ref="v1.0.0")

    row = run_json(capsys, "release", "--json")["sources"][0]

    assert row["latest"] == "v2.0.0-rc1"
    assert row["latestIsPrerelease"] is True
    assert row["newer"] is True


def test_a_branch_pin_reports_tags_without_claiming_newer(project, tmp_path, capsys):
    source = make_source(tmp_path)
    git(source, "tag", "v1.7.0")
    write_manifest(project, source, ref="main")

    row = run_json(capsys, "release", "--json")["sources"][0]

    assert row["pinnedTag"] is False
    assert row["current"] is None
    assert row["latest"] == "v1.7.0"
    assert row["newer"] is False


def test_release_skips_a_source_with_a_local_path(project, tmp_path, capsys):
    source = make_source(tmp_path)
    manifest = write_manifest(project, source, ref="v1.0.0", repo="I-Synergy/AI")
    data = json.loads(manifest.read_text(encoding="utf-8"))
    # A `path` source reads that checkout: its repo is provenance, not a channel.
    data["sources"]["template"]["path"] = str(source)
    manifest.write_text(json.dumps(data, indent=2), encoding="utf-8")

    payload = run_json(capsys, "release", "--json")

    assert payload["sources"] == []
    assert payload["newer"] == []


def test_release_reports_a_tag_cap_but_the_real_count(project, tmp_path, capsys):
    source = make_source(tmp_path)
    for minor in range(25):
        git(source, "tag", f"v1.{minor}.0")
    write_manifest(project, source, ref="v1.0.0")

    row = run_json(capsys, "release", "--json")["sources"][0]

    assert row["tagCount"] == 25
    assert len(row["tags"]) == 20
    assert row["tags"][0] == "v1.24.0"


def test_release_offline_reports_a_row_not_a_failure(project, tmp_path, capsys):
    source = make_source(tmp_path)
    write_manifest(project, source, ref="v1.0.0", repo="https://github.com/acme/catalog.git")

    payload = run_json(capsys, "release", "--json")

    row = payload["sources"][0]
    assert row["checked"] is False
    assert row["error"] == "offline"
    assert row["newer"] is False
    assert payload["newer"] == []


def test_release_reports_an_unreadable_remote_without_failing(project, tmp_path, capsys):
    missing = tmp_path / "not-a-repo"
    missing.mkdir()
    write_manifest(project, missing, ref="v1.0.0")

    payload = run_json(capsys, "release", "--json")

    row = payload["sources"][0]
    assert row["checked"] is False
    assert row["error"]
    assert row["newer"] is False
