import { afterAll, expect, test } from "bun:test";
import { mkdirSync, mkdtempSync, rmSync, writeFileSync } from "node:fs";
import { tmpdir } from "node:os";
import { dirname, join } from "node:path";
import { hardwareSuggestions } from "../src/computer";
import { experiences } from "../src/curated";
import { detectHardware } from "../src/hardware";

const root = mkdtempSync(join(tmpdir(), "nm-sysfs-"));
afterAll(() => rmSync(root, { recursive: true, force: true }));
const write = (path: string, content: string) => {
  mkdirSync(dirname(join(root, path)), { recursive: true });
  writeFileSync(join(root, path), `${content}\n`);
};

test("nothing is found without sysfs", () => {
  expect(detectHardware(join(root, "missing"))).toEqual({ laptop: false, nvidia: false, bluetooth: false, printer: false, fingerprint: false });
});

test("hardware is read from sysfs", () => {
  // A wireless mouse's battery does not make a laptop; an NVIDIA audio function is no graphics card.
  write("class/power_supply/hidpp_battery_0/type", "Battery");
  write("class/power_supply/hidpp_battery_0/scope", "Device");
  write("bus/pci/devices/0000:01:00.1/vendor", "0x10de");
  write("bus/pci/devices/0000:01:00.1/class", "0x040300");
  write("bus/usb/devices/1-1/idVendor", "046d");
  write("bus/usb/devices/1-1/product", "USB Receiver");
  expect(detectHardware(root)).toEqual({ laptop: false, nvidia: false, bluetooth: false, printer: false, fingerprint: false });

  write("class/power_supply/BAT0/type", "Battery");
  write("bus/pci/devices/0000:01:00.0/vendor", "0x10de");
  write("bus/pci/devices/0000:01:00.0/class", "0x030000");
  mkdirSync(join(root, "class/bluetooth/hci0"), { recursive: true });
  write("bus/usb/devices/1-2:1.0/bInterfaceClass", "07");
  write("bus/usb/devices/1-3/idVendor", "27c6");
  expect(detectHardware(root)).toEqual({ laptop: true, nvidia: true, bluetooth: true, printer: true, fingerprint: true });
});

test("every suggestion names a system experience", () => {
  for (const suggestion of hardwareSuggestions)
    expect(experiences.find((item) => item.id === suggestion.experience)?.scope).toBe("system");
});
