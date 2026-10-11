import { readdirSync, readFileSync } from "node:fs";
import { join } from "node:path";

/** Hardware of the computer Nix Manager runs on that a feature can make use of. */
export interface Hardware {
  /** Runs on a battery. */
  laptop: boolean;
  /** Has an NVIDIA graphics card. */
  nvidia: boolean;
  bluetooth: boolean;
  /** A printer is connected by USB. */
  printer: boolean;
  fingerprint: boolean;
}

const read = (path: string) => {
  try {
    return readFileSync(path, "utf8").trim();
  } catch {
    return "";
  }
};
const list = (path: string) => {
  try {
    return readdirSync(path);
  } catch {
    return [];
  }
};

// USB vendors that only make fingerprint readers among their USB devices.
const fingerprintVendors = new Set(["27c6", "138a", "147e", "10a5", "1c7a", "2808"]);

/** Reads `root` (normally /sys) for the hardware features care about. Missing files mean absent hardware. */
export function detectHardware(root = "/sys"): Hardware {
  const supplies = join(root, "class/power_supply");
  const pci = join(root, "bus/pci/devices");
  const usb = join(root, "bus/usb/devices");
  const usbDevices = list(usb);
  return {
    // A battery of the system itself, not one reported by a mouse or headset.
    laptop: list(supplies).some(
      (name) => read(join(supplies, name, "type")) === "Battery" && read(join(supplies, name, "scope")) !== "Device",
    ),
    // Vendor 0x10de, display controller class 0x03.
    nvidia: list(pci).some((device) => read(join(pci, device, "vendor")) === "0x10de" && read(join(pci, device, "class")).startsWith("0x03")),
    bluetooth: list(join(root, "class/bluetooth")).length > 0,
    // USB interface class 07 is a printer.
    printer: usbDevices.some((device) => read(join(usb, device, "bInterfaceClass")) === "07"),
    fingerprint: usbDevices.some(
      (device) => fingerprintVendors.has(read(join(usb, device, "idVendor"))) || /fingerprint/i.test(read(join(usb, device, "product"))),
    ),
  };
}

/** Plugged in: no battery of its own, or a mains supply that is online. */
export function onMains(root = "/sys") {
  const supplies = join(root, "class/power_supply");
  const names = list(supplies);
  const type = (name: string) => read(join(supplies, name, "type"));
  const battery = names.some((name) => type(name) === "Battery" && read(join(supplies, name, "scope")) !== "Device");
  return !battery || names.some((name) => type(name) === "Mains" && read(join(supplies, name, "online")) === "1");
}
