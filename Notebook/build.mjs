import { fileURLToPath } from "node:url";
process.chdir(fileURLToPath(new URL(".", import.meta.url)));
import { build } from "esbuild";
import { cp, mkdir, rm, copyFile } from "node:fs/promises";
const out = "NotebookWeb";
await rm(out, { recursive: true, force: true });
await mkdir(out);
await cp("node_modules/vditor/dist", `${out}/vendor/vditor/dist`, {
  recursive: true,
});
await copyFile("node_modules/vditor/LICENSE", `${out}/vendor/vditor/LICENSE`);
await copyFile("node_modules/lucide/LICENSE", `${out}/LUCIDE-LICENSE`);
await copyFile(
  "node_modules/markdown-it/LICENSE",
  `${out}/MARKDOWN-IT-LICENSE`,
);
await copyFile(
  "../MACAssistant/Resources/Generated/AppIcon-source.png",
  `${out}/app-icon.png`,
);
await copyFile(
  "../MACAssistant/Assets.xcassets/AppEmblem.imageset/AppEmblem.png",
  `${out}/app-emblem.png`,
);
await cp("src/index.html", `${out}/index.html`);
await cp("src/style.css", `${out}/style.css`);
await cp("src/code-editor.html", `${out}/code-editor.html`);
await cp("src/code-editor.css", `${out}/code-editor.css`);
const codeEditorBuild = await build({
  absWorkingDir: process.cwd(),
  entryPoints: ["src/code-editor.mjs"],
  bundle: true,
  outfile: `${out}/code-editor.js`,
  metafile: true,
  minify: true,
  format: "iife",
  target: ["safari16", "chrome110"],
});
await build({
  absWorkingDir: process.cwd(),
  entryPoints: ["src/app.mjs"],
  bundle: true,
  outfile: `${out}/app.js`,
  format: "iife",
  target: ["safari16", "chrome110"],
});

// PWA shell is separate from private notebook data; all clients share this editor bundle.
await copyFile("src/manifest.webmanifest", `${out}/manifest.webmanifest`);
await copyFile(
  "../MACAssistant/Assets.xcassets/AppIcon.appiconset/AppIcon-256.png",
  `${out}/app-icon-256.png`,
);
await copyFile(
  "../MACAssistant/Assets.xcassets/AppIcon.appiconset/AppIcon-512.png",
  `${out}/app-icon-512.png`,
);
const { readFile, writeFile } = await import("node:fs/promises");
const editorPackages = new Set(
  Object.keys(codeEditorBuild.metafile.inputs).flatMap((input) => {
    const match = input.match(/node_modules\/((?:@[^/]+\/)?[^/]+)\//);
    return match ? [match[1]] : [];
  }),
);
const editorLicenses = [];
for (const name of [...editorPackages].sort()) {
  editorLicenses.push(
    `=== ${name} ===\n${await readFile(`node_modules/${name}/LICENSE`, "utf8")}`,
  );
}
await writeFile(`${out}/CODEMIRROR-LICENSE`, editorLicenses.join("\n\n"));
const { createHash } = await import("node:crypto");
const assets = [
  "/index.html",
  "/app.js",
  "/style.css",
  "/app-icon.png",
  "/app-emblem.png",
  "/app-icon-256.png",
  "/app-icon-512.png",
  "/manifest.webmanifest",
  "/vendor/vditor/dist/index.css",
  "/vendor/vditor/dist/index.min.js",
  "/vendor/vditor/dist/js/icons/ant.js",
  "/vendor/vditor/dist/js/lute/lute.min.js",
  "/vendor/vditor/dist/js/i18n/zh_CN.js",
  "/vendor/vditor/dist/css/content-theme/light.css",
  "/vendor/vditor/dist/css/content-theme/dark.css",
  "/vendor/vditor/dist/js/highlight.js/highlight.min.js",
  "/vendor/vditor/dist/js/highlight.js/third-languages.js",
  "/vendor/vditor/dist/js/highlight.js/styles/github.min.css",
  "/vendor/vditor/dist/js/highlight.js/styles/github-dark.min.css",
];
const existing = [];
const hash = createHash("sha256");
for (const asset of assets) {
  try {
    const bytes = await readFile(out + asset);
    hash.update(asset).update(bytes);
    existing.push(asset);
  } catch (e) {
    throw new Error(`Missing offline asset: ${asset}`, { cause: e });
  }
}
const worker = (await readFile("src/sw.js", "utf8"))
  .replace(
    "__CACHE_NAME__",
    JSON.stringify("notes-shell-" + hash.digest("hex").slice(0, 16)),
  )
  .replace("__OFFLINE_ASSETS__", JSON.stringify(existing));
await writeFile(`${out}/sw.js`, worker);
