const { build } = require("esbuild");

const base = {
  bundle: true,
  minify: process.env.NODE_ENV === "production",
  sourcemap: process.env.NODE_ENV !== "production",
  loader: {
    ".ttf": "dataurl",
    ".woff": "dataurl",
    ".svg": "dataurl",
  },
};

const extensionConfig = {
  ...base,
  platform: "node",
  format: "cjs",
  entryPoints: ["./src/host/extension.ts"],
  outfile: "./dist/extension.js",
  external: ["vscode"],
};

const webConfig = {
  ...base,
  platform: "browser",
  target: "es2021",
  format: "esm",
  entryPoints: ["./src/web/main.ts"],
  outfile: "./dist/web.js",
};

const cssConfig = {
  ...base,
  entryPoints: ["./src/web/main.css"],
  outfile: "./dist/web.css",
  minify: true,
};

(async () => {
  await Promise.all([extensionConfig, webConfig, cssConfig].map((config) => build(config)));
  console.log("build complete");
})().catch((error) => {
  process.stderr.write(error.stderr ?? String(error));
  process.exit(1);
});
