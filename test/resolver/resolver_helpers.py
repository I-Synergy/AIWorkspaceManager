"""Small helpers shared by the resolver test modules."""

import importlib.util
import json
import subprocess
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
SCRIPT = ROOT / "tooling" / ".ai" / "scripts" / "catalog.py"


def load_catalog(module_name: str):
    spec = importlib.util.spec_from_file_location(module_name, SCRIPT)
    module = importlib.util.module_from_spec(spec)
    sys.modules[spec.name] = module
    spec.loader.exec_module(module)
    return module


def run(catalog, *argv):
    args = catalog.build_parser().parse_args([str(value) for value in argv])
    try:
        return args.handler(args)
    except SystemExit as exc:
        return exc.code if isinstance(exc.code, int) else 1


def run_json(catalog, capsys, *argv):
    capsys.readouterr()
    assert run(catalog, *argv) == 0
    return json.loads(capsys.readouterr().out)


def git(repo: Path, *args: str) -> str:
    result = subprocess.run(
        ["git", "-C", str(repo), "-c", "user.name=Test", "-c", "user.email=t@example.com", *args],
        capture_output=True, text=True, check=True,
    )
    return result.stdout.strip()


def write(path: Path, text: str) -> Path:
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(text, encoding="utf-8", newline="\n")
    return path


def write_skill(root: Path, name: str, description: str) -> Path:
    skill = Path(root) / ".ai" / "skills" / name
    skill.mkdir(parents=True, exist_ok=True)
    (skill / "SKILL.md").write_text(
        f"---\nname: {name}\ndescription: {description}\n---\n\n{description}\n",
        encoding="utf-8")
    return skill


def make_source(tmp_path: Path, description: str = "alpha skill") -> Path:
    source = tmp_path / "src"
    skill = source / ".ai" / "skills" / "alpha"
    skill.mkdir(parents=True)
    (skill / "SKILL.md").write_text(
        f"---\nname: alpha\ndescription: {description}\n---\n", encoding="utf-8")
    git(source, "init", "-q", "-b", "main")
    git(source, "add", "-A")
    git(source, "commit", "-q", "-m", "v1")
    return source


def read_manifest(root: Path) -> dict:
    return json.loads((root / ".ai" / "ai.json").read_text(encoding="utf-8"))
