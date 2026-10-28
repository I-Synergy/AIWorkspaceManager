"""Installing from the template also brings the resolver and vocabulary.

A repository that installs anything should be able to run `catalog.py` itself —
that is what makes the `.github`/`.claude`/`.reasonix` mirrors possible. An
ordinary sync writes missing files only; an explicit update-to-release refreshes
them so the repository's CLI matches its pin.
"""

import importlib.util
import json
import sys
from pathlib import Path

import pytest

SCRIPT = Path(__file__).resolve().parents[2] / "tooling" / ".ai" / "scripts" / "catalog.py"

sys.dont_write_bytecode = True


def _load_catalog():
    spec = importlib.util.spec_from_file_location("catalog_tooling", SCRIPT)
    module = importlib.util.module_from_spec(spec)
    sys.modules[spec.name] = module
    spec.loader.exec_module(module)
    return module


catalog = _load_catalog()


# ─── Helpers ─────────────────────────────────────────────────────────────────

def write(path: Path, text: str) -> Path:
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(text, encoding="utf-8")
    return path


def make_template(tmp_path: Path) -> Path:
    """A stand-in template checkout with the resolver and vocabulary."""
    source = tmp_path / "template"
    write(source / ".ai" / "scripts" / "catalog.py", "# release catalog\n")
    write(source / ".ai" / "scripts" / "sync-skills.py",
          'from pathlib import Path\nPath("mirror-ran.txt").write_text("yes")\n')
    write(source / ".ai" / "scripts" / "sync-agents.py", "# agents mirror\n")
    write(source / ".ai" / "scripts" / "upgrade-template.py", "# upgrade\n")
    write(source / ".ai" / "scripts" / "hygiene-lint.py", "# extra template script\n")
    write(source / "catalog" / "presets.json", '{"presets": {}}\n')
    write(source / "catalog" / "categories.json", '{"categories": []}\n')
    write(source / "catalog" / "schemas" / "ai.schema.json", "{}\n")
    write(source / "catalog" / "schemas" / "index.schema.json", "{}\n")
    write(source / "catalog" / "schemas" / "lock.schema.json", "{}\n")
    # Supporting .ai/ tree an agent expects.
    write(source / ".ai" / "plans" / "example-plan.md", "# plan\n")
    write(source / ".ai" / "project" / "README.md", "# project\n")
    write(source / ".ai" / "project" / "tech-stack.md", "# stack\n")
    write(source / ".ai" / "reference" / "standards.md", "# standards\n")
    write(source / ".ai" / "checklists" / "pre-submission.md", "# checklist\n")
    write(source / ".ai" / "session-context.md", "# session context\n")
    # Agent-facing configuration at the repository root.
    write(source / "AGENTS.md", "# AGENTS for {ApplicationName}\n")
    write(source / ".github" / "copilot-instructions.md",
          "See {ApplicationName}.Domain.*\n")
    write(source / ".claude" / "settings.json", "{}\n")
    write(source / ".gitattributes", "*.sh text eol=lf\n")
    # Validation scripts the agent instructions point at.
    write(source / ".ai" / "tests" / "validate-skills.py", "print(1)\n")
    write(source / ".ai" / "tests" / "run-all-tests.sh", "#!/bin/sh\n")
    return source


def write_manifest(root: Path, source: Path) -> None:
    write(root / ".ai" / "ai.json", json.dumps({
        "version": 1,
        "sources": {"template": {"repo": "I-Synergy/AI", "ref": "v1", "path": str(source)}},
        "skills": [], "agents": [], "instructions": [], "plugins": [], "hooks": [],
        "materialize": "copy",
        "exclude": [],
    }, indent=2))


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


# ─── Sync installs missing tooling ──────────────────────────────────────────

def test_sync_installs_the_template_scripts_and_vocabulary(project, tmp_path):
    source = make_template(tmp_path)
    write_manifest(project, source)

    assert run("sync") == 0

    assert (project / ".ai" / "scripts" / "catalog.py").read_text(encoding="utf-8") \
        == "# release catalog\n"
    # Every script travels, not only the four the resolver drives.
    assert (project / ".ai" / "scripts" / "hygiene-lint.py").is_file()
    assert (project / ".ai" / "catalog" / "presets.json").is_file()
    assert (project / ".ai" / "catalog" / "schemas" / "lock.schema.json").is_file()
    # The mirror script is delegated to after it has been copied.
    assert (project / "mirror-ran.txt").is_file()


def test_sync_never_overwrites_an_adapted_script(project, tmp_path):
    source = make_template(tmp_path)
    write_manifest(project, source)
    write(project / ".ai" / "scripts" / "catalog.py", "# locally adapted\n")

    assert run("sync") == 0

    assert (project / ".ai" / "scripts" / "catalog.py").read_text(encoding="utf-8") \
        == "# locally adapted\n"


# ─── Update-to-release refreshes them ───────────────────────────────────────

def test_update_to_a_release_refreshes_the_scripts(project, tmp_path):
    source = make_template(tmp_path)
    write_manifest(project, source)
    write(project / ".ai" / "scripts" / "catalog.py", "# old project copy\n")

    assert run("update", "--apply", "--tooling") == 0

    assert (project / ".ai" / "scripts" / "catalog.py").read_text(encoding="utf-8") \
        == "# release catalog\n"
    assert (project / ".ai" / "catalog" / "categories.json").is_file()


def test_update_without_the_flag_leaves_the_adapted_script_alone(project, tmp_path):
    source = make_template(tmp_path)
    write_manifest(project, source)
    write(project / ".ai" / "scripts" / "catalog.py", "# old project copy\n")

    assert run("update", "--apply") == 0

    assert (project / ".ai" / "scripts" / "catalog.py").read_text(encoding="utf-8") \
        == "# old project copy\n"


# ─── Same tree ──────────────────────────────────────────────────────────────

def test_a_repository_that_is_the_template_copies_nothing(tmp_path, monkeypatch):
    root = tmp_path / "template"
    root.mkdir()
    monkeypatch.setenv("CATALOG_ROOT", str(root))

    summary = catalog.install_tooling(root)

    assert summary["copied"] == []
    assert summary["missing"] == []


# ─── Ownership: template-updatable vs project-created-once ──────────────────

def test_the_record_lists_only_what_came_from_the_template(project, tmp_path):
    source = make_template(tmp_path)
    write_manifest(project, source)
    write(project / ".ai" / "scripts" / "my-project-tool.py", "# mine\n")

    assert run("sync") == 0

    record = json.loads((project / ".ai" / "tooling.json").read_text(encoding="utf-8"))
    paths = {entry["path"] for entry in record["files"]}
    assert ".ai/scripts/catalog.py" in paths
    assert ".ai/scripts/my-project-tool.py" not in paths
    # The project script is still there, untouched.
    assert (project / ".ai" / "scripts" / "my-project-tool.py").read_text(
        encoding="utf-8") == "# mine\n"


def test_a_project_file_is_never_touched_by_an_update(project, tmp_path):
    source = make_template(tmp_path)
    write_manifest(project, source)
    write(project / ".ai" / "scripts" / "my-project-tool.py", "# mine\n")
    write(project / ".ai" / "catalog" / "my-presets.json", '{"presets": {"mine": {}}}\n')
    assert run("sync") == 0

    assert run("update", "--apply", "--tooling") == 0

    assert (project / ".ai" / "scripts" / "my-project-tool.py").read_text(
        encoding="utf-8") == "# mine\n"
    assert (project / ".ai" / "catalog" / "my-presets.json").is_file()


def test_a_template_file_edited_after_install_is_protected(project, tmp_path):
    source = make_template(tmp_path)
    write_manifest(project, source)
    assert run("sync") == 0

    edited = project / ".ai" / "catalog" / "presets.json"
    edited.write_text('{"presets": {"project": {}}}\n', encoding="utf-8")

    assert run("update", "--apply", "--tooling") == 0

    # Recorded, but the hash no longer matches: the project owns it now.
    assert edited.read_text(encoding="utf-8") == '{"presets": {"project": {}}}\n'


def test_pin_adopts_the_scripts_a_local_tree_already_has(project):
    write(project / ".ai" / "skills" / "alpha" / "SKILL.md",
          "---\nname: alpha\ndescription: a\n---\n")
    write(project / ".ai" / "scripts" / "catalog.py", "# the project's copy\n")
    write(project / ".ai" / "scripts" / "my-project-tool.py", "# mine\n")

    assert run("pin", "--release", "v2") == 0

    record = json.loads((project / ".ai" / "tooling.json").read_text(encoding="utf-8"))
    paths = {entry["path"] for entry in record["files"]}
    assert ".ai/scripts/catalog.py" in paths
    assert ".ai/scripts/my-project-tool.py" not in paths


# ─── Missing template files come from the bundled tooling ───────────────────

def make_bundle(tmp_path: Path) -> Path:
    bundle = tmp_path / "bundle"
    write(bundle / ".ai" / "scripts" / "catalog.py", "# bundled catalog")
    write(bundle / ".ai" / "scripts" / "hygiene-lint.py", "# bundled lint")
    return bundle


def test_a_script_the_template_dropped_is_taken_from_the_bundle(project, tmp_path, monkeypatch):
    source = make_template(tmp_path)
    # The resolver lives in the extension now, not in the template.
    (source / ".ai" / "scripts" / "catalog.py").unlink()
    bundle = make_bundle(tmp_path)
    monkeypatch.setenv("CATALOG_TOOLING", str(bundle))
    write_manifest(project, source)

    assert run("sync") == 0

    assert (project / ".ai" / "scripts" / "catalog.py").read_text(encoding="utf-8") \
        == "# bundled catalog"
    record = json.loads((project / ".ai" / "tooling.json").read_text(encoding="utf-8"))
    entry = next(e for e in record["files"] if e["path"] == ".ai/scripts/catalog.py")
    assert entry["origin"] == "bundled"


def test_a_file_missing_everywhere_is_reported_not_fatal(project, tmp_path):
    source = make_template(tmp_path)
    (source / ".ai" / "scripts" / "catalog.py").unlink()

    summary = catalog.install_tooling(source)

    assert ".ai/scripts/catalog.py" in summary["missing"]
    assert (project / ".ai" / "ai.json").exists() is False


# ─── Layout and scaffold ────────────────────────────────────────────────────

def test_the_vocabulary_lands_under_ai_not_the_repository_root(project, tmp_path):
    source = make_template(tmp_path)
    write_manifest(project, source)

    assert run("sync") == 0

    assert (project / ".ai" / "catalog" / "presets.json").is_file()
    assert not (project / "catalog").exists()
    # The resolver prefers the project copy.
    presets = catalog._load_presets()
    assert isinstance(presets, dict)


def test_sync_copies_the_supporting_ai_folders(project, tmp_path):
    source = make_template(tmp_path)
    write_manifest(project, source)

    assert run("sync") == 0

    assert (project / ".ai" / "plans" / "example-plan.md").is_file()
    assert (project / ".ai" / "project" / "tech-stack.md").is_file()
    assert (project / ".ai" / "reference" / "standards.md").is_file()
    assert (project / ".ai" / "checklists" / "pre-submission.md").is_file()
    assert (project / ".ai" / "session-context.md").is_file()


def test_project_scaffold_is_created_once_and_never_overwritten(project, tmp_path):
    source = make_template(tmp_path)
    write_manifest(project, source)
    write(project / ".ai" / "reference" / "standards.md", "# our standards\n")

    assert run("sync") == 0
    assert run("update", "--apply", "--tooling") == 0

    assert (project / ".ai" / "reference" / "standards.md").read_text(encoding="utf-8") \
        == "# our standards\n"
    # A file the project did not have is still created.
    assert (project / ".ai" / "plans" / "example-plan.md").is_file()


def test_bootstrap_sets_up_the_manifest_tooling_and_scaffold(project, tmp_path, capsys):
    source = make_template(tmp_path)
    write_manifest(project, source)

    payload = run_json(capsys, "bootstrap", "--json")

    assert payload["command"] == "bootstrap"
    assert payload["created"] is False
    assert (project / ".ai" / "scripts" / "catalog.py").is_file()
    assert (project / ".ai" / "catalog" / "presets.json").is_file()
    assert (project / ".ai" / "plans" / "example-plan.md").is_file()
    assert not (project / "catalog").exists()


def test_migrate_moves_a_root_catalog_into_ai(project):
    write(project / ".ai" / "ai.json", json.dumps({
        "version": 1,
        "sources": {"template": {"repo": "I-Synergy/AI", "ref": "v1", "resolve": "remote"}},
        "skills": [], "agents": [], "materialize": "copy", "exclude": [],
    }, indent=2))
    write(project / "catalog" / "presets.json", '{"presets": {}}\n')
    write(project / "catalog" / "schemas" / "ai.schema.json", "{}\n")

    assert catalog.migrate_catalog_dir() is True

    assert not (project / "catalog").exists()
    assert (project / ".ai" / "catalog" / "presets.json").is_file()
    manifest = json.loads((project / ".ai" / "ai.json").read_text(encoding="utf-8"))
    assert manifest.get("$schema") in (None, "catalog/schemas/ai.schema.json")


def test_the_template_repository_keeps_its_root_catalog(project):
    write(project / ".ai" / "skills" / "alpha" / "SKILL.md",
          "---\nname: alpha\ndescription: a\n---\n")
    write(project / "catalog" / "presets.json", '{"presets": {}}\n')

    assert catalog.migrate_catalog_dir() is False

    assert (project / "catalog" / "presets.json").is_file()


# ─── The resolver cache is ignored ──────────────────────────────────────────

def test_bootstrap_ignores_the_resolver_fetch_cache(project, tmp_path):
    source = make_template(tmp_path)
    write_manifest(project, source)

    assert run("bootstrap") == 0

    text = (project / ".gitignore").read_text(encoding="utf-8")
    assert ".ai/tmp/" in text


def test_gitignore_keeps_existing_lines_and_is_idempotent(project, tmp_path):
    source = make_template(tmp_path)
    write_manifest(project, source)
    write(project / ".gitignore", "node_modules/\n")

    assert run("sync") == 0
    assert run("sync") == 0

    text = (project / ".gitignore").read_text(encoding="utf-8")
    assert "node_modules/" in text
    assert text.count(".ai/tmp/") == 1


def test_an_existing_tmp_ignore_without_a_slash_is_respected(project, tmp_path):
    source = make_template(tmp_path)
    write_manifest(project, source)
    write(project / ".gitignore", ".ai/tmp\n")

    assert run("sync") == 0

    text = (project / ".gitignore").read_text(encoding="utf-8")
    # The existing form is accepted, not duplicated, and the other ignores land.
    assert text.count(".ai/tmp") == 1
    assert ".claude/skills/" in text


# ─── Agent-facing configuration ─────────────────────────────────────────────

def test_sync_copies_the_agent_configuration(project, tmp_path):
    source = make_template(tmp_path)
    write_manifest(project, source)

    assert run("sync") == 0

    assert (project / "AGENTS.md").is_file()
    assert (project / ".github" / "copilot-instructions.md").is_file()
    assert (project / ".claude" / "settings.json").is_file()
    assert (project / ".gitattributes").is_file()


def test_project_owned_agent_configuration_is_not_overwritten(project, tmp_path):
    source = make_template(tmp_path)
    write_manifest(project, source)
    write(project / "AGENTS.md", "# our own agent rules\n")

    assert run("sync") == 0

    assert (project / "AGENTS.md").read_text(encoding="utf-8") == "# our own agent rules\n"


def test_gitignore_includes_the_platform_junctions(project, tmp_path):
    source = make_template(tmp_path)
    write_manifest(project, source)

    assert run("sync") == 0

    text = (project / ".gitignore").read_text(encoding="utf-8")
    for entry in (".ai/tmp/", ".claude/skills/", ".claude/agents/",
                  ".github/skills/", ".github/agents/",
                  ".reasonix/skills/", ".reasonix/agents/",
                  "__pycache__/", ".pytest_cache/"):
        assert entry in text, entry


def test_the_application_name_reaches_the_agent_instructions(project, tmp_path):
    source = make_template(tmp_path)
    write_manifest(project, source)
    assert run("sync") == 0

    assert run("project", "--name", "Payments", "--intention", "Track invoices") == 0

    agents = (project / "AGENTS.md").read_text(encoding="utf-8")
    copilot = (project / ".github" / "copilot-instructions.md").read_text(encoding="utf-8")
    assert "Payments" in agents
    assert "{ApplicationName}" not in agents
    assert "Payments.Domain" in copilot


def test_the_validation_scripts_are_installed_too(project, tmp_path):
    source = make_template(tmp_path)
    write_manifest(project, source)

    assert run("sync") == 0

    assert (project / ".ai" / "tests" / "validate-skills.py").is_file()
    assert (project / ".ai" / "tests" / "run-all-tests.sh").is_file()


def test_an_agent_installed_under_another_name_is_not_modified(tmp_path):
    source = write(tmp_path / "src" / "agents" / "CSharpExpert.agent.md",
                   "---\nname: CSharpExpert\n---\n")
    target = write(tmp_path / "proj" / ".ai" / "agents" / "CSharpExpert.md",
                   source.read_text(encoding="utf-8"))
    item = {
        "name": "CSharpExpert",
        "sourcePath": "agents/CSharpExpert.agent.md",
        "path": ".ai/agents/CSharpExpert.md",
        "sha256": catalog.item_hash(source)[0],
    }
    entry = {"sha256": item["sha256"]}

    assert catalog.installed_hash(item, target) == item["sha256"]
    assert catalog.evaluate_status("agent", item, target, entry, False) == "ok"

    target.write_text("locally edited\n", encoding="utf-8")
    assert catalog.evaluate_status("agent", item, target, entry, False) == "local-modified"


# ─── Instructions follow the installed items ────────────────────────────────

def test_the_instructions_follow_the_installed_items(project, tmp_path):
    source = make_template(tmp_path)
    write_manifest(project, source)
    write(project / ".ai" / "skills" / "alpha" / "SKILL.md",
          "---\nname: alpha\ndescription: Alpha work\n---\n")
    write(project / ".ai" / "agents" / "lead.md",
          "---\nname: lead\ndescription: Leads work\n---\n")
    write(project / "AGENTS.md",
          "# AGENTS\n\nRead `.ai/session-context.md` first.\n\n"
          "See `.ai/skills/alpha/SKILL.md` and `.ai/skills/missing/SKILL.md`.\n")
    write(project / ".github" / "copilot-instructions.md",
          "## Project Context\n\nSee `.ai/project/architecture.md`.\n\n"
          "## Architecture\n\n- Domain: `{ApplicationName}.Domain.*`\n\n"
          "## Work-Type Context Mapping\n\n| Work | Context |\n| --- | --- |\n"
          "| Alpha | `.ai/skills/alpha/SKILL.md`, `.ai/patterns/x.md` |\n"
          "| Missing | `.ai/skills/missing/SKILL.md` |\n")

    result = catalog.update_instruction_docs()

    assert result
    for rel in ("AGENTS.md", ".github/copilot-instructions.md"):
        text = (project / rel).read_text(encoding="utf-8")
        assert ".ai/skills/missing/SKILL.md" not in text
        assert "## Installed skills and agents" in text
        assert "`alpha` — Alpha work" in text
        assert "`lead` — Leads work" in text

    copilot = (project / ".github" / "copilot-instructions.md").read_text(encoding="utf-8")
    assert "<!-- ai-workspace-manager:generated-from-agents -->" in copilot
    assert "# AGENTS" in copilot
    assert "## Project Context" in copilot
    assert "| Missing |" not in copilot
    assert "| Alpha |" in copilot and "`.ai/patterns/x.md`" in copilot

    before = (project / "AGENTS.md").read_text(encoding="utf-8")
    catalog.update_instruction_docs()
    assert (project / "AGENTS.md").read_text(encoding="utf-8") == before


def test_the_copilot_file_follows_edits_to_agents(project, tmp_path):
    source = make_template(tmp_path)
    write_manifest(project, source)
    write(project / "AGENTS.md",
          "# AGENTS\n\nRead `.ai/session-context.md` first.\n")
    write(project / ".github" / "copilot-instructions.md",
          "## Project Context\n\nSee `.ai/project/architecture.md`.\n")
    catalog.update_instruction_docs()

    agents = project / "AGENTS.md"
    agents.write_text(agents.read_text(encoding="utf-8") + "\nA new rule.\n",
                      encoding="utf-8")
    catalog.update_instruction_docs()

    copilot = (project / ".github" / "copilot-instructions.md").read_text(encoding="utf-8")
    assert "A new rule." in copilot


def test_legacy_markers_are_migrated_on_regeneration(project, tmp_path):
    """A file written before the namespace rename is rewritten, not duplicated.

    The old prefix is only reachable through the resolver's migration constant,
    which is the single place the legacy spelling lives.
    """
    legacy = catalog.LEGACY_MARKER_PREFIX
    assert legacy != catalog.MARKER_PREFIX

    source = make_template(tmp_path)
    write_manifest(project, source)
    write(project / ".ai" / "skills" / "alpha" / "SKILL.md",
          "---\nname: alpha\ndescription: Alpha work\n---\n")
    write(project / "AGENTS.md",
          "# AGENTS\n\nRead `.ai/session-context.md` first.\n\n"
          "## Installed skills and agents\n\n"
          f"{legacy}installed:start -->\n\n- `stale`\n\n"
          f"{legacy}installed:end -->\n")
    copilot_path = project / ".github" / "copilot-instructions.md"
    write(copilot_path,
          "# GitHub Copilot Instructions\n\n"
          f"{legacy}generated-from-agents -->\n\n"
          "# AGENTS\n\nRead `.ai/session-context.md` first.\n\n"
          f"{legacy}copilot-reference:start -->\n\n"
          "## Project Context\n\nSee `.ai/project/architecture.md`.\n\n"
          f"{legacy}copilot-reference:end -->\n")

    result = catalog.update_instruction_docs()

    assert result[".github/copilot-instructions.md"] == "updated"
    agents = (project / "AGENTS.md").read_text(encoding="utf-8")
    copilot = copilot_path.read_text(encoding="utf-8")
    for text in (agents, copilot):
        assert legacy not in text

    assert agents.count("<!-- ai-workspace-manager:installed:start -->") == 1
    assert agents.count("<!-- ai-workspace-manager:installed:end -->") == 1
    assert "- `stale`" not in agents
    assert "`alpha` — Alpha work" in agents

    assert copilot.count("<!-- ai-workspace-manager:generated-from-agents -->") == 1
    assert copilot.count("<!-- ai-workspace-manager:copilot-reference:start -->") == 1
    assert copilot.count("<!-- ai-workspace-manager:copilot-reference:end -->") == 1
    assert copilot.count("## Project Context") == 1
    assert "See `.ai/project/architecture.md`." in copilot

    # A file that differs only by its marker namespace is still rewritten: the
    # normalized text would generate the same document, so the comparison has to
    # be against the bytes on disk.
    agents_path = project / "AGENTS.md"
    agents_path.write_text(agents.replace(catalog.MARKER_PREFIX, legacy),
                           encoding="utf-8")
    assert catalog.update_instruction_docs()["AGENTS.md"] == "updated"
    assert legacy not in agents_path.read_text(encoding="utf-8")

    # The next pass has nothing left to migrate or rewrite.
    again = catalog.update_instruction_docs()
    assert again["AGENTS.md"] == "unchanged"
    assert again[".github/copilot-instructions.md"] == "unchanged"
