"""`catalog.py pin` — move the template source to a release, keep the selection.

The command only edits the manifest; replacing the tree is `update --apply`,
which treats an existing-but-unlocked target as "not installed" and rewrites it.
"""

import importlib.util
import json
import subprocess
import sys
from pathlib import Path

import pytest

SCRIPT = Path(__file__).resolve().parents[2] / "tooling" / ".ai" / "scripts" / "catalog.py"

sys.dont_write_bytecode = True


def _load_catalog():
    spec = importlib.util.spec_from_file_location("catalog_pin", SCRIPT)
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


def write_skill(root: Path, name: str, description: str) -> Path:
    skill = Path(root) / ".ai" / "skills" / name
    skill.mkdir(parents=True, exist_ok=True)
    (skill / "SKILL.md").write_text(
        f"---\nname: {name}\ndescription: {description}\n---\n\n{description}\n",
        encoding="utf-8")
    return skill


def make_release_repo(tmp_path: Path, tag: str, description: str) -> Path:
    source = tmp_path / "release-src"
    write_skill(source, "alpha", description)
    write_skill(source, "beta", description)
    git(source, "init", "-q", "-b", "main")
    git(source, "add", "-A")
    git(source, "commit", "-q", "-m", tag)
    git(source, "tag", tag)
    return source


def run(*argv):
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
    monkeypatch.setenv("CATALOG_OFFLINE", "1")


@pytest.fixture
def project(tmp_path, monkeypatch):
    root = tmp_path / "proj"
    root.mkdir()
    monkeypatch.setenv("CATALOG_ROOT", str(root))
    return root


def read_manifest(root: Path) -> dict:
    return json.loads((root / ".ai" / "ai.json").read_text(encoding="utf-8"))


# ─── pin ────────────────────────────────────────────────────────────────────

def test_pin_repoints_the_source_and_keeps_the_installed_selection(project, capsys):
    write_skill(project, "alpha", "local alpha")
    write_skill(project, "beta", "local beta")
    (project / ".ai" / "skills" / "README.md").write_text("notes\n", encoding="utf-8")

    payload = run_json(capsys, "pin", "--release", "2026.11027.12350", "--json")

    assert payload["ref"] == "2026.11027.12350"
    assert payload["selected"] == 2
    manifest = read_manifest(project)
    assert manifest["sources"]["template"]["ref"] == "2026.11027.12350"
    assert manifest["sources"]["template"]["resolve"] == "remote"
    # The selection is frozen as explicit names, and a preset cannot expand it.
    assert manifest["skills"] == ["alpha", "beta"]
    assert "preset" not in manifest
    # A tree that already holds files is copied, never linked to the cache.
    assert manifest["materialize"] == "copy"


def test_pin_keeps_other_sources_and_a_link_mode_for_a_separate_checkout(project, tmp_path, capsys):
    source = tmp_path / "src"
    write_skill(source, "alpha", "source alpha")
    (project / ".ai").mkdir(parents=True, exist_ok=True)
    (project / ".ai" / "ai.json").write_text(json.dumps({
        "version": 1,
        "sources": {
            "template": {"repo": "I-Synergy/AI", "ref": "v1", "path": str(source)},
            "awesome-copilot": {"repo": "github/awesome-copilot", "ref": "main",
                                "resolve": "remote", "license": "MIT",
                                "enabled": False},
        },
        "skills": ["alpha"],
        "agents": [],
        "materialize": "link",
        "exclude": [],
    }, indent=2), encoding="utf-8")

    assert run("pin", "--release", "v2") == 0

    manifest = read_manifest(project)
    # The old source was a checkout elsewhere, not this tree: the link choice
    # is the user's and stays.
    assert manifest["materialize"] == "link"
    assert manifest["sources"]["awesome-copilot"]["ref"] == "main"
    assert manifest["sources"]["template"]["ref"] == "v2"
    assert manifest["sources"]["template"]["resolve"] == "remote"
    assert "path" not in manifest["sources"]["template"]


def test_pin_copies_a_tree_that_held_real_files(project, capsys):
    write_skill(project, "alpha", "local alpha")
    (project / ".ai" / "ai.json").write_text(json.dumps({
        "version": 1,
        "sources": {"template": {"repo": "I-Synergy/AI", "ref": "v1"}},
        "skills": ["alpha"],
        "agents": [],
        "materialize": "link",
        "exclude": [],
    }, indent=2), encoding="utf-8")

    assert run("pin", "--release", "v2") == 0

    # The items are real files in this repository; linking them into the fetch
    # cache would move them out of version control.
    assert read_manifest(project)["materialize"] == "copy"


# ─── pin + update ───────────────────────────────────────────────────────────

def test_pin_then_update_replaces_the_installed_copy(project, tmp_path, capsys):
    source = make_release_repo(tmp_path, "2026.11027.12350", "release content")
    write_skill(project, "alpha", "old local content")

    assert run("pin", "--release", "2026.11027.12350", "--repo", source.as_uri()) == 0
    assert run("update", "--apply") == 0

    installed = (project / ".ai" / "skills" / "alpha" / "SKILL.md").read_text(encoding="utf-8")
    assert "release content" in installed
    assert "old local content" not in installed
    # The lock makes the next comparison meaningful.
    lock = json.loads((project / ".ai" / "ai.lock.json").read_text(encoding="utf-8"))
    assert {entry["id"] for entry in lock["resolved"]} == {"template:alpha"}
    # Only what was already installed is rewritten; a new release item (beta)
    # is not silently added by an upgrade.
    assert not (project / ".ai" / "skills" / "beta").exists()
