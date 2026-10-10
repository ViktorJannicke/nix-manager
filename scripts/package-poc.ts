// Proof of concept: add or remove a Home Manager package by editing the Nix
// source, verify the edit by evaluation, then optionally write it.
//
//   bun scripts/package-poc.ts --flake DIR --home CONFIG add|remove ATTR FILE [--write]
//
// ATTR is a path such as pkgs.krita; FILE is relative to the flake root.
import { readFile, rename, writeFile } from "node:fs/promises";
import { join } from "node:path";
import { parseArgs } from "node:util";
import { Effect } from "effect";
import { addPackage, removePackage, verifyEdit, type Edit } from "../src/packages";
import { runnerLayer } from "../src/process";

const { values, positionals } = parseArgs({
  options: {
    flake: { type: "string" },
    home: { type: "string" },
    write: { type: "boolean", default: false },
  },
  allowPositionals: true,
});
const [kind, attr, file] = positionals;
if (!values.flake || !values.home || (kind !== "add" && kind !== "remove") || !attr || !file) {
  console.error("Usage: package-poc.ts --flake DIR --home CONFIG add|remove ATTR FILE [--write]");
  process.exit(2);
}

/** The changed lines with two lines of context, in unified diff style. */
const hunk = (before: string, edit: Edit) => {
  const old = before.split("\n");
  const now = edit.source.split("\n");
  let first = 0;
  while (first < old.length && old[first] === now[first]) first++;
  let tail = 0;
  while (tail < old.length - first && old.at(-1 - tail) === now.at(-1 - tail)) tail++;
  const lines = [
    ...old.slice(Math.max(0, first - 2), first).map((line) => `  ${line}`),
    ...old.slice(first, old.length - tail).map((line) => `- ${line}`),
    ...now.slice(first, now.length - tail).map((line) => `+ ${line}`),
    ...old.slice(old.length - tail, old.length - tail + 2).map((line) => `  ${line}`),
  ];
  return `@@ ${file}:${first + 1}\n${lines.join("\n")}`;
};

const path = join(values.flake, file);
const before = await readFile(path, "utf8");
const edit = kind === "add" ? await addPackage(before, attr) : await removePackage(before, attr);
console.log(hunk(before, edit));

const started = performance.now();
const verification = await Effect.runPromise(
  verifyEdit({
    flake: values.flake,
    homeConfiguration: values.home,
    file,
    attr,
    kind,
    source: edit.source,
  }).pipe(Effect.provide(runnerLayer())),
);
const seconds = ((performance.now() - started) / 1000).toFixed(1);
console.log(`\nEvaluated in ${seconds}s. ${attr} = ${verification.output}`);
for (const change of verification.changes) {
  for (const out of change.added) console.log(`  + ${change.file}: ${out}`);
  for (const out of change.removed) console.log(`  - ${change.file}: ${out}`);
}
if (!verification.ok) {
  console.log(
    verification.changes.length
      ? "Rejected: the evaluated change is not exactly the requested package."
      : `Rejected: the edit does not change ${values.home}'s home packages. Is ${file} part of that configuration?`,
  );
  process.exit(1);
}
console.log("Verified: only this file changed, by exactly this package.");
if (values.write) {
  if ((await readFile(path, "utf8")) !== before) {
    console.log("Not written: the file changed while it was being verified.");
    process.exit(1);
  }
  await writeFile(`${path}.nix-manager`, edit.source);
  await rename(`${path}.nix-manager`, path);
  console.log(`Wrote ${file}.`);
}
