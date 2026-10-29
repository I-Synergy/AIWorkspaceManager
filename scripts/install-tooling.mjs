/**
 * Install the resolver's tooling into a repository that does not have it.
 *
 *   node scripts/install-tooling.mjs <target-repo-root> [--overwrite]
 *
 * Prints one JSON line: { copied: [...], skipped: [...] }. A file that already
 * exists is left alone unless --overwrite, so a project's locally adapted script
 * is never replaced behind its back.
 *
 * Runs as a plain script (not inside the extension host) so the same code path
 * can be exercised from a terminal, which is how it was verified.
 */
import { copyFile, mkdir, readFile, readdir, stat, writeFile } from "node:fs/promises";
import { dirname, join, relative, sep } from "node:path";
import { fileURLToPath } from "node:url";

const HERE = dirname(fileURLToPath(import.meta.url));
const TOOLING = join(HERE, "..", "tooling");

async function walk(directory) {
  const entries = await readdir(directory, { withFileTypes: true });
  const files = [];
  for (const entry of entries) {
    const full = join(directory, entry.name);
    if (entry.isDirectory()) {
      files.push(...(await walk(full)));
    } else {
      files.push(full);
    }
  }
  return files;
}

async function exists(path) {
  return stat(path).then(() => true, () => false);
}

async function main() {
  const target = process.argv[2];
  const overwrite = process.argv.includes("--overwrite");
  if (!target) {
    throw new Error("usage: install-tooling.mjs <target-repo-root> [--overwrite]");
  }

  const sources = await walk(TOOLING);
  const copied = [];
  const skipped = [];

  for (const source of sources) {
    // Normalise to forward slashes: `relative()` returns backslashes on Windows
    // and a prefix test against "scripts/" would never match.
    const rel = relative(TOOLING, source).split(sep).join("/");
    if (rel === "tooling.json") {
      continue;
    }
    // The bundle keeps the vocabulary at `catalog/`; a project installs it
    // under `.ai/catalog`, so everything the project owns lives in `.ai/`.
    const destinationRel = rel.startsWith("catalog/") ? `.ai/${rel}` : rel;
    const destination = join(target, ...destinationRel.split("/"));
    if (!overwrite && (await exists(destination))) {
      skipped.push(destination);
      continue;
    }
    await mkdir(dirname(destination), { recursive: true });
    await copyFile(source, destination);
    copied.push(destination);
  }

  // A repo with no catalog of its own gets a starter manifest that resolves the
  // template and the copilot catalog remotely. Without it the workspace has an
  // empty catalog and even the presets cannot resolve, which is what a bare
  // tooling copy produced on a real project.
  const manifestPath = join(target, ".ai", "ai.json");
  let starter = "kept";
  if (!overwrite && (await exists(manifestPath))) {
    starter = "kept";
  } else {
    await mkdir(dirname(manifestPath), { recursive: true });
    await writeFile(manifestPath, JSON.stringify({
      "$schema": "catalog/schemas/ai.schema.json",
      version: 1,
      sources: {
        "template": { repo: "I-Synergy/AI", ref: "main", resolve: "remote" },
        "aspire-skills": {
          repo: "microsoft/aspire-skills", ref: "main", resolve: "remote", license: "MIT",
        },
        "awesome-copilot": {
          repo: "github/awesome-copilot", ref: "main", resolve: "remote", license: "MIT",
        },
        "dotnet-maui": {
          repo: "dotnet/maui-labs", ref: "main", resolve: "remote", license: "MIT",
          paths: {
            skill: ["plugins/dotnet-maui/skills"],
            agent: [".github/agents"],
            instruction: [".github/instructions"],
            plugin: [], hook: [],
          },
        },
        "dotnet-maui-tooling": {
          repo: "dotnet/maui-labs", ref: "main", resolve: "remote", license: "MIT",
          paths: {
            skill: ["plugins/dotnet-maui-tooling/skills"],
            agent: [], instruction: [], plugin: [], hook: [],
          },
        },
      },
      skills: [], agents: [], instructions: [], plugins: [], hooks: [],
      materialize: "copy",
      exclude: [],
    }, null, 2) + String.fromCharCode(10), "utf8");
    starter = "created";
  }

  // Resolver state and the platform junctions (the latter point into `.ai/`).
  const entries = [
    "__pycache__/", "*.py[cod]", ".pytest_cache/", ".ai/tmp/",
    ".claude/skills/", ".claude/agents/", ".github/skills/", ".github/agents/",
    ".github/instructions/", ".github/prompts/",
    ".claude/commands/", ".claude/instructions/",
    ".reasonix/skills/", ".reasonix/agents/",
  ];
  const gitignore = join(target, ".gitignore");
  let ignore = "";
  try {
    ignore = await readFile(gitignore, "utf8");
  } catch {
    /* no .gitignore yet */
  }
  const present = new Set(ignore.split(/\r?\n/)
    .map((line) => line.trim().replace(/\/$/, ""))
    .filter((line) => line && !line.startsWith("#")));
  const missing = entries.filter((entry) => !present.has(entry.replace(/\/$/, "")));
  if (missing.length > 0) {
    const block = "# AI Workspace Manager: resolver cache and platform junctions\n"
      + missing.join("\n") + "\n";
    const content = !ignore ? block
      : ignore + (ignore.endsWith("\n") ? "\n" : "\n\n") + block;
    await writeFile(gitignore, content, "utf8");
  }

  const manifest = JSON.parse(await readFile(join(TOOLING, "tooling.json"), "utf8"));
  process.stdout.write(JSON.stringify({
    copied,
    skipped,
    version: manifest.files.length,
  }) + "\n");
}

main().catch((error) => {
  process.stderr.write(`${error.message}\n`);
  process.exit(1);
});
