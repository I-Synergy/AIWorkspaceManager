/**
 * Development server for the AI Workspace Manager webview.
 *
 * Serves the real bundled UI (`dist/web.js` + `dist/web.css`) against fixture
 * data, so the pages can be opened in a browser — and screenshotted — without
 * VS Code. Query parameters:
 *
 *   ?theme=light      light palette instead of dark
 *   ?scenario=empty   a brand-new repository with nothing selected
 */
import { createServer } from "node:http";
import { readFile } from "node:fs/promises";
import { extname, join, resolve, sep } from "node:path";
import { fileURLToPath } from "node:url";

const ROOT = fileURLToPath(new URL("..", import.meta.url));
const TOOLS = join(ROOT, "tools");
const DIST = join(ROOT, "dist");
const FIXTURES = join(TOOLS, "fixtures");
const PORT = Number(process.env.PORT ?? 3731);

const MIME = {
  ".html": "text/html; charset=utf-8",
  ".js": "text/javascript; charset=utf-8",
  ".mjs": "text/javascript; charset=utf-8",
  ".css": "text/css; charset=utf-8",
  ".json": "application/json; charset=utf-8",
  ".svg": "image/svg+xml",
};

function safeFile(root, relative) {
  const base = resolve(root);
  const file = resolve(base, relative);
  return file === base || file.startsWith(base + sep) ? file : null;
}

async function serveFrom(res, root, relative) {
  const file = safeFile(root, relative);
  if (!file) {
    res.writeHead(403, { "content-type": "text/plain" });
    res.end("forbidden");
    return;
  }
  return serve(res, file);
}

async function serve(res, file) {
  try {
    const data = await readFile(file);
    res.writeHead(200, {
      "content-type": MIME[extname(file)] ?? "application/octet-stream",
      "cache-control": "no-store",
    });
    res.end(data);
  } catch {
    res.writeHead(404, { "content-type": "text/plain" });
    res.end(`not found: ${file}`);
  }
}

createServer(async (req, res) => {
  const url = (req.url ?? "/").split("?")[0];
  if (url === "/" || url === "/index.html") {
    return serve(res, join(TOOLS, "index.html"));
  }
  if (url.startsWith("/dist/")) {
    return serveFrom(res, DIST, url.slice("/dist/".length));
  }
  if (url.startsWith("/fixtures/")) {
    return serveFrom(res, FIXTURES, url.slice("/fixtures/".length));
  }
  return serveFrom(res, TOOLS, url.slice(1));
}).listen(PORT, "127.0.0.1", () => {
  console.log(`AI Workspace Manager dev harness: http://localhost:${PORT}`);
  console.log(`  light theme: http://localhost:${PORT}/?theme=light`);
  console.log(`  empty repo : http://localhost:${PORT}/?scenario=empty`);
});
