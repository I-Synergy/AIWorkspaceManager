"""Item locks: a user-frozen item is never overwritten by update or sync."""

import json

from resolver_helpers import (
    load_catalog, read_manifest, run as run_catalog,
    run_json as run_catalog_json, write_skill,
)

catalog = load_catalog("catalog_locks")


def run(*argv):
    return run_catalog(catalog, *argv)


def run_json(capsys, *argv):
    return run_catalog_json(catalog, capsys, *argv)


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
