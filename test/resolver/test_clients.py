"""Client selection: Claude by default; enabling/disabling reconciles folders."""

import json
import shutil
import subprocess
import sys
from pathlib import Path

from resolver_helpers import (
    load_catalog, read_manifest, run as run_catalog, run_json as run_catalog_json,
)

ROOT = Path(__file__).resolve().parents[2]
PLATFORMS = ROOT / "tooling" / ".ai" / "scripts" / "sync-platforms.py"

catalog = load_catalog("catalog_clients")


def run(*argv):
    return run_catalog(catalog, *argv)


def run_json(capsys, *argv):
    return run_catalog_json(catalog, capsys, *argv)


def write_manifest(project: Path, clients=None) -> None:
    manifest = {
        "version": 1,
        "sources": {"template": {"repo": "owner/repo", "ref": "main"}},
        "skills": [],
        "agents": [],
    }
    if clients is not None:
        manifest["clients"] = clients
    (project / ".ai").mkdir(parents=True, exist_ok=True)
    (project / ".ai" / "ai.json").write_text(
        json.dumps(manifest), encoding="utf-8")


def test_new_manifests_default_to_claude_only():
    assert catalog.default_manifest()["clients"] == ["claude"]


def test_a_missing_clients_field_keeps_every_legacy_client():
    assert catalog._clients({}) == {"claude", "copilot", "reasonix"}


def test_clients_command_sets_the_manifest(project, capsys):
    write_manifest(project)

    payload = run_json(capsys, "clients", "--set", "copilot", "reasonix", "--json")

    assert payload["clients"] == ["copilot", "reasonix"]
    assert read_manifest(project)["clients"] == ["copilot", "reasonix"]

    payload = run_json(capsys, "clients", "--set", "--json")
    assert payload["clients"] == []
    assert read_manifest(project)["clients"] == []


def test_sync_platforms_adds_and_removes_client_folders(project):
    (project / ".ai" / "scripts").mkdir(parents=True)
    shutil.copy(PLATFORMS, project / ".ai" / "scripts" / "sync-platforms.py")
    skill = project / ".ai" / "skills" / "alpha"
    skill.mkdir(parents=True)
    (skill / "SKILL.md").write_text(
        "---\nname: alpha\ndescription: alpha\n---\n", encoding="utf-8")
    write_manifest(project, ["claude"])

    script = project / ".ai" / "scripts" / "sync-platforms.py"
    subprocess.run([sys.executable, str(script)], cwd=project, check=True,
                   capture_output=True, text=True)
    assert (project / ".claude" / "skills").exists()
    assert not (project / ".github" / "skills").exists()
    assert not (project / ".reasonix" / "skills").exists()

    manifest = read_manifest(project)
    manifest["clients"] = ["copilot", "reasonix"]
    (project / ".ai" / "ai.json").write_text(json.dumps(manifest), encoding="utf-8")
    subprocess.run([sys.executable, str(script)], cwd=project, check=True,
                   capture_output=True, text=True)

    assert not (project / ".claude" / "skills").exists()
    assert (project / ".github" / "skills").exists()
    assert (project / ".reasonix" / "skills").exists()
    # An empty client folder is removed; .github is kept because it may hold
    # workflows.
    assert not (project / ".claude").exists()
    assert (project / ".github").is_dir()


def test_disabling_every_client_removes_generated_copilot_instructions(
        project, tmp_path, capsys):
    source = tmp_path / "template"
    for rel, text in {
        "AGENTS.md": "# AGENTS\n\nRead `.ai/session-context.md` first.\n",
        ".gitattributes": "*.sh text eol=lf\n",
        ".github/copilot-instructions.md":
            "# Copilot\n\n<!-- ai-workspace-manager:generated-from-agents -->\n",
        ".claude/settings.json": "{}\n",
    }.items():
        path = source / rel
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_text(text, encoding="utf-8")
    write_manifest(project, ["claude", "copilot"])
    manifest = read_manifest(project)
    manifest["sources"]["template"]["path"] = str(source)
    (project / ".ai" / "ai.json").write_text(json.dumps(manifest), encoding="utf-8")

    assert run("bootstrap") == 0
    copilot = project / ".github" / "copilot-instructions.md"
    assert copilot.is_file()

    manifest["clients"] = ["claude"]
    (project / ".ai" / "ai.json").write_text(json.dumps(manifest), encoding="utf-8")
    assert run("sync") == 0

    # The generated Copilot file is removed; a hand-written one would be kept.
    assert not copilot.exists()
    # Nothing else lives in .github, so the folder goes too.
    assert not (project / ".github").exists()
    assert (project / "AGENTS.md").is_file()
    assert (project / ".claude" / "settings.json").is_file()
