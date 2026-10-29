"""Ordering of the category rows the UI paints.

Own catalog first, imported catalogs after: the template is the repository's
catalog, awesome-copilot is the guest. Within each half the vocabulary's
declared order is preserved.
"""

import importlib.util
import sys
from pathlib import Path

SCRIPT = Path(__file__).resolve().parents[2] / "tooling" / ".ai" / "scripts" / "catalog.py"

sys.dont_write_bytecode = True


def _load_catalog():
    spec = importlib.util.spec_from_file_location("catalog_categories", SCRIPT)
    module = importlib.util.module_from_spec(spec)
    sys.modules[spec.name] = module
    spec.loader.exec_module(module)
    return module


catalog = _load_catalog()


def visible(rows):
    return [row["id"] for row in rows if row["count"] > 0]


def test_own_categories_come_before_imported_buckets():
    rows = catalog._category_rows([
        {"category": "copilot", "source": "awesome-copilot"},
        {"category": "uncategorised", "source": "template"},
        {"category": "architecture", "source": "template"},
        {"category": "testing", "source": "template"},
    ])

    # Vocabulary order inside the own half, then the imported bucket.
    assert visible(rows) == ["architecture", "testing", "uncategorised", "copilot"]


def test_an_imported_only_catalog_keeps_the_vocabulary_order():
    rows = catalog._category_rows([
        {"category": "copilot", "source": "awesome-copilot"},
        {"category": "uncategorised", "source": "awesome-copilot"},
    ])

    assert visible(rows) == ["copilot", "uncategorised"]


def test_a_bucket_holding_an_own_item_counts_as_own():
    rows = catalog._category_rows([
        {"category": "copilot", "source": "awesome-copilot"},
        {"category": "copilot", "source": "template"},
        {"category": "docs", "source": "awesome-copilot"},
    ])

    assert visible(rows) == ["copilot", "docs"]


def test_empty_vocabulary_entries_are_still_reported():
    rows = catalog._category_rows([{"category": "copilot", "source": "awesome-copilot"}])

    counts = {row["id"]: row["count"] for row in rows}
    assert counts["copilot"] == 1
    assert counts["architecture"] == 0


def test_maui_labs_plugins_are_separate_imported_buckets():
    categories = {entry["id"]: entry for entry in catalog.load_categories()["categories"]}
    sources = catalog.load_categories()["sources"]

    assert categories["dotnet-maui"]["label"] == "MAUI Labs (dotnet-maui)"
    assert categories["dotnet-maui-tooling"]["label"] == "MAUI Labs (dotnet-maui-tooling)"
    assert sources["dotnet-maui"]["category"] == "dotnet-maui"
    assert sources["dotnet-maui-tooling"]["category"] == "dotnet-maui-tooling"


def test_aspire_skills_is_its_own_imported_bucket():
    categories = {entry["id"]: entry for entry in catalog.load_categories()["categories"]}
    sources = catalog.load_categories()["sources"]

    assert categories["aspire-skills"]["label"] == "Aspire Skills"
    assert sources["aspire-skills"]["category"] == "aspire-skills"


def test_imported_buckets_keep_the_vocabulary_order_behind_our_own():
    rows = catalog._category_rows([
        {"category": "dotnet-maui", "source": "dotnet-maui"},
        {"category": "dotnet-maui-tooling", "source": "dotnet-maui-tooling"},
        {"category": "aspire-skills", "source": "aspire-skills"},
        {"category": "copilot", "source": "awesome-copilot"},
        {"category": "docs", "source": "template"},
    ])

    assert visible(rows) == [
        "docs", "copilot", "aspire-skills", "dotnet-maui", "dotnet-maui-tooling",
    ]
