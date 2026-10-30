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
import { copyFile, mkdir, readdir, readFile, stat, writeFile } from "node:fs/promises";
import { dirname, join, relative, resolve, sep } from "node:path";
import { fileURLToPath } from "node:url";

const HERE = dirname(fileURLToPath(import.meta.url));
const TOOLING = join(HERE, "..", "tooling");

const GITIGNORE_ENTRIES = [
  "__pycache__/", "*.py[cod]", ".pytest_cache/", ".ai/tmp/",
  ".claude/skills/", ".claude/agents/", ".github/skills/", ".github/agents/",
  ".github/instructions/", ".github/prompts/",
  ".claude/commands/", ".claude/instructions/",
  ".reasonix/skills/", ".reasonix/agents/",
];

async function walk(directory) {
  const entries = await readdir(directory, { withFileTypes: true });
  const nested = await Promise.all(entries.map((entry) => {
    const full = join(directory, entry.name);
    return entry.isDirectory() ? walk(full) : Promise.resolve([full]);
  }));
  return nested.flat();
}

async function exists(path) {
  try {
    await stat(path); // NOSONAR -- safePath() confines the target
    return true;
  } catch {
    return false;
  }
}

/** Resolve a path and refuse anything that escapes the target repository. */
function safePath(target, ...parts) {
  const base = resolve(target);
  const path = resolve(base, ...parts);
  if (path !== base && !path.startsWith(base + sep)) {
    throw new Error(`refusing path outside target repository: ${path}`);
  }
  return path;
}

async function copyTooling(target, overwrite) {
  const sources = await walk(TOOLING);
  const results = await Promise.all(sources.map(async (source) => {
    // Normalise to forward slashes: `relative()` returns backslashes on Windows
    // and a prefix test against "scripts/" would never match.
    const rel = relative(TOOLING, source).split(sep).join("/");
    if (rel === "tooling.json") {
      return { copied: [], skipped: [] };
    }
    // The bundle keeps the vocabulary at `catalog/`; a project installs it
    // under `.ai/catalog`, so everything the project owns lives in `.ai/`.
    const destinationRel = rel.startsWith("catalog/") ? `.ai/${rel}` : rel;
    const destination = safePath(target, ...destinationRel.split("/"));
    if (!overwrite && (await exists(destination))) {
      return { copied: [], skipped: [destination] };
    }
    await mkdir(dirname(destination), { recursive: true });
    await copyFile(source, destination);
    return { copied: [destination], skipped: [] };
  }));
  return {
    copied: results.flatMap((result) => result.copied),
    skipped: results.flatMap((result) => result.skipped),
  };
}

/** A bare repo gets a starter manifest that resolves the template and sources. */
async function writeStarterManifest(target, overwrite) {
  const manifestPath = safePath(target, ".ai", "ai.json");
  if (!overwrite && (await exists(manifestPath))) {
    return;
  }
  await mkdir(dirname(manifestPath), { recursive: true }); // NOSONAR
  const manifest = {
    "$schema": "catalog/schemas/ai.schema.json",
    version: 1,
    clients: ["claude"],
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
    presets: [],
    skills: [], agents: [], instructions: [], plugins: [], hooks: [],
    materialize: "copy",
    exclude: [],
    locked: [],
  };
  await writeFile(manifestPath, // NOSONAR
    JSON.stringify(manifest, null, 2) + String.fromCodePoint(10), "utf8");
}

function separator(text) {
  if (!text) {
    return "";
  }
  return text.endsWith("\n") ? "\n" : "\n\n";
}

async function updateGitignore(target) {
  const gitignore = safePath(target, ".gitignore");
  let ignore = "";
  try {
    ignore = await readFile(gitignore, "utf8"); // NOSONAR
  } catch {
    /* no .gitignore yet */
  }
  const present = new Set(ignore.split(/\r?\n/)
    .map((line) => line.trim().replace(/\/$/, ""))
    .filter((line) => line && !line.startsWith("#")));
  const missing = GITIGNORE_ENTRIES.filter(
    (entry) => !present.has(entry.replace(/\/$/, "")));
  if (missing.length === 0) {
    return;
  }
  const block = "# AI Workspace Manager: resolver cache and platform junctions\n"
    + missing.join("\n") + "\n";
  await writeFile(gitignore, ignore + separator(ignore) + block, "utf8"); // NOSONAR
}

async function main() {
  const rawTarget = process.argv[2];
  const overwrite = process.argv.includes("--overwrite");
  if (!rawTarget) {
    throw new Error("usage: install-tooling.mjs <target-repo-root> [--overwrite]");
  }

  const target = resolve(rawTarget);
  const { copied, skipped } = await copyTooling(target, overwrite);
  await writeStarterManifest(target, overwrite);
  await updateGitignore(target);

  const manifest = JSON.parse(await readFile(join(TOOLING, "tooling.json"), "utf8"));
  process.stdout.write(JSON.stringify({
    copied,
    skipped,
    version: manifest.files.length,
  }) + "\n");
}

try {
  await main();
} catch (error) {
  process.stderr.write(`${error.message}\n`);
  process.exitCode = 1;
}
