"""The extension must work in a repository that has no `.ai/ai.json` yet.

A repository that never selected anything has no catalog of its own, so
read-only commands resolve against an implicit starter manifest (the template
source, fetched remotely) and write nothing. The first selection persists it.
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
    spec = importlib.util.spec_from_file_location("catalog_bootstrap", SCRIPT)
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
        "---\nname: alpha\ndescription: alpha skill\n---\n", encoding="utf-8")
    git(source, "init", "-q", "-b", "main")
    git(source, "add", "-A")
    git(source, "commit", "-q", "-m", "v1")
    return source


def starter_for(source: Path) -> dict:
    return {
        "version": 1,
        "sources": {
            "template": {"repo": source.as_uri(), "ref": "main", "resolve": "remote"},
        },
        "skills": [],
        "agents": [],
        "instructions": [],
        "plugins": [],
        "hooks": [],
        "materialize": "copy",
        "exclude": [],
    }


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
    # http(s) is refused; the file:// fixtures below stay usable.
    monkeypatch.setenv("CATALOG_OFFLINE", "1")


@pytest.fixture
def bare_project(tmp_path, monkeypatch):
    """A workspace with no `.ai/` at all — the reported failure case."""
    root = tmp_path / "proj"
    root.mkdir()
    monkeypatch.setenv("CATALOG_ROOT", str(root))
    return root


@pytest.fixture
def local_starter(monkeypatch, tmp_path):
    source = make_source(tmp_path)
    monkeypatch.setattr(catalog, "starter_manifest", lambda: starter_for(source))
    return source


# ─── The starter manifest ────────────────────────────────────────────────────

def test_starter_manifest_points_the_template_at_its_repository(bare_project):
    manifest = catalog.starter_manifest()

    assert manifest["sources"]["template"] == {
        "repo": "I-Synergy/AI", "ref": "main", "resolve": "remote",
    }
    assert manifest["skills"] == []
    assert manifest["agents"] == []
    # A bare repo has no catalog/ to point $schema at.
    assert "$schema" not in manifest


def test_starter_manifest_offers_the_copilot_source_when_online(bare_project, monkeypatch):
    monkeypatch.delenv("CATALOG_OFFLINE", raising=False)

    sources = catalog.starter_manifest()["sources"]

    assert sources["awesome-copilot"] == {
        "repo": "github/awesome-copilot", "ref": "main",
        "resolve": "remote", "license": "MIT",
    }


def test_starter_manifest_offers_the_aspire_source_when_online(bare_project, monkeypatch):
    monkeypatch.delenv("CATALOG_OFFLINE", raising=False)

    sources = catalog.starter_manifest()["sources"]

    assert sources["aspire-skills"] == {
        "repo": "microsoft/aspire-skills", "ref": "main",
        "resolve": "remote", "license": "MIT",
    }


def test_starter_manifest_offers_the_maui_labs_sources_with_their_nested_layout(
        bare_project, monkeypatch):
    monkeypatch.delenv("CATALOG_OFFLINE", raising=False)

    sources = catalog.starter_manifest()["sources"]

    assert sources["dotnet-maui"] == {
        "repo": "dotnet/maui-labs", "ref": "main",
        "resolve": "remote", "license": "MIT",
        "paths": {
            "skill": ["plugins/dotnet-maui/skills"],
            "agent": [".github/agents"],
            "instruction": [".github/instructions"],
            "plugin": [], "hook": [],
        },
    }
    assert sources["dotnet-maui-tooling"] == {
        "repo": "dotnet/maui-labs", "ref": "main",
        "resolve": "remote", "license": "MIT",
        "paths": {
            "skill": ["plugins/dotnet-maui-tooling/skills"],
            "agent": [], "instruction": [], "plugin": [], "hook": [],
        },
    }


def test_starter_manifest_leaves_third_party_sources_out_offline(bare_project):
    # They could not be fetched, and a source that cannot resolve would make
    # every read command fail.
    assert set(catalog.starter_manifest()["sources"]) == {"template"}


def test_starter_manifest_reads_a_repository_that_has_its_own_catalog(bare_project):
    (bare_project / ".ai" / "skills" / "alpha").mkdir(parents=True)

    entry = catalog.starter_manifest()["sources"]["template"]

    # Same decision `init` makes: the local tree is the catalog.
    assert entry["ref"] == "local"
    assert "resolve" not in entry


def test_browse_uses_the_repositorys_own_catalog_without_a_manifest(bare_project, capsys):
    skill = bare_project / ".ai" / "skills" / "alpha"
    skill.mkdir(parents=True)
    (skill / "SKILL.md").write_text(
        "---\nname: alpha\ndescription: alpha skill\n---\n", encoding="utf-8")

    payload = run_json(capsys, "browse", "--json")

    assert [item["id"] for item in payload["items"]] == ["template:alpha"]
    assert payload["source"]["sameDir"] is True
    # Source == target: the file on disk is the installation, so it is selected
    # without a manifest — otherwise Installed would claim the repo is empty.
    assert payload["items"][0]["selected"] is True
    assert payload["items"][0]["installed"] is True
    assert payload["items"][0]["status"] == "ok"
    assert payload["summary"]["selected"] == 1
    assert not (bare_project / ".ai" / "ai.json").exists()


def test_starter_manifest_selects_what_is_already_on_disk(bare_project):
    (bare_project / ".ai" / "skills" / "alpha").mkdir(parents=True)
    (bare_project / ".ai" / "skills" / "beta").mkdir(parents=True)
    (bare_project / ".ai" / "agents").mkdir(parents=True)
    (bare_project / ".ai" / "agents" / "arch.md").write_text("---\nname: arch\n---\n",
                                                            encoding="utf-8")
    # Files that are not items must not become selections.
    (bare_project / ".ai" / "skills" / "README.md").write_text("notes\n", encoding="utf-8")

    manifest = catalog.starter_manifest()

    assert manifest["skills"] == ["alpha", "beta"]
    assert manifest["agents"] == ["arch"]


# ─── Read commands ───────────────────────────────────────────────────────────

def test_browse_works_without_a_manifest(bare_project, local_starter, capsys):
    payload = run_json(capsys, "browse", "--json")

    assert payload["summary"]["available"] == 1
    assert [item["id"] for item in payload["items"]] == ["template:alpha"]
    assert payload["items"][0]["selected"] is False
    assert payload["items"][0]["status"] == "available"
    # Reading must not create the manifest it is reading through.
    assert not (bare_project / ".ai" / "ai.json").exists()


def test_status_works_without_a_manifest(bare_project, local_starter):
    assert run("status") == 0


def test_list_works_without_a_manifest(bare_project, local_starter, capsys):
    payload = run_json(capsys, "list", "--available", "--json")

    assert [row["id"] for row in payload["items"]] == ["template:alpha"]


# ─── The first write ────────────────────────────────────────────────────────

def test_the_first_selection_persists_the_starter_sources(bare_project, local_starter):
    assert run("add", "alpha") == 0

    manifest = json.loads((bare_project / ".ai" / "ai.json").read_text(encoding="utf-8"))
    assert manifest["skills"] == ["alpha"]
    # The point of the starter: the selection file must remember that the
    # catalog comes from the repository, not from this (empty) tree.
    assert manifest["sources"]["template"]["resolve"] == "remote"
    assert manifest["sources"]["template"]["repo"] == local_starter.as_uri()


def test_installing_in_a_local_catalog_repo_writes_the_manifest_and_syncs(bare_project, capsys):
    skill = bare_project / ".ai" / "skills" / "alpha"
    skill.mkdir(parents=True)
    (skill / "SKILL.md").write_text(
        "---\nname: alpha\ndescription: alpha skill\n---\n", encoding="utf-8")

    assert run("add", "alpha") == 0
    assert run("sync") == 0

    manifest = json.loads((bare_project / ".ai" / "ai.json").read_text(encoding="utf-8"))
    assert manifest["skills"] == ["alpha"]
    assert manifest["sources"]["template"]["ref"] == "local"
    # source == target here: sync records the selection, it does not copy.
    assert (skill / "SKILL.md").is_file()


def test_sync_without_a_manifest_is_a_no_op(bare_project, capsys):
    """The implicit selection is enough to sync: source == target has nothing to do."""
    skill = bare_project / ".ai" / "skills" / "alpha"
    skill.mkdir(parents=True)
    (skill / "SKILL.md").write_text(
        "---\nname: alpha\ndescription: alpha skill\n---\n", encoding="utf-8")

    assert run("sync") == 0
    # Reading it does not create the manifest; the selection stays implicit.
    assert not (bare_project / ".ai" / "ai.json").exists()


def test_update_says_a_local_selection_has_no_upstream(bare_project, capsys):
    skill = bare_project / ".ai" / "skills" / "alpha"
    skill.mkdir(parents=True)
    (skill / "SKILL.md").write_text(
        "---\nname: alpha\ndescription: alpha skill\n---\n", encoding="utf-8")

    payload = run_json(capsys, "update", "--json")

    assert payload["remote"] is False
    assert payload["remoteSelected"] is False
    assert payload["ready"] == []


# ─── Adopt ───────────────────────────────────────────────────────────────────

def test_adopt_persists_the_selection_this_repository_already_has(bare_project, capsys):
    skill = bare_project / ".ai" / "skills" / "alpha"
    skill.mkdir(parents=True)
    (skill / "SKILL.md").write_text(
        "---\nname: alpha\ndescription: alpha skill\n---\n", encoding="utf-8")

    payload = run_json(capsys, "adopt", "--json")

    assert payload["created"] is True
    assert payload["selected"] == 1
    manifest = json.loads((bare_project / ".ai" / "ai.json").read_text(encoding="utf-8"))
    assert manifest["version"] == 1
    assert manifest["sources"]["template"]["ref"] == "local"
    assert manifest["skills"] == ["alpha"]
    assert manifest["materialize"] == "copy"
    # Adopting describes; it must not move or rewrite the item.
    assert (skill / "SKILL.md").is_file()


def test_adopt_records_the_remote_template_for_an_empty_repository(bare_project, capsys):
    payload = run_json(capsys, "adopt", "--json")

    assert payload["selected"] == 0
    manifest = json.loads((bare_project / ".ai" / "ai.json").read_text(encoding="utf-8"))
    assert manifest["sources"]["template"]["resolve"] == "remote"


def test_adopt_never_overwrites_an_existing_manifest(bare_project, capsys):
    path = bare_project / ".ai" / "ai.json"
    path.parent.mkdir(parents=True)
    original = json.dumps({"version": 1, "sources": {"template": {"repo": "x", "ref": "y"}},
                           "skills": ["keep-me"], "agents": [], "materialize": "copy",
                           "exclude": []}, indent=2)
    path.write_text(original, encoding="utf-8")

    payload = run_json(capsys, "adopt", "--json")

    assert payload["created"] is False
    assert path.read_text(encoding="utf-8") == original
