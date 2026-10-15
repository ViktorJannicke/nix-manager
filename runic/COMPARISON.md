# Runic port against the Bun version

These numbers come from the port published with NativeAOT (`perf-startup`, on `runic-port` 3fd242e): one native
executable instead of the JIT build on the .NET runtime. The JIT build's numbers, from the finished port of W300-032
(`w300-032-packaging` with `w300-032-finish` 7b40bc0 merged: all dashboard pages, the setup assistant, tray
translations, and the fix for the daemon's start-up memory), are kept as "before", with where its start-up time went.
An earlier run against the unfinished port (ce62fd8) is kept at the end.

## Method

`nix run .#compare -- DIRECTORY` (`nix/compare/`) measures both packages in the same way, one after the other,
and writes `DIRECTORY/report.md` with the raw JSON beside it:

- **Where.** Everything runs in the Hyprland sandbox (`nix/hyprland-sandbox`). That is headless Hyprland and
  Waybar with its tray on the host GPU's render node, inside bubblewrap. The sandbox has its own PID namespace,
  runtime directory and session bus, and no network, nix-daemon, host Wayland, X11 or D-Bus. Nothing appears on
  the desktop, and a Nix Manager that is already running cannot be reached.
- **State.** Each version gets a fresh `HOME`, `XDG_STATE_HOME`, `XDG_CONFIG_HOME` and `XDG_CACHE_HOME`, and the
  same fixture: `~/my-flakes` holding an empty flake, and a host registry (`NIX_MANAGER_HOSTS`) that names the
  sandbox's host. Neither version is told which checkout or host to use. With `NIX_MANAGER_FLAKE` set, the Bun
  version's `ui` would not hand over to its tray.
- **Build tools.** Mocked tools come first on `PATH`, but the packages' wrappers put their own `nix`, `git` and
  `home-manager` before them. None of the measured commands builds anything. The update check that each tray
  starts fails at once, because the sandbox has no nix-daemon and no network. The Runic dashboard therefore shows
  one failed "Check for updates".
- **Command line.** For `--help` and for `status --json`, the run takes the first run in the fresh state directory
  and then the median of 10 warm runs, wall clock, including process exit. The page cache is not dropped, because
  that needs root. The first run is therefore a first start with the files probably already cached, not a cold
  boot.
- **Background process.** `nix-manager tray --interval 10080` starts the Bun tray or the Runic daemon. The time is
  measured until Waybar's StatusNotifierWatcher lists a new item. Memory is the sum, over every process that
  started since then, of VmRSS and of Pss (`/proc/PID/smaps_rollup`), read after 30 s idle.
- **Window.** With the background process running, `nix-manager ui` is started and hands the launch over. Both
  versions do that, then exit. The time is measured until Hyprland maps a new client. Memory is the same sum,
  read 10 s later. For Runic that is the daemon, WebKitNetworkProcess and WebKitWebProcess. For Bun it is the tray
  and the Chromium app window that WebUI starts; Chromium's processes are re-parented to the sandbox's init, so
  they are counted by start time, not by parent. Chromium is nixpkgs' Chromium 154 on Wayland with `--no-sandbox`,
  because bubblewrap leaves it no user namespace. WebKitGTK 2.54.1 runs with its own sandbox off, as Runic Desktop
  starts it. Both render on the GPU.
- **Size.** `nix path-info --recursive --size --closure-size` gives each package's NAR size and closure. The
  report also gives the largest pieces of each closure, and how much Chromium adds beyond the Bun closure. The Bun
  version opens an installed browser, which its package does not bring.

`COMPARE_REPEAT`, `COMPARE_IDLE_SECONDS` and `COMPARE_LOAD_SECONDS` change the counts and waits.

## Results (2026-10-15, NativeAOT)

Host: x86_64-linux, AMD Radeon RX 6950 XT (radeonsi), 24 threads, 62 GiB RAM, NixOS. The table shows the first of
two consecutive runs. The second run agreed within 1 ms, except `ui` to Window (154 ms; Bun 472 ms).

| Measure | Runic | Bun |
| --- | --- | --- |
| `--help`, first run | 11 ms | 136 ms |
| `--help`, median warm run | 8 ms | 126 ms |
| `status --json`, fixture checkout, first run | 16 ms | 124 ms |
| `status --json`, fixture checkout, median warm run | 17 ms | 118 ms |
| `tray` start to status icon registered | 29 ms | 150 ms |
| `ui` to Window mapped (tray running) | 145 ms | 458 ms |
| background process after 30 s idle, RSS (all its processes) | 47.8 MiB | 103.1 MiB |
| background process after 30 s idle, PSS | 36.7 MiB | 80.8 MiB |
| with the Window open after 10 s, RSS (all its processes) | 616 MiB | 1286 MiB |
| with the Window open after 10 s, PSS | 359 MiB | 523 MiB |
| package (NAR size) | 23 MiB | 91 MiB |
| closure | 1116 MiB | 894 MiB |
| browser outside the closure | none: WebKitGTK is in the closure | Chromium: 1332 MiB beyond the Bun closure |

The largest pieces of each closure (NAR size):

- **Runic:**
  - WebKitGTK with JavaScriptCore, 176 MiB;
  - flite, 60 MiB (WebKitGTK's speech synthesis, which the dashboard does not use);
  - GTK 3, 42 MiB;
  - the package itself, 23 MiB: the native executable (20 MiB) and the frontend.
- **Bun:**
  - the package itself, 91 MiB (`main.js` and `node_modules`);
  - Node.js, 102 MiB, which the `node_modules` scripts' interpreters pull in;
  - Bun, 76 MiB;
  - the WebUI library, 0.3 MiB.
- **Both:** Nix, Home Manager and Git, 76 MiB.

The .NET runtime is no longer in the closure: the JIT build needed the ASP.NET Core runtime (101 MiB).

## Before and after NativeAOT

The same harness, first of two runs each:

| Measure | JIT (before) | NativeAOT (after) | Bun |
| --- | --- | --- | --- |
| `--help`, median warm run | 69 ms | 8 ms | 126 ms |
| `status --json`, first run | 185 ms | 16 ms | 124 ms |
| `status --json`, median warm run | 190 ms | 17 ms | 118 ms |
| `tray` start to status icon registered | 245 ms | 29 ms | 150 ms |
| `ui` to Window mapped (tray running) | 318 ms | 145 ms | 458 ms |
| background process after 30 s idle, RSS / PSS | 90.0 / 74.4 MiB | 47.8 / 36.7 MiB | 103.1 / 80.8 MiB |
| with the Window open, RSS / PSS | 690 / 425 MiB | 616 / 359 MiB | 1286 / 523 MiB |
| package / closure | 8.8 / 1205 MiB | 23 / 1116 MiB | 91 / 894 MiB |

The dashboard looks the same in both builds' screenshots, and the same tests pass (below).

## Where the tray's start-up time goes

`strace -f -tt` of `nix-manager tray --interval 10080` in the sandbox, with the same fixture, records when each
library and assembly opens, when `git` runs, and when the session bus is reached; the icon time is when Waybar's
watcher lists the item, as above. strace slows the process: the JIT tray took 341 ms under it (245 ms without), the
NativeAOT tray 41 ms (23 to 29 ms without). The times below are under strace.

**Before: JIT, 341 ms**

| From | To | What |
| --- | --- | --- |
| 0 | 60 ms | The host and runtime: `libhostfxr`, `libhostpolicy`, `libcoreclr` with its LTTng trace provider, `System.Private.CoreLib`; `nix-manager.dll` opens at 60 ms. |
| 60 | 80 ms | `Main` starts: ICU, `System.Console`, `Runic.Platform`. |
| 80 | 124 ms | The configuration loader reads the generated `PrivilegedActions` for the privileged prefix. Runic.Platform's `PrivilegedActionDeclaration` compiles its `ArgumentPattern` as a `NonBacktracking` regular expression when the declaration is created; at Tier 0 that is about 45 ms of JIT (71 regex methods). |
| 124 | 141 ms | The checkout: `git rev-parse --show-toplevel` (136 to 141 ms). |
| 141 | 202 ms | The host registry (System.Text.Json, source-generated) and the single-instance claim: little I/O, mostly Tier 0 JIT. |
| 202 | 291 ms | The Desktop event loop and the services: `Runic.Platform.Runtime`, `Runic.Desktop`, the Views, the dependency injection container, `HttpClient`, Polkit, the portal, `Tmds.DBus.Protocol`; mostly assembly loading and JIT. |
| 291 | 341 ms | The session bus and the StatusNotifierItem registration, until the watcher lists the item. |

`status --json` JIT-compiles 1947 methods (1800 at Tier 0), `--help` 598. Nothing that the icon does not need
runs before it: no WebKitGTK, no tree-sitter, no dashboard catalogue (the tray reads its own small catalogues), and
the first update check waits 10 s. Deferring work therefore had nothing to win; the time was the runtime and the JIT.

**After: NativeAOT, 41 ms**

| From | To | What |
| --- | --- | --- |
| 0 | 10.6 ms | The binary wrapper, then the 20 MiB executable loads and its runtime initializes. |
| 10.6 | 12.6 ms | ICU. |
| 12.6 | 19.5 ms | The configuration, including the Polkit declarations, now precompiled. |
| 19.5 | 24.6 ms | `git rev-parse --show-toplevel`, now the largest single item. |
| 24.6 | 31.5 ms | The host registry, the single-instance claim, the Desktop host and the services. |
| 31.5 | 41 ms | The session bus and the StatusNotifierItem registration. |

**JIT settings, measured on `status --json` before NativeAOT and not kept:** `DOTNET_TieredPGO=0` 197 → 189 ms;
`DOTNET_TC_QuickJitForLoops=1` and `DOTNET_OSR_HitLimit=0` no change; `DOTNET_TieredCompilation=0` 366 ms. ReadyToRun
was not tried: NativeAOT builds without warnings and removes the JIT entirely.

## What NativeAOT required

No source change. The application had no trim or AOT warnings: Runic's generated Bridges and command catalogue,
the source-generated JSON contexts and regular expressions, the tree-sitter calls through unmanaged function
pointers, Tmds.DBus.Protocol and Microsoft.Extensions.DependencyInjection with factory registrations. The build
changes:

- `package.nix` sets `nativeAot = true` for `buildRunicApplication`. The shell completions run the executable
  without `DOTNET_ROOT`; `checks.nix` runs it the same way.
- A NativeAOT restore adds the compiler packages, so the application has an AOT lock file per runtime
  (`packages.aot.lock.json`, `packages.linux-arm64.aot.lock.json`), chosen in `NixManager.csproj` when `PublishAot`
  is set. They differ from the JIT ones only by `Microsoft.DotNet.ILCompiler` and `Microsoft.NET.ILLink.Tasks`,
  which the .NET SDK provides. The JIT lock files stay for `dotnet build` and the tests.
- The builder passes `-p:PublishAot=true` to every project, the tests too. `NixManager.Tests.csproj` keeps
  `PublishAot` local (`TreatAsLocalProperty`) and false, so the tests restore with their JIT lock files and run
  under the JIT as before.
- `IlcTreatWarningsAsErrors` fails the publish on any trim or AOT warning.

The Polkit action is unchanged: its program is still `$out/bin/nix-manager`, the binary wrapper, which now runs the
native executable. Checked: the 393 C# tests, `checks.cli-runic`, `checks.daemon-runic` and
`desktop-a11y-runic` (which runs the helper as root through pkexec) on the NativeAOT executable.

## Before: the JIT build (W300-032)

The same harness and host, first of two runs (the second agreed within 3 %):

| Measure | Runic | Bun |
| --- | --- | --- |
| `--help`, first run | 69 ms | 131 ms |
| `--help`, median warm run | 69 ms | 123 ms |
| `status --json`, fixture checkout, first run | 185 ms | 117 ms |
| `status --json`, fixture checkout, median warm run | 190 ms | 116 ms |
| `tray` start to status icon registered | 245 ms | 147 ms |
| `ui` to Window mapped (tray running) | 318 ms | 424 ms |
| background process after 30 s idle, RSS (all its processes) | 90.0 MiB | 103.6 MiB |
| background process after 30 s idle, PSS | 74.4 MiB | 81.3 MiB |
| with the Window open after 10 s, RSS (all its processes) | 690 MiB | 1278 MiB |
| with the Window open after 10 s, PSS | 425 MiB | 521 MiB |
| package (NAR size) | 8.8 MiB | 91 MiB |
| closure | 1205 MiB | 894 MiB |
| browser outside the closure | none: WebKitGTK is in the closure | Chromium: 1332 MiB beyond the Bun closure |

The largest pieces of each closure (NAR size):

- **Runic:**
  - WebKitGTK with JavaScriptCore, 176 MiB;
  - the .NET 10 ASP.NET Core runtime, 101 MiB;
  - flite, 60 MiB (WebKitGTK's speech synthesis, which the dashboard does not use);
  - GTK 3, 42 MiB;
  - the package itself, 8.8 MiB.
- **Bun:**
  - the package itself, 91 MiB (`main.js` and `node_modules`);
  - Node.js, 102 MiB, which the `node_modules` scripts' interpreters pull in;
  - Bun, 76 MiB;
  - the WebUI library, 0.3 MiB.
- **Both:** Nix, Home Manager and Git, 76 MiB.

**A regression found on the way, and fixed.** With the tray translations on Runic.Translations, the daemon idled at
132 or 255 MiB, depending on the run, and showed its icon after about 510 ms. The cause was the C# that
Runic.Translations.Build generates for the whole dashboard catalogue: one `CreateDefinition` method with about
15,000 constructions, which the JIT compiled at start for roughly 50 MiB. The daemon now reads the `tray/` and
`stored/` catalogues, embedded in the assembly, with a small reader (8bce75a). That gives the numbers above.

Reading these numbers then: the port started faster than Bun for `--help` and opened its Window faster, but was
slower for `status --json` (the configuration loaded under the JIT) and for the tray's icon. NativeAOT closes both
gaps.

## Earlier run: the unfinished port (ce62fd8)

The same harness, before the final pages, the setup assistant and the tray translations:

| Measure | Runic | Bun |
| --- | --- | --- |
| `--help`, median warm run | 75 ms | 128 ms |
| `status --json`, median warm run | 204 ms | 121 ms |
| `tray` start to status icon registered | 246 ms | 152 ms |
| `ui` to Window mapped | 298 ms | 457 ms |
| background process after 30 s idle, RSS / PSS | 80.7 / 65.5 MiB | 102.7 / 80.4 MiB |
| with the Window open, RSS / PSS | 589 / 328 MiB | 1279 / 520 MiB |
| package / closure | 6.9 / 1173 MiB | 91 / 894 MiB |

## Runic SDK gaps found by this comparison

Runic.Translations.Build generates one method that builds every message of a catalogue. For a catalogue of
dashboard size, the JIT costs about 50 MiB and a quarter of a second at start, in every process that touches it.
Suggested fix: generate data (an embedded catalogue read lazily) or split the method per message or per namespace,
so that a process that needs a few messages, such as a tray, only pays for those.

`PrivilegedActionDeclaration` (Runic.Platform) compiles its `ArgumentPattern` as a `NonBacktracking` regular
expression when the declaration is created, so a JIT process that reads the generated `PrivilegedActions`, even one
that never authorizes anything, pays about 45 ms. Suggested fix: compile the pattern on the first `AcceptsArguments`
(it is validated at build time already), or generate it with `[GeneratedRegex]` in the `PrivilegedActions` class.

`buildRunicApplication` with `nativeAot = true` passes `-p:PublishAot=true` to the test project as well, whose
restore then needs AOT lock files of its own, or `TreatAsLocalProperty` as here. Suggested fix: pass `PublishAot`
only to `projectFile`, or document the test project setting in the Nix guide.
