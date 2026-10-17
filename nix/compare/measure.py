"""Measures one Nix Manager package inside the hyprland sandbox; see default.nix.

measure.py NAME PACKAGE OUTPUT.json

Everything runs with a fresh HOME, state and runtime directory under /tmp, against a fixture checkout and host
registry, so no command reads the maintainer's state or reaches a running Nix Manager. The sandbox has no network,
no nix-daemon and a private session bus; the update check that a tray starts fails at once.
"""
import json
import os
import re
import shutil
import signal
import socket
import statistics
import subprocess
import sys
import time
from pathlib import Path

NAME, PACKAGE, OUTPUT = sys.argv[1:4]
PROGRAM = f"{PACKAGE}/bin/nix-manager"
REPEAT = int(os.environ.get("COMPARE_REPEAT", "10"))
IDLE = int(os.environ.get("COMPARE_IDLE_SECONDS", "30"))
LOADED = int(os.environ.get("COMPARE_LOAD_SECONDS", "10"))

root = Path("/tmp/compare") / NAME
shutil.rmtree(root, ignore_errors=True)
for directory in ("home/my-flakes", "state", "config", "cache", "data", "bin"):
    (root / directory).mkdir(parents=True)
runtime = root / "runtime"
runtime.mkdir(mode=0o700)
# The default checkout, ~/my-flakes, and this computer in the host registry, so that neither version is told which
# checkout or host to use: with NIX_MANAGER_FLAKE set, the Bun version's `ui` would not hand over to its tray.
(root / "home/my-flakes/flake.nix").write_text("{ outputs = _: { }; }\n")
(root / "hosts.json").write_text(json.dumps({"compare": {
    "system": "x86_64-linux", "nixosConfiguration": "compare", "nixosHostName": socket.gethostname(),
    "homeConfiguration": "compare"}}))
# Mocked tools for anything that reaches past the wrappers' own PATH entries.
for tool in ("nix", "nix-env", "nix-store", "home-manager", "nixos-rebuild"):
    mock = root / "bin" / tool
    mock.write_text("#!/bin/sh\necho \"mocked $0 $*\" >&2\nexit 1\n")
    mock.chmod(0o755)

env = {key: value for key, value in os.environ.items()
       if key not in ("NIX_MANAGER_DAEMON", "NIX_MANAGER_FLAKE", "NIX_MANAGER_HOST")}
env |= {
    "HOME": str(root / "home"),
    "XDG_STATE_HOME": str(root / "state"),
    "XDG_CONFIG_HOME": str(root / "config"),
    "XDG_CACHE_HOME": str(root / "cache"),
    "XDG_DATA_HOME": str(root / "data"),
    "NIX_MANAGER_HOSTS": str(root / "hosts.json"),
    "PATH": f"{root / 'bin'}:{os.environ['PATH']}",
    "NO_COLOR": "1",
    "LANG": "C.UTF-8",
}
results = {"name": NAME, "package": PACKAGE}


def run(args, extra=None):
    started = time.perf_counter()
    result = subprocess.run([PROGRAM, *args], env=env | (extra or {}), stdin=subprocess.DEVNULL,
                            stdout=subprocess.PIPE, stderr=subprocess.PIPE, text=True, timeout=120)
    elapsed = time.perf_counter() - started
    if result.returncode != 0:
        raise SystemExit(f"{NAME}: nix-manager {' '.join(args)} failed ({result.returncode}): {result.stderr[-2000:]}")
    return elapsed


def timings(key, args, extra=None):
    # The first run in a fresh state directory, then warm runs. The page cache is not dropped (that needs root),
    # so "first" is a first start of the process with its files likely cached, not a cold boot.
    first = run(args, extra)
    warm = [run(args, extra) for _ in range(REPEAT)]
    results[key] = {"first_ms": round(first * 1000), "median_ms": round(statistics.median(warm) * 1000),
                    "min_ms": round(min(warm) * 1000), "runs": REPEAT}


def pids():
    return {int(p.name) for p in Path("/proc").iterdir() if p.name.isdigit()}


def memory(selected):
    rss = pss = 0
    names = []
    for pid in sorted(selected):
        try:
            status = Path(f"/proc/{pid}/status").read_text()
            rollup = Path(f"/proc/{pid}/smaps_rollup").read_text()
        except OSError:
            continue
        rss += int(re.search(r"^VmRSS:\s+(\d+)", status, re.M).group(1)) if "VmRSS" in status else 0
        pss += int(re.search(r"^Pss:\s+(\d+)", rollup, re.M).group(1))
        names.append(re.search(r"^Name:\s+(.*)$", status, re.M).group(1))
    return {"rss_mib": round(rss / 1024, 1), "pss_mib": round(pss / 1024, 1), "processes": names}


def sni_items():
    output = subprocess.run(["sandbox-sni-items"], capture_output=True, text=True).stdout
    return [line for line in output.splitlines() if line.strip()]


def clients():
    output = subprocess.run(["hyprctl", "-j", "clients"], capture_output=True, text=True).stdout
    try:
        return json.loads(output)
    except json.JSONDecodeError:
        return []


def wait(condition, limit, what):
    deadline = time.perf_counter() + limit
    while time.perf_counter() < deadline:
        if condition():
            return
        time.sleep(0.02)
    raise SystemExit(f"{NAME}: {what} did not happen within {limit} s")


def stop(processes):
    for pid in processes:
        try:
            os.kill(pid, signal.SIGTERM)
        except ProcessLookupError:
            pass
    deadline = time.time() + 10
    while time.time() < deadline and any(Path(f"/proc/{pid}").exists() for pid in processes):
        time.sleep(0.1)
    for pid in processes:
        try:
            os.kill(pid, signal.SIGKILL)
        except ProcessLookupError:
            pass


timings("help", ["--help"])
timings("status_json", ["status", "--json"])

# The background process: the Bun tray, or the port's daemon with its status icon.
baseline = pids() | {os.getpid()}
items = len(sni_items())
logs = open(f"/out/logs/{NAME}-tray.log", "w")
started = time.perf_counter()
tray = subprocess.Popen([PROGRAM, "tray", "--interval", "10080"], env=env, stdin=subprocess.DEVNULL,
                        stdout=logs, stderr=subprocess.STDOUT)
try:
    wait(lambda: len(sni_items()) > items, 60, "the status icon")
    results["tray_to_icon_ms"] = round((time.perf_counter() - started) * 1000)
    time.sleep(IDLE)
    results["tray_idle"] = memory(pids() - baseline) | {"after_s": IDLE}

    # The dashboard Window, opened by a second command that hands it to the running process.
    windows = len(clients())
    started = time.perf_counter()
    # Both versions hand `ui` to the running process and exit; it is measured with the rest if it does not.
    subprocess.Popen([PROGRAM, "ui"], env=env, stdin=subprocess.DEVNULL, stdout=logs, stderr=subprocess.STDOUT)
    wait(lambda: len(clients()) > windows, 60, "the dashboard window")
    results["ui_to_window_ms"] = round((time.perf_counter() - started) * 1000)
    results["window_class"] = [c.get("class") for c in clients()][-1]
    time.sleep(LOADED)
    subprocess.run(["sandbox-shot", f"{NAME}-window.png"], stdout=subprocess.DEVNULL)
    results["window_open"] = memory(pids() - baseline) | {"after_s": LOADED}
    results["window_title"] = [c.get("title") for c in clients()][-1]
finally:
    stop(sorted(pids() - baseline))
    logs.close()

Path(OUTPUT).write_text(json.dumps(results, indent=2) + "\n")
print(json.dumps(results, indent=2))
