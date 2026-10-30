"""`catalog.py project` — the name and intention a new repository starts from.

The name defaults to the solution (`.sln`/`.slnx`) or the folder. Writing fills
the still-template documents only; a customized one is never rewritten.
"""

import json
from pathlib import Path

import pytest

from resolver_helpers import (
    load_catalog, run as run_catalog, run_json as run_catalog_json, write,
)

catalog = load_catalog("catalog_project")


def run(*argv):
    return run_catalog(catalog, *argv)


def run_json(capsys, *argv):
    return run_catalog_json(catalog, capsys, *argv)


SESSION_TEMPLATE = """# Session Context Template (CUSTOMIZE THIS)

**Instructions:** Copy this to `.ai/session-context.md` and customize for your project.

# {ApplicationName} Session Context

**Last Updated:** [DateTime]
**Updated By:** [Claude Code | Copilot | Reasonix Code | User]
**Project Version:** [Version]

---

## Project Overview

**Application Name:** [{ApplicationName}]
**Purpose:** [What this application does]
**Current Phase:** [Development / Testing / Production]
"""

ARCHITECTURE_TEMPLATE = """# Project Architecture (CUSTOMIZE THIS)

**Instructions:** Document your project's architecture decisions and patterns.

## Architecture Style

**Primary Architecture:** [Clean Architecture / Hexagonal / Onion / Layered / Custom]
"""

DOMAINS_TEMPLATE = """# Business Domains (CUSTOMIZE THIS)

**Instructions:** Document your project's business domains and entities.

## Domain Overview
"""


@pytest.fixture
def scaffold(project):
    write(project / ".ai" / "session-context.md", SESSION_TEMPLATE)
    write(project / ".ai" / "project" / "architecture.md", ARCHITECTURE_TEMPLATE)
    write(project / ".ai" / "project" / "domains.md", DOMAINS_TEMPLATE)
    return project


# ─── Reading ────────────────────────────────────────────────────────────────

def test_the_name_is_detected_from_the_solution_before_the_folder(project, capsys):
    write(project / "Payments.slnx", "<Solution />\n")

    info = run_json(capsys, "project", "--json")

    assert info["detectedName"] == "Payments"
    assert info["name"] == "Payments"


def test_the_folder_name_is_the_fallback(project, capsys):
    info = run_json(capsys, "project", "--json")

    assert info["detectedName"] == "proj"


# ─── Writing ────────────────────────────────────────────────────────────────

def test_writing_stores_the_info_and_fills_the_template_documents(scaffold, capsys):
    info = run_json(capsys, "project", "--name", "Payments",
                    "--intention", "Track invoices and payouts", "--json")

    assert info["name"] == "Payments"
    assert ".ai/session-context.md" in info["written"]
    assert ".ai/project/architecture.md" in info["written"]
    assert ".ai/project/domains.md" in info["written"]

    stored = json.loads((scaffold / ".ai" / "project" / "project.json")
                        .read_text(encoding="utf-8"))
    assert stored["name"] == "Payments"
    assert stored["intention"] == "Track invoices and payouts"

    session = (scaffold / ".ai" / "session-context.md").read_text(encoding="utf-8")
    assert "# Payments Session Context" in session
    assert "**Application Name:** Payments" in session
    assert "**Purpose:** Track invoices and payouts" in session
    assert "{ApplicationName}" not in session

    architecture = (scaffold / ".ai" / "project" / "architecture.md").read_text(encoding="utf-8")
    assert architecture.startswith("# Payments — Project Architecture")
    assert "**Intention:** Track invoices and payouts" in architecture
    assert "(CUSTOMIZE THIS)" not in architecture


def test_a_customized_document_is_never_rewritten(scaffold, capsys):
    session = scaffold / ".ai" / "session-context.md"
    session.write_text("# Payments Session Context\n\nOur own notes.\n", encoding="utf-8")

    info = run_json(capsys, "project", "--name", "Payments",
                    "--intention", "Track invoices", "--json")

    assert ".ai/session-context.md" in info["skipped"]
    assert ".ai/session-context.md" not in info["written"]
    assert session.read_text(encoding="utf-8") == "# Payments Session Context\n\nOur own notes.\n"


def test_the_read_returns_what_was_written(scaffold, capsys):
    run_json(capsys, "project", "--name", "Payments", "--intention", "Track invoices", "--json")

    info = run_json(capsys, "project", "--json")

    assert info["name"] == "Payments"
    assert info["intention"] == "Track invoices"


def test_the_name_is_filled_across_the_scaffold_folders(scaffold, capsys):
    write(scaffold / ".ai" / "patterns" / "api-patterns.md",
          "# API patterns\n\n`{ApplicationName}.{Domain}.{Entity}`\n")
    write(scaffold / ".ai" / "reference" / "templates" / "endpoint.cs.txt",
          "namespace {ApplicationName}.Api;\n")
    # Installed items are not scaffold: their placeholders stay for the agent.
    write(scaffold / ".ai" / "skills" / "alpha" / "SKILL.md",
          "---\nname: alpha\ndescription: a\n---\n\n{ApplicationName}\n")

    info = run_json(capsys, "project", "--name", "Payments",
                    "--intention", "Track invoices", "--json")

    assert ".ai/patterns/api-patterns.md" in info["written"]
    assert (scaffold / ".ai" / "patterns" / "api-patterns.md").read_text(
        encoding="utf-8") == "# API patterns\n\n`Payments.{Domain}.{Entity}`\n"
    assert (scaffold / ".ai" / "reference" / "templates" / "endpoint.cs.txt").read_text(
        encoding="utf-8") == "namespace Payments.Api;\n"
    assert (scaffold / ".ai" / "skills" / "alpha" / "SKILL.md").read_text(
        encoding="utf-8").endswith("{ApplicationName}\n")


def test_bootstrap_creates_a_starter_readme(project):
    assert catalog.ensure_readme() == "created"

    text = (project / "README.md").read_text(encoding="utf-8")
    assert text.startswith("# proj")
    assert "[Describe the project's purpose.]" in text


def test_the_project_form_fills_the_starter_readme(project, capsys):
    catalog.ensure_readme()

    info = run_json(capsys, "project", "--name", "Payments",
                    "--intention", "Track invoices", "--json")

    assert "README.md" in info["written"]
    assert (project / "README.md").read_text(encoding="utf-8") \
        == "# Payments\n\nTrack invoices\n"


def test_a_customized_readme_is_left_alone(project, capsys):
    write(project / "README.md", "# Our project\n\nOur own notes.\n")

    info = run_json(capsys, "project", "--name", "Payments",
                    "--intention", "Track invoices", "--json")

    assert "README.md" not in info["written"]
    assert (project / "README.md").read_text(encoding="utf-8") \
        == "# Our project\n\nOur own notes.\n"


def test_the_intention_can_be_added_after_the_name(scaffold, capsys):
    run_json(capsys, "project", "--name", "Payments", "--json")

    info = run_json(capsys, "project", "--name", "Payments",
                    "--intention", "Track invoices", "--json")

    assert ".ai/session-context.md" in info["written"]
    session = (scaffold / ".ai" / "session-context.md").read_text(encoding="utf-8")
    assert "**Purpose:** Track invoices" in session
    architecture = (scaffold / ".ai" / "project" / "architecture.md").read_text(encoding="utf-8")
    assert "**Intention:** Track invoices" in architecture
