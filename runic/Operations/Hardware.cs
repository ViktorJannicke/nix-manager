using System.Globalization;
using System.Text.RegularExpressions;

namespace NixManager.Operations;

/// <summary>Hardware of the computer Nix Manager runs on that a feature can make use of.</summary>
/// <param name="Laptop">Runs on a battery.</param>
/// <param name="Nvidia">Has an NVIDIA graphics card.</param>
/// <param name="Bluetooth">Has a Bluetooth adapter.</param>
/// <param name="Printer">A printer is connected by USB.</param>
/// <param name="Fingerprint">Has a fingerprint reader.</param>
public sealed record HardwareFeatures(bool Laptop, bool Nvidia, bool Bluetooth, bool Printer, bool Fingerprint);

/// <summary>The computer's hardware, power supply and load.</summary>
public static partial class Hardware
{
    /// <summary>Where the kernel's device tree is read: <c>NIX_MANAGER_SYSFS</c>, which tests point at a copy, or /sys.</summary>
    public static string SysfsRoot => Environment.GetEnvironmentVariable("NIX_MANAGER_SYSFS") is { Length: > 0 } root ? root : "/sys";

    private static readonly Lazy<HardwareFeatures> Detected = new(() => Detect(SysfsRoot));

    /// <summary>
    /// This computer's hardware, read once per process: suggestions for hardware plugged in
    /// later appear after a restart.
    /// </summary>
    public static HardwareFeatures Current => Detected.Value;

    private static string Read(string path)
    {
        try { return File.ReadAllText(path).Trim(); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return ""; }
    }

    private static string[] List(string path)
    {
        try { return Directory.GetFileSystemEntries(path).Select(entry => Path.GetFileName(entry)).ToArray(); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return []; }
    }

    // USB vendors that only make fingerprint readers among their USB devices.
    private static readonly HashSet<string> FingerprintVendors = ["27c6", "138a", "147e", "10a5", "1c7a", "2808"];

    [GeneratedRegex("fingerprint", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex FingerprintProduct();

    /// <summary>A battery of the system itself, not one reported by a mouse or headset.</summary>
    private static bool HasBattery(string supplies, IEnumerable<string> names) =>
        names.Any(name => Read(Path.Combine(supplies, name, "type")) == "Battery" && Read(Path.Combine(supplies, name, "scope")) != "Device");

    /// <summary>Reads <paramref name="root"/> (normally /sys) for the hardware features care about; missing files mean absent hardware (<c>detectHardware</c>).</summary>
    public static HardwareFeatures Detect(string root = "/sys")
    {
        var supplies = Path.Combine(root, "class/power_supply");
        var pci = Path.Combine(root, "bus/pci/devices");
        var usb = Path.Combine(root, "bus/usb/devices");
        var usbDevices = List(usb);
        return new HardwareFeatures(
            Laptop: HasBattery(supplies, List(supplies)),
            // Vendor 0x10de, display controller class 0x03.
            Nvidia: List(pci).Any(device => Read(Path.Combine(pci, device, "vendor")) == "0x10de"
                && Read(Path.Combine(pci, device, "class")).StartsWith("0x03", StringComparison.Ordinal)),
            Bluetooth: List(Path.Combine(root, "class/bluetooth")).Length > 0,
            // USB interface class 07 is a printer.
            Printer: usbDevices.Any(device => Read(Path.Combine(usb, device, "bInterfaceClass")) == "07"),
            Fingerprint: usbDevices.Any(device => FingerprintVendors.Contains(Read(Path.Combine(usb, device, "idVendor")))
                || FingerprintProduct().IsMatch(Read(Path.Combine(usb, device, "product")))));
    }

    /// <summary>Plugged in: no battery of its own (a mouse's battery does not count), or a mains supply that is online (<c>onMains</c>).</summary>
    public static bool OnMains(string root = "/sys")
    {
        var supplies = Path.Combine(root, "class/power_supply");
        var names = List(supplies);
        return !HasBattery(supplies, names)
            || names.Any(name => Read(Path.Combine(supplies, name, "type")) == "Mains" && Read(Path.Combine(supplies, name, "online")) == "1");
    }

    /// <summary>Not busy: the load of the last minute is below half the processors.</summary>
    public static bool NotBusy()
    {
        var first = Read("/proc/loadavg").Split(' ').FirstOrDefault();
        var load = double.TryParse(first, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) ? value : 0;
        return load < Environment.ProcessorCount / 2.0;
    }
}
