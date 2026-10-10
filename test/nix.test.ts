import { expect, test } from "bun:test";
import { Effect } from "effect";
import { lockChanges, NixProgress, parseClosureDiff, storeName } from "../src/nix";
import { runProcess, type Output } from "../src/process";

const nix = (event: Record<string, unknown>) => `@nix ${JSON.stringify(event)}`;

test("structured Nix logs become readable lines and aggregated progress", () => {
  const progress = new NixProgress();
  expect(progress.snapshot().evaluating).toBe(true);
  expect(progress.line("plain output")).toBe("plain output");
  expect(
    progress.line(nix({ action: "msg", level: 3, msg: "\u001b[1mthis derivation will be built:\u001b[0m" })),
  ).toBe("this derivation will be built:");
  // Verbose messages and pure progress events produce no log text.
  expect(progress.line(nix({ action: "msg", level: 4, msg: "evaluating file" }))).toBeUndefined();
  expect(
    progress.line(nix({ action: "start", id: 1, level: 0, parent: 0, text: "", type: 104 })),
  ).toBeUndefined();
  progress.line(nix({ action: "result", id: 1, type: 105, fields: [1, 3, 1, 0] }));
  expect(
    progress.line(
      nix({
        action: "start",
        id: 2,
        level: 3,
        parent: 0,
        type: 105,
        text: "building '/nix/store/6mzmgw0jjdrr5bwzhk96ff3jgg56mdx5-hello-2.12.drv'",
        fields: ["/nix/store/6mzmgw0jjdrr5bwzhk96ff3jgg56mdx5-hello-2.12.drv", "", 1, 1],
      }),
    ),
  ).toBe("building '/nix/store/6mzmgw0jjdrr5bwzhk96ff3jgg56mdx5-hello-2.12.drv'");
  progress.line(nix({ action: "result", id: 2, type: 104, fields: ["buildPhase"] }));
  expect(progress.line(nix({ action: "result", id: 2, type: 101, fields: ["compiling"] }))).toBe(
    "hello-2.12> compiling",
  );
  // Download sizes come from SetExpected on the realisation activity.
  progress.line(nix({ action: "start", id: 3, level: 0, parent: 0, text: "", type: 102 }));
  progress.line(nix({ action: "result", id: 3, type: 106, fields: [101, 4096] }));
  const snapshot = progress.snapshot();
  expect(snapshot.evaluating).toBe(false);
  expect(snapshot.builds).toEqual({ done: 1, expected: 3, running: 1, failed: 0 });
  expect(snapshot.bytes.expected).toBe(4096);
  expect(snapshot.current).toEqual(["hello-2.12 (buildPhase)"]);
  progress.line(nix({ action: "stop", id: 2 }));
  progress.line(nix({ action: "result", id: 1, type: 105, fields: [2, 3, 0, 0] }));
  expect(progress.snapshot().builds.done).toBe(2);
  expect(progress.snapshot().current).toEqual([]);
  expect(progress.line("@nix {not json")).toBe("@nix {not json");
});

test("runner converts structured stderr and reports progress for Nix commands", async () => {
  const events: Output[] = [];
  const script = [
    nix({ action: "msg", level: 0, msg: "error: attribute missing" }),
    nix({ action: "start", id: 7, level: 0, parent: 0, text: "", type: 104 }),
    nix({ action: "result", id: 7, type: 105, fields: [0, 2, 0, 0] }),
  ]
    .map((line) => `console.error(${JSON.stringify(line)});`)
    .join("");
  const result = await Effect.runPromise(
    runProcess(
      { label: "fixture", argv: [process.execPath, "-e", script], progress: true },
      (event) => events.push(event),
    ),
  );
  expect(result.stderr).toBe("error: attribute missing\n");
  expect(events.filter((e) => e.stream === "stderr").map((e) => (e as { text: string }).text).join("")).toBe(
    "error: attribute missing\n",
  );
  const last = events.filter((e) => e.stream === "progress").at(-1);
  expect(last?.stream === "progress" && last.progress.builds.expected).toBe(2);
});

test("closure differences are parsed into package changes", () => {
  expect(
    parseClosureDiff(
      [
        "firefox: 130.0 → 131.0.2, +1024.0 KiB",
        "\u001b[1mlinux\u001b[0m: 6.9.1, 6.9.1-modules → 6.10.0, 6.10.0-modules, \u001b[31;1m+2048.5 KiB\u001b[0m",
        "htop: ∅ → 3.3.0, +120.0 KiB",
        "nano: 8.0 → ∅, -900.0 KiB",
        "glibc: ε → 2.40",
        "mesa: +16.0 KiB",
        "7zz: 26.02, 26.02-fish → ∅, -6.0 MiB",
        "cef-binary: 6533 → 6534, +1.5 GiB",
        "",
      ].join("\n"),
    ),
  ).toEqual([
    { name: "firefox", before: ["130.0"], after: ["131.0.2"], sizeDelta: 1024 * 1024, kind: "changed" },
    {
      name: "linux",
      before: ["6.9.1", "6.9.1-modules"],
      after: ["6.10.0", "6.10.0-modules"],
      sizeDelta: Math.round(2048.5 * 1024),
      kind: "changed",
    },
    { name: "htop", before: [], after: ["3.3.0"], sizeDelta: 120 * 1024, kind: "added" },
    { name: "nano", before: ["8.0"], after: [], sizeDelta: -900 * 1024, kind: "removed" },
    { name: "glibc", before: [""], after: ["2.40"], sizeDelta: 0, kind: "changed" },
    { name: "mesa", before: [], after: [], sizeDelta: 16 * 1024, kind: "rebuilt" },
    { name: "7zz", before: ["26.02", "26.02-fish"], after: [], sizeDelta: -6 * 1024 ** 2, kind: "removed" },
    { name: "cef-binary", before: ["6533"], after: ["6534"], sizeDelta: 1.5 * 1024 ** 3, kind: "changed" },
  ]);
});

test("lock changes include transitive nodes with dates and sources", () => {
  const before = JSON.stringify({
    nodes: {
      root: { inputs: { nixpkgs: "nixpkgs" } },
      nixpkgs: { locked: { type: "github", owner: "NixOS", repo: "nixpkgs", rev: "a", lastModified: 0 } },
      same: { locked: { type: "path", path: "/x", narHash: "sha256-x" } },
    },
  });
  const after = JSON.stringify({
    nodes: {
      root: {},
      nixpkgs: { locked: { type: "github", owner: "NixOS", repo: "nixpkgs", rev: "b", lastModified: 86400 } },
      same: { locked: { type: "path", path: "/x", narHash: "sha256-x" } },
      added: { locked: { type: "git", url: "git+https://example.org/repo?ref=main", rev: "c" } },
    },
  });
  expect(lockChanges(before, after)).toEqual([
    {
      name: "added",
      before: undefined,
      after: { rev: "c", narHash: undefined, lastModified: undefined, source: "https://example.org/repo", url: undefined },
    },
    {
      name: "nixpkgs",
      before: {
        rev: "a",
        narHash: undefined,
        lastModified: "1970-01-01T00:00:00.000Z",
        source: "NixOS/nixpkgs",
        url: "https://github.com/NixOS/nixpkgs/commit/a",
      },
      after: {
        rev: "b",
        narHash: undefined,
        lastModified: "1970-01-02T00:00:00.000Z",
        source: "NixOS/nixpkgs",
        url: "https://github.com/NixOS/nixpkgs/commit/b",
      },
    },
  ]);
  expect(storeName("/nix/store/6mzmgw0jjdrr5bwzhk96ff3jgg56mdx5-hello-2.12.drv")).toBe("hello-2.12");
});
