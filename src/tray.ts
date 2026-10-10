import dbus from "dbus-next";
import { ManagerError, actionLabels, request } from "./domain";
import type { Manager } from "./manager";

const { Interface } = dbus.interface;
const { Variant } = dbus;
type Properties = Record<string, InstanceType<typeof Variant>>;
type Layout = [number, Properties, InstanceType<typeof Variant>[]];

export async function startTray(
  manager: Manager,
  open: () => Promise<void>,
  quit: () => void,
  intervalMinutes: number,
) {
  if (!process.env.DBUS_SESSION_BUS_ADDRESS)
    throw new ManagerError({
      message:
        "The tray needs a desktop D-Bus session. Start it in your graphical session.",
    });
  const bus = dbus.sessionBus();
  bus.on("error", (error) => {
    console.error(`Tray D-Bus error: ${error.message}`);
  });
  const invoke = (fn: () => void | Promise<void>) => {
    void Promise.resolve()
      .then(fn)
      .catch((e) => {
        console.error(e);
        void notify("Nix Manager needs attention", String(e.message || e));
      });
  };
  let lastNotification = "",
    notifiedOperation = "",
    notificationId = 0,
    stopped = false;
  const notify = async (title: string, body: string) => {
    try {
      const proxy = await bus.getProxyObject(
        "org.freedesktop.Notifications",
        "/org/freedesktop/Notifications",
      );
      const iface = proxy.getInterface("org.freedesktop.Notifications");
      iface.removeAllListeners("ActionInvoked");
      iface.on("ActionInvoked", (id: number, action: string) => {
        if (id === notificationId && action === "default") invoke(open);
      });
      notificationId = await iface.Notify(
        "Nix Manager",
        notificationId,
        "system-software-update",
        title,
        body.replace(/[<>]/g, ""),
        ["default", "Open Nix Manager"],
        { "desktop-entry": new Variant("s", "nix-manager") },
        -1,
      );
    } catch (error) {
      console.error(`Desktop notification unavailable: ${error}`);
    }
  };
  const check = async () => {
    if (manager.busy || stopped) return;
    try {
      manager.start(request({ action: "check", host: manager.config.hostKey }));
      await manager.wait();
    } catch (e) {
      if (!(e instanceof ManagerError && /running/.test(e.message))) throw e;
    }
  };
  const actions = [
    { id: 1, label: "Open Nix Manager", run: open },
    { id: 2, label: "Check for updates", run: check },
    {
      id: 3,
      label: "Prepare all updates for review…",
      run: async () => {
        // Nothing is activated from the tray; the dashboard shows the
        // resulting package changes for confirmation.
        manager.start(
          request({
            action: "review-build",
            host: manager.config.hostKey,
            update: true,
          }),
        );
        await open();
      },
    },
    {
      id: 4,
      label: "Quit Nix Manager",
      run: async () => {
        quit();
      },
    },
  ];
  class Menu extends Interface {
    Version = 3;
    TextDirection = "ltr";
    Status = "normal";
    IconThemePath: string[] = [];
    revision = 1;
    constructor() {
      super("com.canonical.dbusmenu");
    }
    properties(id: number, names: string[] = []): Properties {
      const action = actions.find((a) => a.id === id);
      const values: Properties =
        id === 0
          ? { "children-display": new Variant("s", "submenu") }
          : {
              label: new Variant("s", action?.label || ""),
              visible: new Variant("b", !!action),
              enabled: new Variant(
                "b",
                !!action && !([2, 3].includes(id) && manager.busy),
              ),
            };
      return names.length
        ? Object.fromEntries(
            Object.entries(values).filter(([key]) => names.includes(key)),
          )
        : values;
    }
    GetLayout(parent: number, depth: number, names: string[]) {
      const layout: Layout = [
        parent,
        this.properties(parent, names),
        parent === 0 && depth !== 0
          ? actions.map(
              (a) =>
                new Variant("(ia{sv}av)", [
                  a.id,
                  this.properties(a.id, names),
                  [],
                ]),
            )
          : [],
      ];
      return [this.revision, layout];
    }
    GetGroupProperties(ids: number[], names: string[]) {
      return ids
        .filter((id) => id === 0 || actions.some((a) => a.id === id))
        .map((id) => [id, this.properties(id, names)]);
    }
    GetProperty(id: number, name: string) {
      return this.properties(id)[name] || new Variant("s", "");
    }
    Event(id: number, event: string, _data: unknown, _timestamp: number) {
      if (event === "clicked") {
        const action = actions.find((a) => a.id === id);
        if (action) invoke(action.run);
      }
    }
    EventGroup(events: [number, string, unknown, number][]) {
      events.forEach((args) => this.Event(...args));
      return [];
    }
    AboutToShow(_id: number) {
      return false;
    }
    AboutToShowGroup(_ids: number[]) {
      return [[], []];
    }
    LayoutUpdated(revision: number, parent: number) {
      return [revision, parent];
    }
    ItemsPropertiesUpdated(updated: unknown[], removed: unknown[]) {
      return [updated, removed];
    }
  }
  Menu.configureMembers({
    properties: {
      Version: { signature: "u", access: "read" },
      TextDirection: { signature: "s", access: "read" },
      Status: { signature: "s", access: "read" },
      IconThemePath: { signature: "as", access: "read" },
    },
    methods: {
      GetLayout: { inSignature: "iias", outSignature: "u(ia{sv}av)" },
      GetGroupProperties: { inSignature: "aias", outSignature: "a(ia{sv})" },
      GetProperty: { inSignature: "is", outSignature: "v" },
      Event: { inSignature: "isvu", outSignature: "" },
      EventGroup: { inSignature: "a(isvu)", outSignature: "ai" },
      AboutToShow: { inSignature: "i", outSignature: "b" },
      AboutToShowGroup: { inSignature: "ai", outSignature: "aiai" },
    },
    signals: {
      LayoutUpdated: { signature: "ui" },
      ItemsPropertiesUpdated: { signature: "a(ia{sv})a(ias)" },
    },
  });
  class Item extends Interface {
    Category = "SystemServices";
    Id = "nix-manager";
    Title = "Nix Manager";
    Status = "Active";
    WindowId = 0;
    IconName = "system-software-update";
    IconPixmap: unknown[] = [];
    OverlayIconName = "";
    OverlayIconPixmap: unknown[] = [];
    AttentionIconName = "software-update-available";
    AttentionIconPixmap: unknown[] = [];
    AttentionMovieName = "";
    ItemIsMenu = false;
    Menu = "/MenuBar";
    ToolTip: unknown[] = [
      "system-software-update",
      [],
      "Nix Manager",
      "Manage your NixOS and Home Manager configurations",
    ];
    constructor() {
      super("org.kde.StatusNotifierItem");
    }
    Activate(_x: number, _y: number) {
      invoke(open);
    }
    SecondaryActivate(_x: number, _y: number) {
      invoke(check);
    }
    ContextMenu(_x: number, _y: number) {
      /* The host renders Menu via dbusmenu. */
    }
    Scroll(_delta: number, _orientation: string) {}
    NewIcon() {}
    NewAttentionIcon() {}
    NewOverlayIcon() {}
    NewToolTip() {}
    NewTitle() {}
    NewStatus(status: string) {
      return status;
    }
  }
  const stringProps = [
    "Category",
    "Id",
    "Title",
    "Status",
    "IconName",
    "OverlayIconName",
    "AttentionIconName",
    "AttentionMovieName",
  ];
  Item.configureMembers({
    properties: {
      ...Object.fromEntries(
        stringProps.map((name) => [
          name,
          { signature: "s", access: "read" as const },
        ]),
      ),
      WindowId: { signature: "u", access: "read" },
      IconPixmap: { signature: "a(iiay)", access: "read" },
      OverlayIconPixmap: { signature: "a(iiay)", access: "read" },
      AttentionIconPixmap: { signature: "a(iiay)", access: "read" },
      ToolTip: { signature: "(sa(iiay)ss)", access: "read" },
      ItemIsMenu: { signature: "b", access: "read" },
      Menu: { signature: "o", access: "read" },
    },
    methods: {
      Activate: { inSignature: "ii" },
      SecondaryActivate: { inSignature: "ii" },
      ContextMenu: { inSignature: "ii" },
      Scroll: { inSignature: "is" },
    },
    signals: {
      NewIcon: { signature: "" },
      NewAttentionIcon: { signature: "" },
      NewOverlayIcon: { signature: "" },
      NewToolTip: { signature: "" },
      NewTitle: { signature: "" },
      NewStatus: { signature: "s" },
    },
  });
  const menu = new Menu(),
    item = new Item();
  bus.export("/StatusNotifierItem", item);
  bus.export("/MenuBar", menu);
  // One tray per user, even if a desktop file and systemd both launch it.
  const name = "org.kde.StatusNotifierItem.nix_manager";
  const result = await bus.requestName(name, 4);
  if (result !== 1) {
    bus.disconnect();
    throw new ManagerError({
      message: "Nix Manager is already running in the tray.",
    });
  }
  const register = async () => {
    const proxy = await bus.getProxyObject(
      "org.kde.StatusNotifierWatcher",
      "/StatusNotifierWatcher",
    );
    await proxy
      .getInterface("org.kde.StatusNotifierWatcher")
      .RegisterStatusNotifierItem(name);
  };
  try {
    await register();
  } catch (e) {
    bus.disconnect();
    throw new ManagerError({
      message: `No system tray host is available: ${e}`,
    });
  }
  const dbusProxy = await bus.getProxyObject(
    "org.freedesktop.DBus",
    "/org/freedesktop/DBus",
  );
  dbusProxy
    .getInterface("org.freedesktop.DBus")
    .on("NameOwnerChanged", (changed: string, _old: string, owner: string) => {
      if (changed === "org.kde.StatusNotifierWatcher" && owner)
        invoke(register);
    });
  const refresh = () => {
    const report = manager.updates(),
      count = report?.items.length || 0;
    item.Status = count ? "NeedsAttention" : "Active";
    item.ToolTip = [
      item.IconName,
      [],
      "Nix Manager",
      manager.active
        ? manager.active.step
        : report?.errors.length
          ? "Some update checks failed"
          : count
            ? `${count} updates available`
            : "No updates reported",
    ];
    item.NewStatus(item.Status);
    item.NewToolTip();
    menu.LayoutUpdated(++menu.revision, 0);
    if (manager.busy) return;
    const op = manager.history()[0];
    if (op && op.id !== notifiedOperation && !op.request.dryRun) {
      notifiedOperation = op.id;
      if (op.status === "failed")
        invoke(() =>
          notify(
            "Nix Manager needs attention",
            op.error?.slice(0, 300) ||
              "An operation failed. Open the dashboard for its log.",
          ),
        );
      else if (op.request.action === "review-build" && op.status === "succeeded")
        invoke(() =>
          notify(
            "Changes are ready to review",
            "The build finished. Open Nix Manager to see what changes and apply it.",
          ),
        );
      else if (op.request.action !== "check" && op.status === "succeeded")
        invoke(() =>
          notify(
            "Nix Manager finished",
            `${actionLabels[op.request.action]} completed for ${op.request.host || manager.config.hostKey}.`,
          ),
        );
    }
    if (count && report) {
      const signature = JSON.stringify(report.items);
      if (signature !== lastNotification) {
        lastNotification = signature;
        invoke(() =>
          notify(
            `${count} updates available`,
            report.items.map((i) => i.name).join(", "),
          ),
        );
      }
    } else if (report && !report.errors.length) lastNotification = "";
  };
  notifiedOperation = manager.history()[0]?.id || "";
  const unsubscribe = manager.onChange(refresh);
  refresh();
  const first = setTimeout(() => invoke(check), 10_000);
  const timer = setInterval(() => invoke(check), intervalMinutes * 60_000);
  return {
    close() {
      stopped = true;
      clearTimeout(first);
      clearInterval(timer);
      unsubscribe();
      bus.disconnect();
    },
  };
}
