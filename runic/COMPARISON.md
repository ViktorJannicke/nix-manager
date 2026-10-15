# Runic port against the Bun version

These numbers come from the finished port of W300-032: `w300-032-packaging` with `w300-032-finish` 7b40bc0 merged
(all dashboard pages, the setup assistant, tray translations, and the fix for the daemon's start-up memory). An earlier run against the unfinished port (ce62fd8)
is kept at the end for comparison.

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

## Results (2026-10-15)

Host: x86_64-linux, AMD Radeon RX 6950 XT (radeonsi), 24 threads, 62 GiB RAM, NixOS. The table shows the first of
two consecutive runs. The second run agreed within 3 %.

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

## Reading the numbers

- **Command line.** The port starts faster for `--help`. It is slower for `status --json`, which loads the
  configuration with the .NET JIT. ReadyToRun or a trimmed start-up path are the obvious levers. They are not
  tried here.
- **Memory.** Idle, the daemon uses about a tenth less than the Bun tray. With the Window open, the port uses
  0.54× the RSS and 0.8× the PSS. WebKitGTK's two processes cost far less than the Chromium process tree that
  WebUI starts.
- **Start-up of the background process.** The Bun tray registers its icon in 150 ms, and the daemon in 250 ms.
- **Size.** The port's own closure is larger, because WebKitGTK and the .NET runtime are in it. The Bun version
  relies on a browser outside its closure, and Chromium adds 1.3 GiB when it is not installed already. On a
  desktop that has GTK and WebKitGTK already, much of the port's closure is shared.

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

## Runic SDK gap found by this comparison

Runic.Translations.Build generates one method that builds every message of a catalogue. For a catalogue of
dashboard size, the JIT costs about 50 MiB and a quarter of a second at start, in every process that touches it.
Suggested fix: generate data (an embedded catalogue read lazily) or split the method per message or per namespace,
so that a process that needs a few messages, such as a tray, only pays for those.

