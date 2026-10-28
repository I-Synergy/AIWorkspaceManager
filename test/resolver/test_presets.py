"""The curated presets: both sources, dependencies included.

The catalog itself is not vendored here, so these tests check the structure the
resolver relies on plus the closure that turns an agent into its required
skills. Every id in the real file was validated against the 1066-item catalog.
"""

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
    "Security",
    "Design",
    "Review",
    "Devops",
    ".Net Core",
    ".Net Maui",
    ".Net WPF",
    ".Net WinUI",
    "Books, blogs and writing",
    ".Net Web and Blazor",
    "Tools",
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

    assert list(data["presets"]) == EXPECTED + ["full"]


def test_every_preset_selects_from_both_sources():
    for name in EXPECTED:
        ids = ids_of(preset_body(name))
        assert ids, name
        assert any(i.startswith("awesome-copilot:") for i in ids), name
        assert any(not i.startswith("awesome-copilot:") for i in ids), name


def test_full_covers_every_skill_and_agent_of_both_sources():
    body = preset_body("full")

    assert body["skills"] == [{"all": True}]
    assert body["agents"] == [{"all": True}]
    # Hooks stay opt-in per source, so `full` must not pull them in.
    assert "hooks" not in body


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


def test_the_uncategorised_bucket_is_labelled_template():
    categories = {entry["id"]: entry for entry in catalog.load_categories()["categories"]}

    assert categories["uncategorised"]["label"] == "Template"


def test_net_core_is_the_recommended_default():
    data = json.loads(PRESETS.read_text(encoding="utf-8"))
    recommended = [name for name, body in data["presets"].items() if body.get("recommended")]

    assert recommended == [".Net Core"]
