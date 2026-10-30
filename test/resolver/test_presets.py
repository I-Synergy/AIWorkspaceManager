"""The curated presets: both sources, dependencies included.

The catalog itself is not vendored here, so these tests check the structure the
resolver relies on plus the closure that turns an agent into its required
skills. Every id in the real file was validated against the 1066-item catalog.
"""

import argparse
import importlib.util
import json
import sys
from pathlib import Path

SCRIPT = Path(__file__).resolve().parents[2] / "tooling" / ".ai" / "scripts" / "catalog.py"
PRESETS = SCRIPT.parent.parent.parent / "catalog" / "presets.json"

sys.dont_write_bytecode = True


def _load_catalog():
    spec = importlib.util.spec_from_file_location("catalog_presets", SCRIPT)
    module = importlib.util.module_from_spec(spec)
    sys.modules[spec.name] = module
    spec.loader.exec_module(module)
    return module


catalog = _load_catalog()

EXPECTED = [
    "Architecture",
    "Aspire",
    "Books, blogs and writing",
    "Design",
    "Devops",
    ".Net Core",
    ".Net Maui",
    ".Net Web and Blazor",
    ".Net WinUI",
    ".Net WPF",
    "Review",
    "Security",
    "Tools",
    "Minimal",
]


def preset_body(name: str) -> dict:
    data = json.loads(PRESETS.read_text(encoding="utf-8"))
    return data["presets"][name]


def ids_of(body: dict) -> list:
    ids = []
    for key in ("skills", "agents", "instructions", "plugins", "hooks"):
        ids.extend(entry for entry in body.get(key) or [] if isinstance(entry, str))
    return ids


def test_the_requested_presets_exist_in_order():
    data = json.loads(PRESETS.read_text(encoding="utf-8"))

    assert list(data["presets"]) == EXPECTED + ["Everything"]


def test_every_preset_selects_items():
    for name in EXPECTED:
        assert ids_of(preset_body(name)), name


def test_presets_draw_on_the_template_and_every_imported_source():
    all_ids = {i for name in EXPECTED for i in ids_of(preset_body(name))}

    assert any(not i.startswith(tuple(
        ("awesome-copilot:", "aspire-skills:", "dotnet-maui:", "dotnet-maui-tooling:")
    )) for i in all_ids)
    for prefix in ("awesome-copilot:", "aspire-skills:", "dotnet-maui:", "dotnet-maui-tooling:"):
        assert any(i.startswith(prefix) for i in all_ids), prefix


def test_the_aspire_preset_covers_the_official_source():
    assert ids_of(preset_body("Aspire")) == [
        "aspire-skills:aspire",
        "aspire-skills:aspire-init",
        "aspire-skills:aspireify",
        "aspire-skills:aspire-orchestration",
        "aspire-skills:aspire-monitoring",
        "aspire-skills:aspire-deployment",
        "aspire-skills:aspire-project-v2-migration",
    ]


def test_minimal_is_a_small_template_baseline():
    body = preset_body("Minimal")

    assert body["skills"] == [
        "architect",
        "code-reviewer",
        "dotnet-engineer",
        "security",
        "technical-writer",
    ]
    assert body["agents"] == ["architect", "developer", "reviewer"]
    assert body.get("recommended") is not True


def test_everything_covers_every_skill_and_agent_of_all_sources():
    body = preset_body("Everything")

    assert body["skills"] == [{"all": True}]
    assert body["agents"] == [{"all": True}]
    # Hooks stay opt-in per source, so `Everything` must not pull them in.
    assert "hooks" not in body


def test_the_old_full_preset_name_is_an_alias_for_everything():
    index_by_key = {}
    for kind, name in (("skill", "template:alpha"), ("agent", "template:lead")):
        index_by_key[(kind, name)] = {
            "id": name, "kind": kind, "source": "template", "requires": [],
        }

    resolved = catalog._preset_selection({"preset": "full"}, index_by_key)

    # `full` is not in presets.json any more; it resolves through the alias.
    assert resolved == {"skill": ["template:alpha"], "agent": ["template:lead"],
                        "instruction": [], "prompt": [], "plugin": [], "hook": []}
    assert catalog._preset_selection({"preset": "Everything"}, index_by_key) == resolved


def _index(*entries) -> dict:
    return {
        (kind, item_id): {
            "id": item_id, "kind": kind, "source": "template", "requires": [],
        }
        for kind, item_id in entries
    }


def test_presets_compose_instead_of_replacing_each_other(monkeypatch):
    monkeypatch.setattr(catalog, "_load_presets", lambda: {"presets": {
        "A": {"skills": ["template:a"]},
        "B": {"skills": ["template:b"], "agents": ["template:lead"]},
    }})
    index = _index(("skill", "template:a"), ("skill", "template:b"),
                   ("agent", "template:lead"))

    resolved = catalog._preset_selection({"presets": ["A", "B"]}, index)

    assert resolved["skill"] == ["template:a", "template:b"]
    assert resolved["agent"] == ["template:lead"]


def test_the_legacy_preset_field_is_one_entry_of_the_list():
    manifest = {"preset": "full", "presets": ["Everything", "Aspire"]}

    # `full` is an alias, and the duplicate it would add is dropped.
    assert catalog._preset_names(manifest) == ["Everything", "Aspire"]


def test_preset_add_and_remove_keep_the_rest_of_the_selection(
        tmp_path, monkeypatch, capsys):
    monkeypatch.setenv("CATALOG_ROOT", str(tmp_path))
    manifest = {
        "version": 1,
        "sources": {"template": {"repo": "owner/repo", "ref": "main"}},
        "preset": "Minimal",
        "skills": ["architect"],
        "agents": [],
        "exclude": ["code-reviewer"],
    }
    path = tmp_path / ".ai" / "ai.json"
    path.parent.mkdir(parents=True)
    path.write_text(json.dumps(manifest), encoding="utf-8")

    assert catalog.cmd_preset(
        argparse.Namespace(preset_action="add", names=["Aspire"], json=True)) == 0
    saved = json.loads(path.read_text(encoding="utf-8"))
    # The legacy singleton is migrated into the additive list.
    assert saved["presets"] == ["Minimal", "Aspire"]
    assert "preset" not in saved
    assert saved["skills"] == ["architect"]
    assert saved["exclude"] == ["code-reviewer"]

    assert catalog.cmd_preset(
        argparse.Namespace(preset_action="remove", names=["Minimal"], json=True)) == 0
    saved = json.loads(path.read_text(encoding="utf-8"))
    assert saved["presets"] == ["Aspire"]
    assert saved["skills"] == ["architect"]
    assert saved["exclude"] == ["code-reviewer"]
    capsys.readouterr()


def test_removing_a_preset_item_records_an_exclusion(tmp_path, monkeypatch):
    monkeypatch.setenv("CATALOG_ROOT", str(tmp_path))
    monkeypatch.setenv("CATALOG_OFFLINE", "1")
    for name in ("alpha", "beta"):
        skill = tmp_path / ".ai" / "skills" / name
        skill.mkdir(parents=True)
        (skill / "SKILL.md").write_text(
            f"---\nname: {name}\ndescription: {name}\n---\n\n{name}\n",
            encoding="utf-8")
    path = tmp_path / ".ai" / "ai.json"
    path.write_text(json.dumps({
        "version": 1,
        "sources": {"template": {"repo": "owner/repo", "ref": "main"}},
        "presets": ["Everything"],
        "skills": [],
        "agents": [],
    }), encoding="utf-8")

    assert catalog.cmd_remove(argparse.Namespace(
        ids=["alpha"], kind=None, force=False, prune=False, json=True)) == 0

    saved = json.loads(path.read_text(encoding="utf-8"))
    # The item stays available to others but is opted out of the preset.
    assert saved["exclude"] == ["alpha"]
    assert saved["presets"] == ["Everything"]
    assert saved["skills"] == []


def test_an_agent_preset_brings_its_dependencies():
    index_by_key = {
        ("agent", "template:lead"): {
            "id": "template:lead", "kind": "agent", "source": "template",
            "requires": ["template:alpha", "template:beta"],
        },
        ("skill", "template:alpha"): {
            "id": "template:alpha", "kind": "skill", "source": "template", "requires": [],
        },
        ("skill", "template:beta"): {
            "id": "template:beta", "kind": "skill", "source": "template", "requires": [],
        },
    }

    resolved = catalog._closure([("agent", "template:lead")], index_by_key)

    # Dependencies first, the thing that asked for them last.
    assert resolved == [
        ("skill", "template:alpha"),
        ("skill", "template:beta"),
        ("agent", "template:lead"),
    ]


def test_the_uncategorised_bucket_is_labelled_i_synergy():
    categories = {entry["id"]: entry for entry in catalog.load_categories()["categories"]}

    assert categories["uncategorised"]["label"] == "I-Synergy"


def test_net_core_is_the_recommended_default():
    data = json.loads(PRESETS.read_text(encoding="utf-8"))
    recommended = [name for name, body in data["presets"].items() if body.get("recommended")]

    assert recommended == [".Net Core"]


def test_maui_presets_select_the_imported_skills_from_maui_labs():
    maui = preset_body(".Net Maui")["skills"]
    design = preset_body("Design")["skills"]

    assert "dotnet-maui:maui-performance" in maui
    assert "dotnet-maui-tooling:maui-devflow-debug" in maui
    assert "dotnet-maui-tooling:android-java-bindings" in maui
    assert "dotnet-maui:maui-accessibility" in design
    # The template keeps its own MAUI skills bare.
    assert "maui-specialist" in maui
    assert "maui-blazor" in maui
    assert "maui-apple-design" in maui
    assert "mobile-release" in maui
    # Nothing that moved to the source is still referenced as a template item.
    assert not {"maui-performance", "maui-app-architecture", "devflow-connect"} & set(maui)
    assert "dotnet-maui:expert-reviewer" in preset_body(".Net Maui")["agents"]


def test_aspire_deployment_also_appears_in_the_devops_preset():
    devops = preset_body("Devops")["skills"]

    assert "aspire-skills:aspire-deployment" in devops
    # The dedicated Aspire preset still carries the whole workflow.
    assert "aspire-skills:aspire" in preset_body("Aspire")["skills"]
