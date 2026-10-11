import { expect, test } from "bun:test";
import type { BuildReview, EditedPackage, PackageChange, ReviewOutput } from "../src/contracts";
import type { Operation } from "../src/contracts";
import { request } from "../src/domain";
import { reviewSummary, timeLeft, usualSeconds } from "../src/summary";

const MiB = 1024 * 1024;
const review = (fields: Partial<BuildReview>): BuildReview => ({
  id: "abababab",
  host: "desktop",
  scope: "home",
  createdAt: "2026-10-11T20:00:00Z",
  fingerprint: "",
  source: "checkout",
  ...fields,
});
const output = (changes: PackageChange["kind"][], size?: [number, number], fields: Partial<ReviewOutput> = {}): ReviewOutput => ({
  path: "/nix/store/out",
  compared: true,
  changes: changes.map((kind, index) => ({ name: `p${index}`, before: [], after: [], sizeDelta: 0, kind })),
  closureSize: size?.[1],
  currentClosureSize: size?.[0],
  ...fields,
});
const pkg = (name: string): EditedPackage => ({ out: `/nix/store/${name}`, name, file: "modules/apps.nix" });
const edits = (added: string[], removed: string[] = [], options = 0): BuildReview["edits"] => ({
  changes: [],
  hash: "",
  files: [],
  effects: [
    {
      scope: "home",
      added: added.map(pkg),
      removed: removed.map(pkg),
      options: Array.from({ length: options }, (_, index) => ({ option: `o${index}`, after: true })),
    },
  ],
});

test("a review of edits names what is installed and removed", () => {
  expect(reviewSummary(review({ edits: edits(["cowsay"]), home: output(["added"], [100 * MiB, 103 * MiB]) }))).toBe(
    "Installs cowsay. It needs about 3.0 MiB more disk space.",
  );
  expect(reviewSummary(review({ edits: edits(["a", "b", "c", "d"], ["e"], 2) }))).toBe(
    "Installs a, b and 2 more, removes e and changes 2 settings.",
  );
  // A package rebuilt by a setting is neither installed nor removed.
  expect(reviewSummary(review({ edits: edits(["git"], ["git"]) }))).toBe("Nothing you use changes.");
});

test("an update counts packages, sources, space and a new kernel", () => {
  const system = output(["changed", "changed", "added", "removed"], [2048 * MiB, 1024 * MiB], { kernel: { before: "6.1", after: "6.6" } });
  const inputs = { names: ["nixpkgs"], baseLockHash: "", changes: [{ name: "nixpkgs" }] } as BuildReview["inputs"];
  expect(reviewSummary(review({ scope: "all", source: "inputs", inputs, home: output(["changed"]), system, downloaded: 300 * MiB }))).toBe(
    "Updates 1 software source. Updates 3 packages, adds 1 and removes 1. It frees about 1.0 GiB of disk space. 300 MiB were downloaded to prepare it. Restart afterwards to use the new kernel.",
  );
  expect(reviewSummary(review({ home: output(["rebuilt"], [MiB, MiB]) }))).toBe("Nothing you use changes.");
  expect(reviewSummary(review({ home: output([], undefined, { compared: false }) }))).toBe(
    "Builds everything new, as nothing is active to compare with.",
  );
});

test("time estimates use the median of earlier runs of the same kind", () => {
  const run = (minutes: number, day: number, fields: Partial<Operation> = {}, update = true): Operation =>
    ({
      id: `${day}`,
      request: request({ action: "review-build", update }),
      status: "succeeded",
      startedAt: `2026-10-${String(day).padStart(2, "0")}T10:00:00Z`,
      finishedAt: new Date(Date.parse(`2026-10-${String(day).padStart(2, "0")}T10:00:00Z`) + minutes * 60_000).toISOString(),
      ...fields,
    }) as Operation;
  const asked = request({ action: "review-build", update: true });
  expect(usualSeconds([run(4, 1)], asked)).toBeUndefined();
  // The oldest of six runs is ignored, and so are failures and other kinds.
  const runs = [run(60, 1), run(2, 2), run(3, 3), run(10, 4), run(4, 5), run(5, 6), run(1, 7, { status: "failed" }), run(1, 8, {}, false)];
  expect(usualSeconds(runs, asked)).toBe(4 * 60);
  expect(timeLeft(undefined, 10)).toBeUndefined();
  expect(timeLeft(240, 60)).toBe("About 3 minutes left");
  expect(timeLeft(240, 230)).toBe("Less than a minute left");
  expect(timeLeft(240, 400)).toBe("Taking longer than usual");
  expect(timeLeft(5400, 0)).toBe("About 1.5 hours left");
});
