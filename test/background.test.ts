import { afterEach, expect, test } from "bun:test";
import { mkdirSync, mkdtempSync, readFileSync, rmSync, symlinkSync, writeFileSync } from "node:fs";
import { tmpdir } from "node:os";
import { dirname, join } from "node:path";
import { Effect } from "effect";
import { request, type Command } from "../src/domain";
import { Manager } from "../src/manager";
import { lastPrepared, prepareInBackground } from "../src/background";
import { onMains } from "../src/hardware";
import { writePreferences } from "../src/preferences";
import { getReview, listReviews } from "../src/reviews";
import { hashText } from "../src/observe";
import { fixture } from "./helpers";

const cleanups: (() => void)[] = [];
afterEach(() => {
  cleanups
    .splice(0)
    .reverse()
    .forEach((f) => f());
});
const setup = () => {
  const config = fixture();
  cleanups.push(() => rmSync(config.flake, { recursive: true, force: true }));
  return config;
};

const oldLock = '{"nodes":{"root":{},"nixpkgs":{"locked":{"rev":"a","lastModified":1}}}}';
const newLock =
  '{"nodes":{"root":{},"nixpkgs":{"locked":{"rev":"b","lastModified":2,"type":"github","owner":"NixOS","repo":"nixpkgs"}}}}';

/** A manager whose builds write `newLock` and link the given outputs. */
const fakeManager = (config: ReturnType<typeof setup>) => {
  writeFileSync(join(config.flake, "flake.lock"), oldLock);
  const outputs = { home: join(config.flake, "home-output"), system: join(config.flake, "system-output") };
  mkdirSync(outputs.home);
  mkdirSync(outputs.system);
  const calls: Command[] = [];
  const manager = new Manager(config, undefined, (command) =>
    Effect.sync(() => {
      calls.push(command);
      if (command.argv.includes("--output-lock-file"))
        writeFileSync(command.argv[command.argv.indexOf("--output-lock-file") + 1], newLock);
      if (command.argv.includes("--out-link"))
        symlinkSync(
          command.label.includes("home") ? outputs.home : outputs.system,
          command.argv[command.argv.indexOf("--out-link") + 1],
        );
      return { code: 0, stdout: "", stderr: "" };
    }),
  );
  return { manager, outputs, calls };
};

const report = (config: ReturnType<typeof setup>, items: { name: string; latest: string; kind: "input" | "package" }[]) =>
  writeFileSync(
    join(config.stateDir, "updates.json"),
    JSON.stringify({
      flake: config.flake,
      report: { checkedAt: new Date().toISOString(), errors: [], items: items.map((item) => ({ current: "old", ...item })) },
    }),
  );

const always = { mains: () => true, idle: () => true };

test("updates are prepared in the background only when allowed, new, plugged in and idle", async () => {
  const config = setup();
  const { manager } = fakeManager(config);
  report(config, [{ name: "nixpkgs", latest: "b", kind: "input" }]);
  expect(prepareInBackground(manager, always)).toBe("Preparing updates in the background is off.");

  writePreferences(config, { prepareUpdates: true });
  expect(prepareInBackground(manager, { mains: () => false, idle: () => true })).toBe("The computer runs on battery.");
  expect(prepareInBackground(manager, { mains: () => true, idle: () => false })).toBe("The computer is busy.");

  // Package pins change files in the checkout, so they are not prepared unasked.
  report(config, [{ name: "firefox", latest: "131", kind: "package" }]);
  expect(prepareInBackground(manager, always)).toBe("There are no updates to prepare.");

  report(config, [
    { name: "nixpkgs", latest: "b", kind: "input" },
    { name: "firefox", latest: "131", kind: "package" },
  ]);
  const operation = prepareInBackground(manager, always);
  if (typeof operation === "string") throw new Error(operation);
  expect(operation.request).toMatchObject({ action: "review-build", host: "test", update: true });
  expect(prepareInBackground(manager, always)).toBe("These updates were prepared already.");
  expect((await manager.wait())?.status).toBe("succeeded");
  expect(lastPrepared(config)?.reviewId).toBe(operation.request.reviewId!);
  expect(await getReview(config, operation.request.reviewId!)).toBeDefined();

  // A newer release is prepared again.
  report(config, [{ name: "nixpkgs", latest: "c", kind: "input" }]);
  manager.start(request({ action: "check" }));
  expect(prepareInBackground(manager, always)).toBe("Something else is running.");
  await manager.wait();
});

test("a boot apply keeps the home part, which is applied once the new system runs", async () => {
  const config = setup();
  const { manager, outputs, calls } = fakeManager(config);
  manager.start(request({ action: "review-build", reviewId: "aaaaaaaa", inputs: ["nixpkgs"] }));
  expect((await manager.wait())?.status).toBe("succeeded");

  manager.start(request({ action: "review-apply", reviewId: "aaaaaaaa", activation: "boot" }));
  expect((await manager.wait())?.status).toBe("succeeded");
  expect(readFileSync(join(config.flake, "flake.lock"), "utf8")).toBe(newLock);
  const kept = (await getReview(config, "aaaaaaaa"))!;
  expect(kept).toMatchObject({ scope: "home", afterBoot: { system: outputs.system }, home: { path: outputs.home } });
  expect(kept.system).toBeUndefined();
  expect(kept.inputs?.baseLockHash).toBe(hashText(newLock));

  // Another system is running, so nothing happens yet.
  const booted = join(config.flake, "run/booted-system");
  mkdirSync(dirname(booted));
  symlinkSync(outputs.home, booted);
  expect(await manager.resumeAfterBoot(booted)).toBeUndefined();

  rmSync(booted);
  symlinkSync(outputs.system, booted);
  calls.length = 0;
  const operation = await manager.resumeAfterBoot(booted);
  expect(operation?.request).toMatchObject({ action: "review-apply", scope: "home", reviewId: "aaaaaaaa", activation: "switch" });
  expect((await getReview(config, "aaaaaaaa"))?.afterBoot).toBeUndefined();
  expect((await manager.wait())?.status).toBe("succeeded");
  expect(calls.some((command) => command.argv.includes(outputs.home))).toBe(true);
  expect(calls.some((command) => command.argv.includes(outputs.system))).toBe(false);
  expect(await listReviews(config)).toEqual([]);
  expect(await manager.resumeAfterBoot(booted)).toBeUndefined();
});

test("only a system battery without mains power counts as battery", () => {
  const root = mkdtempSync(join(tmpdir(), "nm-power-"));
  cleanups.push(() => rmSync(root, { recursive: true, force: true }));
  const write = (path: string, content: string) => {
    mkdirSync(dirname(join(root, path)), { recursive: true });
    writeFileSync(join(root, path), `${content}\n`);
  };
  expect(onMains(join(root, "missing"))).toBe(true);
  write("class/power_supply/hidpp_battery_0/type", "Battery");
  write("class/power_supply/hidpp_battery_0/scope", "Device");
  expect(onMains(root)).toBe(true);
  write("class/power_supply/BAT0/type", "Battery");
  write("class/power_supply/AC/type", "Mains");
  write("class/power_supply/AC/online", "0");
  expect(onMains(root)).toBe(false);
  write("class/power_supply/AC/online", "1");
  expect(onMains(root)).toBe(true);
});
