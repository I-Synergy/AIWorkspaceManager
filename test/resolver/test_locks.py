"""Item locks: a user-frozen item is never overwritten by update or sync."""

import importlib.util
import json
import sys
from pathlib import Path

import pytest

SCRIPT = Path(__file__).resolve().parents[2] / "tooling" / ".ai" / "scripts" / "catalog.py"

sys.dont_write_bytecode = True


def _load_catalog():
    spec = importlib.util.spec_from_file_location("catalog_locks", SCRIPT)
    module = importlib.util.module_from_spec(spec)
    sys.modules[spec.name] = module
    spec.loader.exec_module(module)
    return module


catalog = _load_catalog()


@pytest.fixture(autouse=True)
def no_network(monkeypatch):
    monkeypatch.setenv("CATALOG_OFFLINE", "1")


@pytest.fixture
def project(tmp_path, monkeypatch):
    root = tmp_path / "proj"
    root.mkdir()
    monkeypatch.setenv("CATALOG_ROOT", str(root))
    return root


def write_skill(root: Path, name: str, description: str) -> Path:
    skill = root / ".ai" / "skills" / name
    skill.mkdir(parents=True, exist_ok=True)
    (skill / "SKILL.md").write_text(
        f"---\nname: {name}\ndescription: {description}\n---\n\n{description}\n",
        encoding="utf-8")
    return skill


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


def read_manifest(root: Path) -> dict:
    return json.loads((root / ".ai" / "ai.json").read_text(encoding="utf-8"))


def test_lock_and_unlock_edit_the_manifest(project, capsys):
    write_skill(project, "alpha", "alpha")
    (project / ".ai" / "ai.json").write_text(json.dumps({
        "version": 1,
        "sources": {"template": {"repo": "owner/repo", "ref": "main"}},
        "skills": ["alpha"],
        "agents": [],
    }), encoding="utf-8")

    payload = run_json(capsys, "lock", "alpha", "--json")

    assert payload["changed"] == ["alpha"]
    assert read_manifest(project)["locked"] == ["alpha"]

    payload = run_json(capsys, "unlock", "alpha", "--json")

    assert payload["changed"] == ["alpha"]
    assert read_manifest(project)["locked"] == []


def test_lock_refuses_an_item_the_project_does_not_install(project, capsys):
    write_skill(project, "alpha", "alpha")
    (project / ".ai" / "ai.json").write_text(json.dumps({
        "version": 1,
        "sources": {"template": {"repo": "owner/repo", "ref": "main"}},
        "skills": ["alpha"],
        "agents": [],
    }), encoding="utf-8")

    assert run("lock", "beta", "--json") != 0
    assert read_manifest(project).get("locked", []) == []


def test_update_and_sync_never_overwrite_a_locked_item(project, tmp_path, capsys):
    source = tmp_path / "source"
    write_skill(source, "alpha", "upstream text")
    (project / ".ai").mkdir(parents=True, exist_ok=True)
    (project / ".ai" / "ai.json").write_text(json.dumps({
        "version": 1,
        "sources": {"template": {"repo": "owner/repo", "ref": "main",
                                 "path": str(source)}},
        "materialize": "copy",
        "skills": ["alpha"],
        "agents": [],
    }), encoding="utf-8")
    assert run("sync") == 0

    target = project / ".ai" / "skills" / "alpha" / "SKILL.md"
    edited = "---\nname: alpha\ndescription: local edit\n---\n\nmy change\n"
    target.write_text(edited, encoding="utf-8")
    run_json(capsys, "lock", "alpha", "--json")

    update = run_json(capsys, "update", "--apply", "--force", "--json")

    assert update["appliedItems"] == []
    assert [row["displayId"] for row in update["locked"]] == ["alpha"]
    assert target.read_text(encoding="utf-8") == edited
    # sync is not an escape hatch either, and it keeps the pristine hash so a
    # later unlock still sees the edit as drift.
    sync = run_json(capsys, "sync", "--check", "--json")
    assert sync["clean"] is True
    assert any(action["detail"] == "locked" for action in sync["actions"])

    # Unlocking resumes normal updates.
    run_json(capsys, "unlock", "alpha", "--json")
    update = run_json(capsys, "update", "--apply", "--force", "--json")
    assert [row["id"] for row in update["appliedItems"]] == ["template:alpha"]
    assert "upstream text" in target.read_text(encoding="utf-8")
