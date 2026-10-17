"""Writes the comparison table from measure.py's results and nix path-info's closures; see default.nix.

report.py OUTPUT-DIRECTORY RUNIC BUN CHROMIUM
"""
import json
import re
import sys
from pathlib import Path

out, runic, bun, chromium = sys.argv[1:5]
out = Path(out)
MIB = 1024 * 1024


def closure(name):
    data = json.loads((out / f"{name}-closure.json").read_text())
    # nix path-info --json-format 1: a list of objects, or an object keyed by path in later versions.
    entries = data if isinstance(data, list) else [dict(value, path=key) for key, value in data.items()]
    return {entry["path"]: entry for entry in entries}


def pieces(paths, patterns):
    sizes = {}
    for label, pattern in patterns.items():
        sizes[label] = sum(entry["narSize"] for path, entry in paths.items() if re.search(pattern, path))
    return sizes


def mib(value):
    return f"{value / MIB:.1f} MiB" if value < 10 * MIB else f"{value / MIB:.0f} MiB"


results = {name: json.loads((out / "sandbox" / f"{name}.json").read_text())
           for name in ("runic", "bun") if (out / "sandbox" / f"{name}.json").exists()}
closures = {"runic": closure("runic"), "bun": closure("bun"), "chromium": closure("chromium")}
own = {name: closures[name][path]["narSize"] for name, path in (("runic", runic), ("bun", bun))}
total = {name: closures[name][path]["closureSize"] for name, path in (("runic", runic), ("bun", bun))}
browser_only = sum(entry["narSize"] for path, entry in closures["chromium"].items() if path not in closures["bun"])

rows = []


def row(label, runic_value, bun_value):
    rows.append(f"| {label} | {runic_value} | {bun_value} |")


def timing(key, field):
    return {name: (f"{r[key][field]} ms" if key in r else "–") for name, r in results.items()}


for key, label in (("help", "`--help`"), ("status_json", "`status --json`, fixture checkout")):
    for field, kind in (("first_ms", "first run"), ("median_ms", "median warm run")):
        t = timing(key, field)
        row(f"{label}, {kind}", t.get("runic", "–"), t.get("bun", "–"))
for key, label in (("tray_to_icon_ms", "`tray` start to status icon registered"),
                   ("ui_to_window_ms", "`ui` to Window mapped (tray running)")):
    row(label, *(f"{results[n][key]} ms" if n in results and key in results[n] else "–" for n in ("runic", "bun")))
for key, label in (("tray_idle", "background process idle"), ("window_open", "with the Window open")):
    for field in ("rss_mib", "pss_mib"):
        row(f"{label}, {field.split('_')[0].upper()} (all its processes)",
            *(f"{results[n][key][field]} MiB" if n in results and key in results[n] else "–" for n in ("runic", "bun")))
row("package (NAR size)", mib(own["runic"]), mib(own["bun"]))
row("closure", mib(total["runic"]), mib(total["bun"]))
row("browser outside the closure (Chromium, as in the sandbox)", "none (WebKitGTK is in the closure)",
    f"{mib(browser_only)} beyond the Bun closure")

runic_pieces = pieces(closures["runic"], {
    ".NET runtime (ASP.NET Core)": r"-dotnet-aspnetcore-runtime|-dotnet-runtime",
    "WebKitGTK and JavaScriptCore": r"-webkitgtk-",
    "flite (WebKitGTK's speech synthesis)": r"-flite-",
    "GTK 3": r"-gtk\+3-",
    "Nix, Home Manager, Git": r"-nix-2|-home-manager-|-git-[0-9]",
    "the package itself": re.escape(runic),
})
bun_pieces = pieces(closures["bun"], {
    "Bun": r"-bun-[0-9]",
    "WebUI library": r"-webui-",
    "Node.js (node_modules' scripts)": r"-nodejs-",
    "Nix, Home Manager, Git": r"-nix-2|-home-manager-|-git-[0-9]",
    "the package itself (main.js and node_modules)": re.escape(bun),
})

print("| Measure | Runic | Bun |")
print("| --- | --- | --- |")
print("\n".join(rows))
print()
print("Largest pieces of each closure (NAR size):")
print()
for name, sizes in (("Runic", runic_pieces), ("Bun", bun_pieces)):
    # A piece the closure lacks, such as the .NET runtime of a NativeAOT build, is left out.
    print(f"- {name}: " + ", ".join(f"{label} {mib(size)}" for label, size in sizes.items() if size))
for name, r in results.items():
    print(f"- {name} processes with the Window open: {', '.join(r['window_open']['processes'])}; window class {r.get('window_class')}")
