import { afterEach, expect, test } from "bun:test";
import { mkdirSync, readFileSync, rmSync, writeFileSync } from "node:fs";
import { join } from "node:path";
import { Effect, Layer } from "effect";
import { ManagerError, request, type Command } from "../src/domain";
import { Runner } from "../src/process";
import {
  Network,
  updatePackage,
  extractHash,
  compareVersions,
  checkUpdates,
  atomicPin,
  type Package,
} from "../src/updates";
import { fixture } from "./helpers";
const dirs: string[] = [];
afterEach(() =>
  dirs.splice(0).forEach((d) => rmSync(d, { recursive: true, force: true })),
);
const hash = "sha256-" + "B".repeat(43) + "=",
  rev = "a".repeat(40);

function harness(
  name: Package,
  options: {
    fail?: string;
    current?: string;
    locked?: string;
    pinRev?: string;
    force?: boolean;
    dryRun?: boolean;
    concurrentEdit?: boolean;
    channel?: string;
  } = {},
) {
  const config = fixture();
  dirs.push(config.flake);
  const dir = join(config.flake, "pkgs", name);
  mkdirSync(dir, { recursive: true });
  const pinFile = join(dir, "source.json");
  writeFileSync(
    pinFile,
    JSON.stringify({
      version: options.current || "0.0.34",
      ...(options.channel ? { channel: options.channel } : {}),
      nixpkgsRev: options.pinRev || rev,
    }),
  );
  writeFileSync(join(dir, "package.nix"), "{}");
  const before = readFileSync(pinFile, "utf8"),
    calls: Command[] = [];
  const run = (c: Command) =>
    Effect.gen(function* () {
      calls.push(c);
      let stdout = "",
        stderr = "",
        code = 0;
      const args = c.argv.join(" ");
      if (args.includes("nixpkgs.outPath")) stdout = config.flake;
      else if (args.includes("nixpkgs.rev")) stdout = rev;
      else if (args.includes("pkgs.${builtins.getEnv"))
        stdout = options.locked || "0.0.34";
      else if (c.argv[0] === "diff") stdout = "recipe comparison";
      else if (c.argv[0] === "nix-prefetch-url") stdout = "rawhash";
      else if (c.argv[1] === "hash") stdout = hash;
      else if (args.endsWith(".drvPath")) stdout = "/nix/store/expected.drv";
      else if (c.argv[1] === "build" && c.allowFailure) {
        if (options.fail === "hash")
          return yield* Effect.fail(
            new ManagerError({ message: "Hash discovery failed" }),
          );
        stderr = `hash mismatch in fixed-output derivation '/nix/store/expected.drv':\n  got: ${hash}`;
        code = 1;
      } else if (c.argv[1] === "build") {
        if (options.fail === "build")
          return yield* Effect.fail(
            new ManagerError({ message: "Build failed" }),
          );
        if (options.concurrentEdit) writeFileSync(pinFile, "a concurrent edit");
      } else throw new Error(`Unhandled fixture command: ${args}`);
      return { stdout, stderr, code };
    });
  const network = Layer.succeed(Network, {
    text: (url: string) =>
      options.fail === "network"
        ? Effect.fail(new ManagerError({ message: "Network failed" }))
        : Effect.succeed(
            url.includes("releases/latest")
              ? '{"tag_name":"v0.0.38"}'
              : url.includes("releases?per_page")
                ? JSON.stringify([
                    { tag_name: "v0.0.39-nightly.20261013.12" },
                    { tag_name: "v0.0.39-nightly.20261013.9" },
                    { tag_name: "v0.0.40-nightly.20261014.1", draft: true },
                    { tag_name: "v0.0.39-preview.1" },
                    { tag_name: "v0.0.38" },
                  ])
                : url.includes("commits/nixos-unstable")
                ? JSON.stringify({ sha: rev })
                : 'version = "0.0.38";\n',
          ),
  });
  const effect = updatePackage(
    config,
    request({
      action: `${name}-update`,
      host: "test",
      dryRun: !!options.dryRun,
      force: !!options.force,
    }),
    () => {},
  ).pipe(Effect.provide(Layer.merge(Layer.succeed(Runner, { run }), network)));
  return {
    config,
    before,
    pinFile,
    calls,
    run: () => Effect.runPromise(effect),
  };
}
test("semantic versions compare numerically and reject unstable formats", () => {
  expect(compareVersions("1.10.0", "1.9.99")).toBe(1);
  expect(compareVersions("1.0.0", "1.0.0")).toBe(0);
  expect(() => compareVersions("1.0.0-beta", "1.0.0")).toThrow();
});
test("nightlies order by date and run, before their stable release", () => {
  const ordered = [
    "0.0.45",
    "0.0.46-nightly.20261012.99",
    "0.0.46-nightly.20261013.9",
    "0.0.46-nightly.20261013.10",
    "0.0.46",
    "0.0.47-nightly.20261001.1",
  ];
  for (let i = 1; i < ordered.length; i++) {
    expect(compareVersions(ordered[i], ordered[i - 1])).toBe(1);
    expect(compareVersions(ordered[i - 1], ordered[i])).toBe(-1);
  }
  expect(() => compareVersions("0.0.46-preview.1", "0.0.46")).toThrow();
});
test("t3code: nightly channel follows the newest published nightly", async () => {
  const h = harness("t3code", {
    current: "0.0.38-nightly.20261001.1",
    channel: "nightly",
  });
  await h.run();
  const pin = JSON.parse(readFileSync(h.pinFile, "utf8"));
  expect(pin.version).toBe("0.0.39-nightly.20261013.12");
  expect(pin.channel).toBe("nightly");
  const stable = harness("t3code", { current: "0.0.38-nightly.20261001.1" });
  await stable.run();
  expect(JSON.parse(readFileSync(stable.pinFile, "utf8")).version).toBe(
    "0.0.38",
  );
  const caughtUp = harness("t3code", {
    current: "0.0.39-nightly.20261013.12",
    channel: "nightly",
    locked: "0.0.39",
  });
  await caughtUp.run();
  expect(caughtUp.calls.some((c) => c.argv[1] === "build")).toBe(false);
});
test("hash discovery matches the exact derivation", () => {
  const log = `hash mismatch in fixed-output derivation '/nix/store/other.drv':\n got: sha256-${"A".repeat(43)}=\nhash mismatch in fixed-output derivation '/nix/store/expected.drv':\n got: ${hash}`;
  expect(extractHash(log, "/nix/store/expected.drv", 1)).toBe(hash);
  for (const output of [
    "network failed",
    `got: ${hash}`,
    `hash mismatch in fixed-output derivation '/nix/store/other.drv':\n got: ${hash}`,
    "hash mismatch in fixed-output derivation '/nix/store/expected.drv':\n got: invalid",
  ])
    expect(() => extractHash(output, "/nix/store/expected.drv", 1)).toThrow();
  expect(() => extractHash(log, "/nix/store/expected.drv", 0)).toThrow();
});
for (const name of ["t3code", "opencode", "pince"] as const) {
  test(`${name}: dry run preserves pins and never fetches archives or builds`, async () => {
    const h = harness(name, { dryRun: true });
    await h.run();
    expect(readFileSync(h.pinFile, "utf8")).toBe(h.before);
    expect(
      h.calls.some(
        (c) => c.argv[0] === "nix-prefetch-url" || c.argv[1] === "build",
      ),
    ).toBe(false);
  });
  for (const fail of [
    "network",
    "build",
    ...(name === "pince" ? [] : ["hash"]),
  ])
    test(`${name}: ${fail} failure leaves pin intact`, async () => {
      const h = harness(name, { fail });
      await expect(h.run()).rejects.toThrow();
      expect(readFileSync(h.pinFile, "utf8")).toBe(h.before);
    });
  test(`${name}: validated build precedes atomic pin replacement`, async () => {
    const h = harness(name);
    await h.run();
    const pin = JSON.parse(readFileSync(h.pinFile, "utf8"));
    expect(pin.version).toBe("0.0.38");
    expect(h.calls.at(-1)?.argv[1]).toBe("build");
    expect(
      pin[
        name === "t3code"
          ? "cargoHash"
          : name === "opencode"
            ? "nodeHash"
            : "hash"
      ],
    ).toBe(hash);
    if (name !== "pince") expect(pin.nixpkgsRev).toBe(rev);
  });
  test(`${name}: current pins skip unless forced`, async () => {
    for (const force of [false, true]) {
      const h = harness(name, { current: "0.0.38", force });
      await h.run();
      expect(h.calls.some((c) => c.argv[1] === "build")).toBe(force);
    }
  });
  test(`${name}: forced downgrades and concurrent pin edits are rejected`, async () => {
    const h = harness(name, { current: "0.0.39", force: true });
    await expect(h.run()).rejects.toThrow("downgrade");
    expect(readFileSync(h.pinFile, "utf8")).toBe(h.before);
    const edited = harness(name, { concurrentEdit: true });
    await expect(edited.run()).rejects.toThrow("Pin changed");
    expect(readFileSync(edited.pinFile, "utf8")).toBe("a concurrent edit");
  });
}
for (const name of ["t3code", "opencode"] as const)
  test(`${name}: changed nixpkgs revision rebuilds; caught-up nixpkgs skips`, async () => {
    const changed = harness(name, {
      current: "0.0.38",
      pinRev: "b".repeat(40),
    });
    await changed.run();
    expect(changed.calls.some((c) => c.argv[1] === "build")).toBe(true);
    for (const locked of ["0.0.38", "0.0.39"]) {
      const h = harness(name, { locked });
      await h.run();
      expect(h.calls.some((c) => c.argv[1] === "build")).toBe(false);
    }
  });
test("update checks write an alternate lock and report partial failures", async () => {
  const config = fixture();
  dirs.push(config.flake);
  const lock = JSON.stringify({
    nodes: { nixpkgs: { locked: { rev: "old" } } },
  });
  writeFileSync(join(config.flake, "flake.lock"), lock);
  for (const name of ["t3code", "opencode", "pince"]) {
    mkdirSync(join(config.flake, "pkgs", name), { recursive: true });
    writeFileSync(
      join(config.flake, "pkgs", name, "source.json"),
      '{"version":"1.0.0"}',
    );
  }
  const layer = Layer.merge(
    Layer.succeed(Runner, {
      run: (c) =>
        Effect.sync(() => {
          expect(c.argv).toContain("--output-lock-file");
          writeFileSync(
            c.argv.at(-1)!,
            JSON.stringify({ nodes: { nixpkgs: { locked: { rev: "new" } } } }),
          );
          return { code: 0, stdout: "", stderr: "" };
        }),
    }),
    Layer.succeed(Network, {
      text: (url) =>
        url.includes("anomalyco")
          ? Effect.fail(new ManagerError({ message: "API unavailable" }))
          : Effect.succeed('{"tag_name":"v1.1.0"}'),
    }),
  );
  const report = await Effect.runPromise(
    checkUpdates(config).pipe(Effect.provide(layer)),
  );
  expect(report.items.map((i) => i.name)).toEqual([
    "nixpkgs",
    "t3code",
    "pince",
  ]);
  expect(report.errors).toHaveLength(1);
  expect(readFileSync(join(config.flake, "flake.lock"), "utf8")).toBe(lock);
});
