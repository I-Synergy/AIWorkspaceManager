"""Sources whose items are not at one root per kind.

MAUI Labs keeps its skills under `plugins/<plugin>/skills`, its agents under
`.github/agents` and its instructions under `.github/instructions`. A source
declares those directories with `paths`; the index reads every listed tree and
records the real source path for provenance.
"""

import importlib.util
import sys
from pathlib import Path

SCRIPT = Path(__file__).resolve().parents[2] / "tooling" / ".ai" / "scripts" / "catalog.py"

sys.dont_write_bytecode = True


def _load_catalog():
    spec = importlib.util.spec_from_file_location("catalog_sources", SCRIPT)
    module = importlib.util.module_from_spec(spec)
    sys.modules[spec.name] = module
    spec.loader.exec_module(module)
    return module


catalog = _load_catalog()


def write(path: Path, text: str) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(text, encoding="utf-8")


def make_nested_source(tmp_path: Path) -> Path:
    source = tmp_path / "labs"
    write(source / "plugins" / "one" / "skills" / "alpha" / "SKILL.md",
          "---\nname: alpha\ndescription: alpha skill\n---\n")
    write(source / "plugins" / "two" / "skills" / "beta" / "SKILL.md",
          "---\nname: beta\ndescription: beta skill\n---\n")
    write(source / ".github" / "agents" / "review.agent.md",
          "---\nname: review\ndescription: review agent\n---\n")
    write(source / ".github" / "instructions" / "testing.instructions.md",
          "---\ndescription: testing instructions\n---\n")
    # A tree the source did not list must not be indexed.
    write(source / "plugins" / "three" / "skills" / "ignored" / "SKILL.md",
          "---\nname: ignored\n---\n")
    write(source / "unlisted" / "skills" / "gamma" / "SKILL.md",
          "---\nname: gamma\n---\n")
    return source


def manifest_for(source: Path, paths: dict) -> dict:
    return {
        "version": 1,
        "sources": {
            "labs": {
                "repo": "example/labs", "ref": "main", "resolve": "local",
                "path": str(source), "license": "MIT", "paths": paths,
            },
        },
        "skills": [], "agents": [], "instructions": [], "plugins": [], "hooks": [],
        "materialize": "copy", "exclude": [],
    }


def test_every_configured_directory_is_indexed(tmp_path):
    source = make_nested_source(tmp_path)
    manifest = manifest_for(source, {
        "skill": ["plugins/one/skills", "plugins/two/skills"],
        "agent": [".github/agents"],
        "instruction": [".github/instructions"],
    })

    index = catalog.build_index(manifest)
    items = {(item["kind"], item["id"]): item for item in index["items"]}

    assert set(items) == {
        ("skill", "labs:alpha"),
        ("skill", "labs:beta"),
        ("agent", "labs:review"),
        ("instruction", "labs:testing"),
    }
    # The source path is the real location, not a synthesised `skills/<name>`.
    assert items[("skill", "labs:alpha")]["sourcePath"] == "plugins/one/skills/alpha"
    assert items[("skill", "labs:beta")]["sourcePath"] == "plugins/two/skills/beta"
    assert items[("agent", "labs:review")]["sourcePath"] == ".github/agents/review.agent.md"
    # Provenance points at the upstream file, not at the installed `.ai/` path.
    assert items[("skill", "labs:alpha")]["upstream"] == (
        "https://github.com/example/labs/tree/main/plugins/one/skills/alpha"
    )
    # And the installed path stays under `.ai/`.
    assert items[("skill", "labs:beta")]["path"] == ".ai/skills/beta"


def test_an_unlisted_tree_is_not_read(tmp_path):
    source = make_nested_source(tmp_path)
    manifest = manifest_for(source, {"skill": ["plugins/one/skills"]})

    ids = [item["id"] for item in catalog.build_index(manifest)["items"]]

    assert ids == ["labs:alpha"]


def test_maui_labs_is_two_builtin_sources_with_their_nested_layouts():
    app = catalog.BUILTIN_SOURCES["dotnet-maui"]
    tooling = catalog.BUILTIN_SOURCES["dotnet-maui-tooling"]

    for entry in (app, tooling):
        assert entry["repo"] == "dotnet/maui-labs"
        assert entry["resolve"] == "remote"
        assert entry["license"] == "MIT"
    assert app["paths"]["skill"] == ["plugins/dotnet-maui/skills"]
    assert app["paths"]["agent"] == [".github/agents"]
    assert app["paths"]["instruction"] == [".github/instructions"]
    assert tooling["paths"]["skill"] == ["plugins/dotnet-maui-tooling/skills"]
    # The tooling source contributes no agents, instructions, plugins or hooks.
    assert tooling["paths"]["agent"] == []
    assert tooling["paths"]["instruction"] == []


def test_an_empty_paths_entry_disables_that_kind(tmp_path):
    source = make_nested_source(tmp_path)
    manifest = manifest_for(source, {
        "skill": ["plugins/one/skills"],
        "plugin": [],
    })

    index = catalog.build_index(manifest)
    kinds = {item["kind"] for item in index["items"]}

    # `plugins/` is the fallback for the plugin kind. An explicit empty list
    # means "this source ships none", not "use the fallback".
    assert kinds == {"skill"}
    assert [item["id"] for item in index["items"]] == ["labs:alpha"]


def test_aspire_skills_is_a_builtin_source_ahead_of_the_community_aspire_skill():
    ids = list(catalog.BUILTIN_SOURCES)
    aspire = catalog.BUILTIN_SOURCES["aspire-skills"]

    assert aspire["repo"] == "microsoft/aspire-skills"
    assert aspire["resolve"] == "remote"
    assert aspire["license"] == "MIT"
    # awesome-copilot also ships a skill named `aspire`; the official source
    # must be indexed first so its item keeps the name.
    assert ids.index("aspire-skills") < ids.index("awesome-copilot")


def test_builtin_source_order_decides_who_wins_a_name_collision(tmp_path):
    awesome = tmp_path / "awesome"
    official = tmp_path / "official"
    for root in (awesome, official):
        write(root / "skills" / "aspire" / "SKILL.md",
              "---\nname: aspire\ndescription: aspire skill\n---\n")
    manifest = {
        "version": 1,
        "sources": {
            "template": {"repo": "I-Synergy/AI", "ref": "local"},
            # Deliberately the other way round from BUILTIN_SOURCES.
            "awesome-copilot": {"repo": "example/awesome", "ref": "main",
                                "resolve": "local", "path": str(awesome)},
            "aspire-skills": {"repo": "example/official", "ref": "main",
                              "resolve": "local", "path": str(official)},
        },
        "skills": [], "agents": [], "instructions": [], "plugins": [], "hooks": [],
        "materialize": "copy", "exclude": [],
    }

    names = [name for name, _entry in catalog._sources(manifest)]
    index = catalog.build_index(manifest)
    ids = {item["id"] for item in index["items"]}

    assert names.index("aspire-skills") < names.index("awesome-copilot")
    assert "aspire-skills:aspire" in ids
    assert "awesome-copilot:aspire" not in ids
    assert [entry["id"] for entry in index["shadowed"]] == ["awesome-copilot:aspire"]


def test_prompts_and_instructions_are_indexed_into_their_own_kinds(tmp_path):
    source = tmp_path / "labs"
    write(source / "prompts" / "alpha.prompt.md",
          "---\nname: alpha\ndescription: alpha prompt\n---\n")
    write(source / "prompts" / "beta.md",
          "---\nname: beta\ndescription: beta prompt\n---\n")
    write(source / "prompts" / "README.md", "# not a prompt\n")
    write(source / "instructions" / "style.instructions.md",
          "---\ndescription: house style\n---\n")
    write(source / "instructions" / "house.md",
          "---\ndescription: plain markdown instructions\n---\n")
    manifest = manifest_for(source, {
        "prompt": ["prompts"],
        "instruction": ["instructions"],
    })

    index = catalog.build_index(manifest)
    items = {(item["kind"], item["id"]): item for item in index["items"]}

    assert set(items) == {
        ("prompt", "labs:alpha"),
        ("prompt", "labs:beta"),
        ("instruction", "labs:style"),
        ("instruction", "labs:house"),
    }
    assert items[("prompt", "labs:alpha")]["path"] == ".ai/prompts/alpha.prompt.md"
    assert items[("prompt", "labs:alpha")]["sourcePath"] == "prompts/alpha.prompt.md"
    # A prompt may be authored as plain `.md`; it installs with the prompt suffix.
    assert items[("prompt", "labs:beta")]["path"] == ".ai/prompts/beta.prompt.md"
    assert items[("instruction", "labs:style")]["path"] == \
        ".ai/instructions/style.instructions.md"
    assert items[("instruction", "labs:house")]["path"] == \
        ".ai/instructions/house.instructions.md"


def test_local_selection_finds_installed_prompts(tmp_path, monkeypatch):
    write(tmp_path / ".ai" / "prompts" / "alpha.prompt.md", "---\nname: alpha\n---\n")
    monkeypatch.setenv("CATALOG_ROOT", str(tmp_path))

    assert catalog.local_selection()["prompts"] == ["alpha"]


def test_two_sources_on_the_same_repo_share_one_cache():
    app = catalog.source_cache_dir("dotnet/maui-labs", "main")
    tooling = catalog.source_cache_dir("dotnet/maui-labs", "main")

    assert app == tooling
    assert app.name.startswith("maui-labs-main-")
    assert app != catalog.source_cache_dir("dotnet/maui-labs", "v0.1.0-preview.10")
    # A local file:// fixture must not become a Windows path that is too long.
    uri = "file:///C:/Users/someone/AppData/Local/Temp/pytest-of-someone/src"
    assert len(catalog.source_cache_dir(uri, "main").name) < 80
