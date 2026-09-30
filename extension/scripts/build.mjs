// Build: bundles the extension into build/ (default) or the tests into build/test/ (`tests` arg).
import { build } from "esbuild";
import { cpSync, mkdirSync, rmSync, existsSync } from "node:fs";
import { dirname, join } from "node:path";
import { fileURLToPath } from "node:url";

const root = join(dirname(fileURLToPath(import.meta.url)), "..");
const mode = process.argv[2] ?? "extension";

if (mode === "tests") {
  const out = join(root, "build", "test");
  rmSync(out, { recursive: true, force: true });
  await build({
    entryPoints: [join(root, "src", "tracker.test.ts")],
    bundle: true,
    platform: "node",
    format: "esm",
    target: "node22",
    outdir: out,
    outExtension: { ".js": ".mjs" },
    external: ["node:*"],
    logLevel: "info",
  });
} else {
  const out = join(root, "build");
  rmSync(out, { recursive: true, force: true });
  mkdirSync(out, { recursive: true });
  await build({
    entryPoints: [join(root, "src", "background.ts"), join(root, "src", "popup.ts")],
    bundle: true,
    minify: true,
    format: "iife",
    platform: "browser",
    target: "chrome110",
    outdir: out,
    logLevel: "info",
  });
  for (const f of ["manifest.json", "popup.html", "popup.css"]) cpSync(join(root, f), join(out, f));
  if (!existsSync(join(root, "icons"))) throw new Error("icons/ missing: run `node scripts/make-icons.mjs` first");
  cpSync(join(root, "icons"), join(out, "icons"), { recursive: true });
  console.log("extension built to", out);
}
