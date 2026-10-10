import { expect, test } from "bun:test";
import { rmSync, writeFileSync } from "node:fs";
import { join } from "node:path";
import dbus from "dbus-next";
import { Effect } from "effect";
import { fixture } from "./helpers";
import { Manager } from "../src/manager";
import { startTray } from "../src/tray";

test("tray registers StatusNotifierItem and exposes a working D-Bus menu on a private session", async () => {
  const config = fixture();
  const busConfig = join(config.flake, "session.conf");
  // A private config avoids host /etc and service activation inside Nix's sandbox.
  writeFileSync(
    busConfig,
    `<busconfig><type>session</type><listen>unix:tmpdir=${config.flake}</listen><auth>EXTERNAL</auth><policy context="default"><allow own="*"/><allow send_destination="*"/><allow receive_sender="*"/></policy></busconfig>`,
  );
  const daemon = Bun.spawnSync([
    "dbus-daemon",
    `--config-file=${busConfig}`,
    "--fork",
    "--print-address=1",
    "--print-pid=1",
  ]);
  if (daemon.exitCode !== 0) {
    rmSync(config.flake, { recursive: true, force: true });
    throw new Error(daemon.stderr.toString());
  }
  const [address, pid] = daemon.stdout.toString().trim().split("\n");
  const previous = process.env.DBUS_SESSION_BUS_ADDRESS;
  process.env.DBUS_SESSION_BUS_ADDRESS = address;
  const bus = dbus.sessionBus();
  bus.on("error", () => {});
  const manager = new Manager(config, undefined, () => Effect.never);
  const { Interface } = dbus.interface;
  let registered = "",
    opens = 0,
    quits = 0;
  class Watcher extends Interface {
    constructor() {
      super("org.kde.StatusNotifierWatcher");
    }
    RegisterStatusNotifierItem(name: string) {
      registered = name;
    }
  }
  Watcher.configureMembers({
    methods: { RegisterStatusNotifierItem: { inSignature: "s" } },
  });
  let tray: Awaited<ReturnType<typeof startTray>> | undefined;
  try {
    await bus.requestName("org.kde.StatusNotifierWatcher", 4);
    bus.export("/StatusNotifierWatcher", new Watcher());
    tray = await startTray(
      manager,
      async () => {
        opens++;
      },
      () => {
        quits++;
      },
      360,
    );
    expect(registered).toBe("org.kde.StatusNotifierItem.nix_manager");
    const proxy = await bus.getProxyObject(registered, "/StatusNotifierItem");
    const item = proxy.getInterface("org.kde.StatusNotifierItem");
    const properties = proxy.getInterface("org.freedesktop.DBus.Properties");
    expect(
      (await properties.Get("org.kde.StatusNotifierItem", "Menu")).value,
    ).toBe("/MenuBar");
    await item.Activate(0, 0);
    await Bun.sleep(5);
    expect(opens).toBe(1);
    const menu = (
      await bus.getProxyObject(registered, "/MenuBar")
    ).getInterface("com.canonical.dbusmenu");
    const [revision, layout] = await menu.GetLayout(0, -1, []);
    expect(revision).toBeGreaterThan(0);
    expect(layout[2]).toHaveLength(4);
    expect(layout[2][2].value[1].label.value).toBe("Prepare all updates for review…");
    await menu.Event(3, "clicked", new dbus.Variant("s", ""), 0);
    await Bun.sleep(10);
    expect(opens).toBe(2);
    expect(manager.active?.request).toMatchObject({
      action: "review-build",
      update: true,
    });
    const busyLayout = await menu.GetLayout(0, -1, []);
    expect(busyLayout[1][2][2].value[1].enabled.value).toBe(false);
    await menu.Event(4, "clicked", new dbus.Variant("s", ""), 0);
    await Bun.sleep(5);
    expect(quits).toBe(1);
  } finally {
    tray?.close();
    await manager.close();
    bus.disconnect();
    process.kill(Number(pid), "SIGTERM");
    if (previous === undefined) delete process.env.DBUS_SESSION_BUS_ADDRESS;
    else process.env.DBUS_SESSION_BUS_ADDRESS = previous;
    rmSync(config.flake, { recursive: true, force: true });
  }
}, 10_000);
