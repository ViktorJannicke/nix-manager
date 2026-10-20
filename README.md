# Nix Manager

Nix Manager builds, reviews and applies NixOS and Home Manager configurations
from a flake checkout. Its command line, dashboard Window and Linux tray are one
C# application on the [Runic SDK](https://github.com/Runic-Artifex/runic-sdk),
in `runic/`, with a Svelte dashboard. The command line is defined with Runic
Command Line; its help, version output and shell completions are generated from
the same command catalogue, so they stay in sync with the accepted options. Run
`nix-manager --help` for the complete command reference.

```sh
nix run github:ViktorJannicke/nix-manager -- --flake /path/to/configuration ui
nix run . -- --flake /path/to/configuration --dry-run update cleanup switch
nix run . -- --flake /path/to/configuration check --output json
nix run . -- --flake /path/to/configuration tray
```

## Install with NixOS

Add this flake as an input and import its NixOS module:

```nix
inputs.nix-manager.url = "github:ViktorJannicke/nix-manager";

# Inside nixosSystem:
modules = [
  inputs.nix-manager.nixosModules.default
  {
    programs.nix-manager = {
      enable = true;
      hosts.desktop = {
        system = "x86_64-linux";
        nixosConfiguration = "desktop";
        nixosHostName = "desktop";
        homeConfiguration = "user-desktop";
      };
      tray = {
        enable = true;
        flakePath = "%h/configuration";
      };
    };
  }
];
```

The module installs the package (`programs.nix-manager.package`, by default
this flake's `packages.${system}.default`) and the optional tray service. The
package brings the `nix-manager` command, the desktop entry (with a "Start in
system tray" action and `StartupNotify`, so the desktop hands `nix-manager ui`
an activation token), the icon, bash, zsh and fish completions generated from
its command line at build time (NixOS links them when
`programs.bash.completion`, `programs.zsh` or `programs.fish` is on), and the
Polkit policy that its build generates for its privileged action, whose program
is the package's binary wrapper that pkexec runs (`/run/wrappers/bin/pkexec`,
which the module enables). `checks.module` inspects what the module links into
the system path. The former option `programs.nix-manager.runic.package` is
renamed to `programs.nix-manager.package`; `programs.nix-manager.runic.enable`
is removed, and setting it fails with an explanation.

The module writes the host registry to `/etc/nix-manager/hosts.json` and sets
`NIX_MANAGER_HOSTS` for login sessions and the tray. An empty `hosts` option uses
`data/hosts.nix` from the selected configuration checkout when it exists, and
otherwise reads the host from the flake itself: the NixOS configuration named
like the computer (as `nixos-rebuild` picks it) or setting it as
`networking.hostName`, and the Home Manager configuration named `user@host` or
`user` (as `home-manager` picks it), or the user's one whose name shares a word
with the host. When that is still ambiguous, `--host` chooses the NixOS
configuration, and a registry settles the rest. An explicit
`NIX_MANAGER_HOSTS` environment variable can point to another JSON registry.
New login sessions pick up changed system environment variables.

The application retains its own locked nixpkgs and Home Manager inputs. Do not
set `inputs.nix-manager.inputs.nixpkgs.follows = "nixpkgs"` if you want system
dependency updates to leave the application build unchanged. Consume
`inputs.nix-manager.packages.${system}.default` directly instead of rebuilding
its recipe with the consumer's package set. Host metadata is runtime
configuration, so changing it leaves the application store path unchanged.

Home Manager has to be a standalone `homeConfigurations` entry; Home Manager
imported as a NixOS module is not supported yet. Without `--flake`, the manager
uses the Git checkout it runs in when that is a system configuration, and
otherwise `~/my-flakes`. The optional T3 Code, OpenCode and
PINCE package updaters expect the corresponding `pkgs/` recipes and source pins
used by `my-flakes`; extraction does not make those commands generic.

The package builds for x86_64-linux and aarch64-linux. The aarch64 build has
only been evaluated so far, without an aarch64 builder. It is published with
NativeAOT as one native executable without the .NET runtime, so the tray shows
its icon and the command line answers without starting a runtime or compiling.

## CI and binary cache

GitHub Actions evaluates both Linux architectures and builds/tests x86_64-linux
using the committed lock files. It also builds the frontend's dependencies
(`.#default.frontendNodeModules`) explicitly, so the build-time dependency cache
can be uploaded even when the application is already available from a cache.
Cachix support is optional and initially disabled.

To enable it, create or choose a public Cachix cache, then set:

- Repository variable `CACHIX_CACHE_NAME` to its name.
- Repository secret `CACHIX_AUTH_TOKEN` to a write token for that cache.

Rerun the workflow. Pull requests read the cache without receiving its write
token. Main-branch and tag builds can upload; the Cachix post-build hook includes
the frontend's dependencies as well as the application.
Without a cache name, all checks still run using the standard NixOS cache.

Configure consuming machines with the cache's URL and public signing key:

```nix
nix.settings = {
  extra-substituters = [ "https://YOUR-CACHE.cachix.org" ];
  extra-trusted-public-keys = [ "YOUR-CACHE.cachix.org-1:PUBLIC-KEY" ];
};
```

Copy the actual public key from Cachix; no secret belongs in Nix source files.
GitHub source hosting alone does not supply a binary cache. Local Nix builds
reuse exact valid store outputs; an installed application may outlive its
garbage-collected build-time dependencies.

## Dashboard and tray

`ui` opens the dashboard in a desktop Window on Runic Desktop, which shows the
page with WebKitGTK 4.1 through GTK 3 and falls back to an installed browser
without it. The page is built into the package; nothing is downloaded at
application startup. `--no-open` serves the page without a Window and prints
its address (`RUNIC_APPLICATION_URL=...`); `--browser` serves it the same way
and opens it in the desktop's default browser with `xdg-open`. A served page
admits a tab only with the session's token. With `--browser`, it ends once its
last tab has been closed for a second (a reload reconnects within that) and no
operation runs, or when no tab connected within three minutes.

The package adds Nix Manager to the application menu with its own icon. Opening
it while the tray runs brings up the tray's dashboard instead of a second one.
When it was started without a terminal and cannot read the configuration, a
Window explains why.

On a computer without a configuration checkout, `ui` starts with a setup
assistant. It copies `/etc/nixos` into a new folder in the home folder (by
default `~/nixos-config`), adds a `flake.nix` with the computer's NixOS
configuration and a `home.nix` for a standalone Home Manager configuration,
starts Git, and pins nixpkgs to the revision the computer runs, so the first
build matches the running system. It falls back to the unstable branches when
Home Manager has no branch for the running release. It then evaluates both
configurations and only keeps the folder when they evaluate; files only an
administrator can read and channel imports such as `<home-manager/nixos>` are
pointed out first. The folder is remembered, and the window moves on to the
dashboard. When flakes are not enabled yet, the manager enables them for its own
`nix` commands, and the new configuration turns them on for good once applied.

The dashboard is organised around one flow: prepare, review, apply. Preparing
builds the selected Home Manager and NixOS configurations without privileges and
keeps the exact outputs. The Changes page shows the package differences from
`nix store diff-closures`, changed lock nodes (including transitive inputs), the
closure size, and warnings when the checkout or lock changed since the build, the
build targets another host, or the kernel changes. Applying activates exactly those
outputs, now or on next boot; the checkout is not evaluated again. Nothing is
switched without a review, except installs in one step (below).

The Software page installs packages and turns on features by editing the
checkout's Nix files, through the same flow. Discover searches an index of every
package in the locked nixpkgs (with overlays), built on first search and kept per
lock file, and offers about fifty curated features such as Steam, Docker, Flatpak,
an SSH server, Tailscale or fzf, each a small set of options with typed settings.
Discover opens with starting points, Gaming, Office and school, Photo and video
and Programming, each a set of features and apps added as one pending edit with
anything unwanted unticked. Below them are apps: popular ones first, then by category, with their
names, summaries and icons. Those details come from Flathub's public AppStream
catalogue, downloaded when Discover opens and again after a week, and matched to
nixpkgs packages by homepage and name; the apps are still installed from the
locked nixpkgs. Icons are downloaded by the application, kept, and served to the
page beside its own files, so the page itself never contacts Flathub. Settings can turn this off. Search lists
apps first, including packages built like graphical apps that Flathub does not
know, then other software.
Search can be limited to free software and can leave out broken packages. Results
show versions and licenses, and say whether a package is installed. `/` focuses
the search, and the arrow keys move between it and the results. Installed lists
the packages each file adds with their versions, and the packages modules add as
a result of settings. Changes are kept as pending edits until prepared, and the
Overview lists them beside the next generation:

- Nix evaluation supplies the meaning: which file defines an option or a package
  list, and what a value is now. A tree-sitter syntax tree locates the exact
  binding, so an edit changes only those lines and keeps comments and formatting.
  Computed values (`lib.mkDefault`, `let` bindings, conditionals) are refused
  rather than overwritten.
- Prepare copies the checkout, writes the edits into the copy, and evaluates the
  configuration with and without them. Each edit must have exactly its intended
  effect (the package appears in that file's list; the option has the value and is
  defined in that file) before anything is built. The review shows the line diffs
  next to their effects and the usual package differences.
- A package or feature can also go into a new file, such as
  `modules/home/programs/fzf.nix`. The file's path is added to the `imports` of a
  module the host already imports (by default the one that imports the most
  modules from the same folder), through the same syntax-tree edit, and
  verification checks the settings take effect from the new file. A feature gets a
  file of its own when its folder already holds several modules.
- Apply first writes the reviewed files, refusing if any of them changed since
  preparing (or a file to create appeared), then activates the reviewed outputs.
  Edited files must be tracked by Git, because flakes only see tracked files;
  created files are added with `git add --intent-to-add`.
- Install now (and Remove now) prepares and applies in one step, with progress
  in a dialog. It is offered only for installing and removing packages of your
  own Home Manager configuration (and allowing non-free software for them), on
  the computer the dashboard runs on, when no other edits are pending. Those
  change no kernel, boot loader or service. The prepared review is checked again
  before applying: if it builds the system or changes any other option, it waits
  on the Changes page instead. "Review it first" in the dialog does the same.
- If applying fails after something changed, the earlier system and home
  generations are activated again and the written files are put back. The
  pending edits and the review are kept. Each part goes back on its own, and
  anything that could not go back is named in the explanation.

The same draft is available from a terminal. Edits are checked against the
checkout before they are saved, and the dashboard shows them as pending edits:

```bash
nix-manager software search ripgrep --free  # --working leaves out broken packages
nix-manager software add btop fd            # the used package list with most packages
nix-manager software add --system htop --file modules/nixos/packages.nix
nix-manager software set programs.fzf.enable true --new-file modules/home/programs/fzf.nix
nix-manager software unset programs.zoxide.enable
nix-manager software pending --diff
nix-manager software drop 2                 # or clear
nix-manager software prepare                # builds a review, like Prepare
nix-manager software apply                  # writes and activates it; --boot, --dry-run
```

Values for `set` are JSON (`true`, `8080`, `["--height 40%"]`) or plain text.
New settings go to the file that defines the option; `--file` or `--new-file`
chooses one when the flake does not set it yet. `--new-file` imports the file from
its folder's imported module unless `--import-from` names another.

The other pages are Overview (active generations, the prepared next generation,
reboot notices and recent activity), Computer (the computer's name, date and
time, language, keyboard, login, sound, printing, Bluetooth, firewall and user
accounts, edited as pending edits like software), Updates (flake inputs and
package pins, each selectable before preparing), Restore points (generations,
rollback, cleanup and history), Activity (step outcomes and a filterable live
log with copy) and Settings. The Svelte 5 interface supports light, dark and
system themes.

Settings chooses how much the dashboard shows. Simple, the default for a new
state directory, gives pending edits one button: "Apply changes" when they can be
applied in one step, otherwise "Review and apply", which prepares them. Reviews
start with a plain summary; sizes, file diffs, flake inputs and the full package
list are one click away. Detailed shows every prepare, review and apply step.
A state directory with earlier operations starts detailed. Preferences are kept in
`preferences.json` in the state directory, where the tray's background tasks read
them too. Flake input updates and package-pin updates are distinct: refreshing the
lock does not refresh the three local package pins. After updating a pin, prepare
a home build to apply it.

The detected host is used by default. Settings can override it to build another
configuration from the same checkout on this machine; the dashboard then shows a
warning. It does not connect to another computer.

The Restore points page observes the actual Home Manager and system profile links,
marks their active store paths, and shows which entries the selected retention
count would remove. A rollback first prepares a review for the selected exact
generation, then uses the same reviewed-output activation path as a fresh build.
Retention can be set from 1–100 generations and is validated again by the
privileged system-cleanup entry point.

Preparing creates GC roots under the manager state directory and records the exact
Home Manager activation package and NixOS toplevel. Repository fingerprints cover
the lock file and working-tree state, so changed checkouts make old reviews visibly
stale. Selected input updates are resolved into a separate proposed lock file;
applying copies that exact reviewed lock atomically and refuses if the checkout
changed in the meantime. Discarding a review removes its manager-owned GC roots,
allowing a later garbage collection to reclaim the outputs.

`tray` shows a status icon (a StatusNotifierItem) with a menu, supported by
Plasma and other compatible Linux desktops; notifications go through the desktop
portal. Its menu opens the dashboard, checks updates, prepares all updates for
review, or quits. Preparing builds with every input updated, and the dashboard
shows the changes for review before anything is applied. Checks run ten seconds
after startup and then every six hours (`--interval MINUTES`, from 5 to 10080,
changes this). Notifications report new update sets, failed operations, and
completed changes. Clicking a notification opens the dashboard. Checks never
activate a configuration automatically.

With "Keep a history of changes" switched on (the default for new users), every
applied change commits exactly the files it wrote, with a message saying what it
did; the Restore points page lists the checkout's history. "Back up after each
change" also pushes when the branch has an upstream. Neither ever fails an apply.

The Restore points page measures how much a cleanup frees before it runs. With
"Clean up automatically" switched on, the tray removes all but the newest five
home generations and unused store paths at most once a week, on mains power
and when the computer is not busy. System generations are only removed on
request, since that needs a password.

With "Prepare updates in the background" switched on in Settings, the tray also
builds a review of new source updates after a check, while the computer is on
mains power and not busy, and notifies when it is ready. The notification can
apply it now or on the next boot. Applying a review on the next boot keeps its
home part; once the computer runs that system, the next tray or dashboard start
applies the home part from the same review. Package pins are never prepared in
the background.

To start the tray with graphical sessions, set in the NixOS configuration:

```nix
programs.nix-manager.tray = {
  enable = true;
  intervalMinutes = 360;
};
```

The tray uses the same checkout as the window; `flakePath = "%h/configuration"`
selects another one. Tray autostart defaults to disabled. After a system switch that enables it, the
user service starts with the next graphical session, or with
`systemctl --user start nix-manager-tray`.

### One process per user

Nix Manager runs as one per-user process. With `tray.enable`, the
`nix-manager-tray` user service runs `nix-manager tray`, windowless with the
status icon, and that process owns every operation.

- `nix-manager ui` hands its launch to the daemon over the single-instance
  socket in `XDG_RUNTIME_DIR`. The daemon opens its dashboard Window, or brings
  the open one forward. Closing the Window leaves a running operation running.
  Without a daemon, `ui` becomes that process for as long as its Window is open
  or an operation runs. Command lines reach it in the same way, and a tray that
  starts meanwhile waits for it to end. `--no-open`, `--browser`, or a checkout
  other than the running instance's run the dashboard on its own.
- Operation commands hand their request to the daemon when it runs for the same
  checkout, host and state directory. The daemon streams the operation back, so
  the terminal shows the same output, `--output json` included, and the command exits
  with the same status. Ctrl+C cancels the operation in the daemon; a command
  line that goes away, such as with a dropped SSH session, leaves it running
  to its end. A second operation is refused with the operation lock's message.
  Status, history, previews (`--dry-run`), help and completions run in the
  command's own process.
  Without a daemon, with `NIX_MANAGER_DAEMON=0`, or without `XDG_RUNTIME_DIR`,
  commands run in their own process. Over SSH, operations with a
  privileged step also run there, so that Polkit asks on the terminal and not
  on the desktop.
- The daemon checks for updates, prepares them and cleans up weekly on the
  schedule above. It applies the home part of an update applied on the next
  boot, and observes the system after every operation. When an operation that
  neither a Window nor a terminal shows ends, it sends a notification.

### Translations

The dashboard, the setup assistant and the tray speak English or German. They
follow the desktop's language (`LC_ALL`, `LC_MESSAGES`, then `LANG`) unless
Settings → Language chooses one. The command line stays in English, so its
output can be searched for and pasted into reports. Output from Nix itself is
shown as Nix wrote it.

One Runic Translations catalogue serves the page and the daemon, in
`runic/Frontend/translations`: keyed messages (`en.rmf2`, `de.rmf2`), the text
the backend writes for people, such as step labels, notes, errors and failure
explanations, together with the curated features and the Computer page's
settings (`stored/`), and the daemon's status icon, menu and notifications
(`tray/`). The page reads it through the Vite plugin as `m.*()`, in the
language the ViewModel says (`useMessageLocale`). Messages with markup, such
as `Detected as {#highlight}{$host}{/highlight}.`, render with
`LocalizedInline`; the page's own tags are declared in `runic.json`. The daemon
formats the same catalogue through the C# that Runic.Translations.Build
generates for it, `DashboardText` (`runic/Daemon/TrayText.cs`). The generated
catalogue reads a language only when it is first used. The tray follows the
dashboard's language setting while it runs.

The backend does not store or send filled-in English: its text for people is
a `DisplayText` (`runic/Domain/DisplayText.cs`), a text reference to a message
of `stored/` with its inputs, built with `DisplayText.Of("stored_<key>",
("name", value), ...)`, whose English comes from the catalogue. A value that
is itself a message, such as the step in "{$label} failed", is a nested
reference; text that is not a message, such as Nix's output, a path or a
package name, is `DisplayText.Plain`. Records keep the reference's JSON, so a
record written months ago is shown in the language of the day it is read; the
page shows it with `text()` from `runic/Frontend/src/lib/i18n.ts`, the tray
with `TrayText.Show`, and the command line prints its English. Records written
before text references hold English text, which every language shows as it
is. Give a message a new key when its meaning changes. `TranslationCoverageTests`
reads the backend's sources and fails when a message they name is missing or
named with other inputs than it has, or when a message has no German.

German uses the formal "Sie", „these quotes“ and the same word for the same
thing everywhere:

| English | German |
| --- | --- |
| pending edit, Pending | vorgemerkte Änderung, Vorgemerkt |
| prepare, prepared | vorbereiten, vorbereitet |
| apply, apply now, apply on next boot | anwenden, jetzt anwenden, beim nächsten Start anwenden |
| review | prüfen, Prüfung |
| discard | verwerfen |
| restore point | Wiederherstellungspunkt |
| go back (to a restore point) | zurückkehren |
| changes (the page) | Änderungen |
| update | Aktualisierung |
| Discover, Installed | Entdecken, Installiert |
| feature, app, package | Funktion, App, Paket |
| starting point | Startpunkt |
| your user, every user, just you | Ihr Benutzer, alle Benutzer, nur Sie |
| system | System |
| configuration, configuration folder | Konfiguration, Konfigurationsordner |
| home configuration | Benutzerkonfiguration |
| build (verb, noun) | bauen, Build |
| host, target host | Rechner, Zielrechner |
| tray | Kontrollleiste |
| operation, step, output | Vorgang, Schritt, Ausgabe |
| free, non-free software | freie, unfreie Software |
| clean up | aufräumen |
| disk space | Speicherplatz |

Names stay as they are: Nix, NixOS, nixpkgs, Flathub, Git, flake inputs.

## Execution and state

All selected configurations build once, before either activation, and the CLI's
`switch` and `boot` activate those build results directly instead of rebuilding.
Arguments after `--` are forwarded to `nix build`. Home activates before system;
activation is sequential, not transactional. Failure during the second activation
can leave home already switched. Cleanup runs only after all selected activations
succeed and retains three generations in each selected profile. Garbage
collection removes unreferenced store paths, which can require downloads or
rebuilds if needed again.

Steps run as argument vectors without a shell, with closed standard input and in
a new session without a controlling terminal. Their output is streamed to the
CLI and saved for the dashboard; Nix's structured log becomes readable lines and
progress. Interactive terminals show a compact task log of the last lines;
`--plain` disables dynamic presentation and `--verbose` retains successful task
output. `--output json` writes one `runic.commandline/1` response to stdout and
sends command output to stderr. Its payload is typed: `nix-manager.run/1` for
the root command (`kind` is `plan`, `operation`, `history` or `status`, with the
member of that name; `check` adds `updates`), `nix-manager.software-search/1` for
`software search` and `nix-manager.software-draft/1` for the other software
commands that change or list pending edits. A failed command's `fault.data`
holds the failed operation (`nix-manager.run/1`) or the exact error with its
explanation (`nix-manager.error/1`). Text written for people in these records,
such as step labels, notes, errors and explanations, is a Runic Translations
text reference: a JSON object with the message `key`, its `arguments` and the
English as `fallbackText` (transport version 2). Text that is not a message,
such as Nix's output, and records written before text references, are JSON
strings. The error's `message` is English.

Exit codes are Runic Command Line's: 0 on success, 2 for invalid arguments or
combinations, 4 after cancellation, 5 when the configuration or host cannot be
loaded, 10 when the command or its operation fails, and 70 for an unexpected
failure. A fault's code (`NM0002`, `NM0004`, `NM0005`, `NM0010`) carries the
same number, and its message is a fixed summary such as "The configuration
could not be loaded."; the exact error, which may name paths, is written before
it in human output and kept in the fault's data.
Cancelling a build terminates its process group. During a privileged step,
cancellation waits for that step to finish, then stops further
steps. Completed activation, lock changes or pin updates are not undone by
cancellation. Interrupted operations retain their logs.

System activation, system rollback and system cleanup go through the packaged
executable's `--privileged` entry point and its Polkit action
`org.nixos.nix-manager`. The policy declares each command and checks its
arguments by position; the helper then checks their meaning.
Its `activate-output` command accepts only `switch` or `boot` and a NixOS system
store path, sets the system profile to it and runs its `switch-to-configuration`.
`activate-generation` accepts only the number of an existing system generation
and activates it the same way, for going back after a failed apply. `rollback`
takes no arguments, and `gc` accepts only a retention count from 1 to 100.
The dashboard and tray run as the ordinary user. There is no general
shell-command entry point or remote listening option.

History, logs, update reports and the operation lock live under
`$XDG_STATE_HOME/nix-manager` (default `~/.local/state/nix-manager`). The dashboard
lists the latest 50 operations and displays the last 128,000 characters
of the selected log. Individual stored logs are capped at 16 MiB; older operation
files remain on disk. The lock is a `flock` on `operation.lock`, which the OS
releases even if a manager process crashes. It covers CLI, dashboard, tray, and
different checkout selections using that state directory. It does not lock out
native Nix commands or other users.
`NIX_MANAGER_STATE_DIR` can select an isolated state directory for testing. `NIX_MANAGER_SYSFS` points hardware detection at a fake `/sys` tree, and
`NIX_MANAGER_SETUP_SOURCE` points the setup assistant at another classic configuration.
Pending software edits are stored in `draft.json` and the package index under
`catalog/`, keyed by the lock file; building an index evaluates every package and
needs several gigabytes of memory for about a quarter of a minute. Flathub's app
catalogue and icons are kept under `apps/`.

Operation records include the status and timestamps of each planned step. The
dashboard's ViewModels are views of the process's operation service: its events
reach the page in order, and closing the Window leaves the service and a running
operation as they are. The dashboard also watches the state directory, so
operations started by a command line in another process, new update reports,
reviews, pending edits and preferences changed elsewhere appear without a
refresh. It observes the computer again when the Window opens, after each
operation and every 30 seconds while it is open.

Update checks write a proposed flake lock to a temporary directory using Nix's
`--output-lock-file`, then compare input revisions. Stable GitHub releases are
compared against the local package pins. A pin with `"channel": "nightly"`
follows the newest `vX.Y.Z-nightly.YYYYMMDD.N` or stable release instead; a
nightly sorts before the stable release with the same `X.Y.Z`. A package may already be newer in locked
nixpkgs; its updater detects that and skips an unnecessary pin update. Partial
check failures remain visible and make `check` exit nonzero. Cached results show
their check time and can become stale until the next check.

Package updaters retain the locked nixpkgs recipe, compare it with an upstream
recipe from one resolved commit, and only save a pin after the candidate builds.
Hash discovery accepts a mismatch for the exact expected dependency derivation.
Pins are replaced by a same-filesystem rename; an intervening manual pin edit
causes the update to fail instead of overwriting it. `--dry-run` package updates
may fetch metadata and recipe text, but do not fetch archives, build or save pins.

## Development and checks

```sh
nix develop
cd runic
dotnet tool restore                    # the Runic and Runic Translations tools
(cd Frontend && bun install --frozen-lockfile)
dotnet build
dotnet run --project NixManager.Tests -- --progress off
```

The shell supplies the .NET SDK, Bun and Node.js for `runic/`, GTK and WebKitGTK
for the dashboard Window, Chromium for headless browser checks of a served page,
Python and D-Bus for `test/`, and tree-sitter's Nix grammar
(`NIX_MANAGER_NIX_PARSER`, `NIX_MANAGER_TREE_SITTER`), which the program and its
tests read Nix files with. `dotnet build` in `runic/` builds `NixManager.slnx`:
the application, its frontend, and the tests. The frontend in `runic/Frontend`
uses Svelte 5, Vite, Tailwind CSS 4 and bits-ui, with no external asset requests;
`bun run build` builds it and runs `svelte-check`, `bun run typecheck` runs
`svelte-check` alone, and `bun run dev` starts Vite. The page's bridge to its
ViewModels is generated in `runic/Frontend/src/generated/`.

The frontend's dependencies are locked by `runic/Frontend/bun.lock`. The Nix
build installs them as a fixed-output derivation whose hash differs per system,
because Bun installs only the system's optional packages; both hashes are in
`runic/package.nix` (`frontend.hash`), and the build fails when they are out of
date. After changing the frontend's dependencies, update `bun.lock` with
`bun install`, then both hashes: `nix build .#default.frontendNodeModules`
reports the current system's when it is out of date, and
`nix build .#default.frontendNodeModules.fetchedFor.aarch64-linux` computes the
aarch64-linux one on any system.

NuGet packages are restored in locked mode. Each runtime needs its own lock
file because nixpkgs restores with `--runtime` for the system's runtime
identifier alone, and NuGet's locked mode rejects a lock file that records other
runtimes. NativeAOT's restore adds the compiler packages, so the application
also has an AOT lock file per runtime (`packages.aot.lock.json`,
`packages.linux-arm64.aot.lock.json`); the Nix build publishes only
`NixManager.csproj` with NativeAOT, and the tests run under the JIT and keep
theirs. Regenerate all of them after changing package references, in `runic/`:

```sh
dotnet restore NixManager.slnx --force-evaluate
dotnet restore NixManager.csproj --force-evaluate -p:PublishAot=true
dotnet restore NixManager.slnx --force-evaluate -r linux-arm64
dotnet restore NixManager.csproj --force-evaluate -r linux-arm64 -p:PublishAot=true
```

The package installs its shell completions itself. Outside the package, the
command line generates them from its catalog:

```sh
nix-manager completion bash > ~/.local/share/bash-completion/completions/nix-manager
nix-manager completion zsh > ~/.local/share/zsh/site-functions/_nix-manager
nix-manager completion fish > ~/.config/fish/completions/nix-manager.fish
```

`nix build .` builds the package in the sandbox and runs the .NET unit tests
(TUnit). The tests mock Nix commands and GitHub responses: they never activate a
machine or change real pins. `nix flake check` runs every check:

- `nix-manager`: the package, built and unit-tested.
- `cli`: the Python CLI regression suite (`test/test-manager.py`) against the
  packaged executable, with mocked build tools.
- `daemon`: the daemon as processes (`test/test-daemon.py`): a daemon or a
  dashboard, and command lines that hand it their operations, with mocked build
  tools and the dashboard served instead of shown.
- `module`: the NixOS module, including runtime host configuration that does not
  alter the application derivation, and what it links into the system path.
- `formatting`: Nix formatting.

### Desktop and screen-reader VM tests

Slower NixOS VM tests in `nix/lab` run outside `nix flake check`. They
need KVM:

```sh
nix build .#desktop-a11y   # GNOME and Orca: tab order, typing, and what Orca says
nix build .#speech-audio   # Orca's real speech, recorded and heard by whisper.cpp
nix build .#desktop-apply  # the dashboard and software apply for real, a reboot, cleanup
nix build .#webkit-attribute-run # the WebKitGTK attribute-run abort in both WebKitGTK flavours
```

`desktop-a11y` checks the installed command, the policy (pkexec maps the
program to the action and runs the privileged helper as root), the desktop
entry and the bash completion. It opens the setup assistant without a checkout:
Tab reaches the folder entry and "Create my configuration", Orca names them, and
typing reaches the entry. Then, with a seeded `~/my-flakes` and a registered
host, it walks the dashboard's Tab cycle without Orca and with it. The
expectations are about structure, so page changes keep the test valid: the skip
link comes first, the navigation's buttons (read from the page) follow in order,
at most one stop has no name (G-17, the stop outside the web view; not seen with
SDK 0.8.0-preview.3), and Orca's walk has the same stops and says their names.
The test then opens Settings by keyboard. In `checklist.txt` it records what
Orca says for headings (H), lists (L), the theme radio group and its arrow keys,
and how a heading's size changes with Ctrl and + (G-18) and with GNOME's large
text. Only a web process crash fails those steps. The AT-SPI application is
`nix-manager` (Chromium's is `chrom…`), and its window class is `nix-manager`.
`speech-audio` also hears a Tab stop in the dashboard through espeak-ng.

WebKitGTK 2.54.1 aborts its web process when Orca asks for the text attributes
of a list item whose text is only the U+FFFC of an embedded button and whose
last node has no box, such as whitespace or the comment anchors Svelte leaves.
A `CrashOnOverflow` in `textAttributesWithUTF8Offset` indexes its
UTF-16-to-UTF-8 offset table out of bounds, and the Window freezes. GTK 4's
webkitgtk-6.0 aborts the same way, and WebKit main still has the unchecked
index. The dashboard's navigation items are `display: contents`, which WebKitGTK
answers without that computation, and each gives its position with
`aria-setsize` and `aria-posinset`, so Orca says "list with 8 items" when Tab
enters the navigation (`runic/Frontend/src/App.svelte`).
`nix build .#webkit-attribute-run` opens the minimal pages of
`nix/lab/webkit-attribute-run` in both WebKitGTK flavours and records which
abort; `REPORT.md` there is the upstream report. The VM test counts the web
process's core dumps after every Tab and names the press. It also checks that
Orca says the list's size, reads the new number of restore points to keep, and
hears the Window announce a running operation's step and its end.

WebKitGTK sends no AT-SPI event when the text of a live region changes, so Orca
announces no live region in the Window (Chromium does, with the same page). The
page therefore has the Window announce what its live regions show, a running
operation's step and each toast, through Runic Desktop's announcements
(`Object:Announcement`); in a browser the live region speaks instead. The
restore points to keep are a spin button, whose value Orca reads as it
changes; its "Keep more" and "Keep fewer" buttons are for the pointer and leave
focus on it.

Orca's speech is written to a text transcript, so the tests compare exactly
what a screen-reader user hears. `desktop-apply` boots through systemd-boot
with a configuration checkout whose systems are prebuilt on the host through
`lib.lab`, so the offline VM builds nothing. Its disk image takes about 9 GB.

`nix run .#lab -- start` boots the `desktop-a11y` VM in the background, and
further `nix run .#lab --` commands press keys, type, take screenshots, print the
accessibility tree or Orca's speech, copy files in and out, and run commands
inside it. `nix run .#lab-apply` does the same with the `desktop-apply` VM and
alice's checkout. Only one lab runs at a time. `nix run .#lab`
lists them. Nothing opens on the host desktop.

### Hyprland on the GPU, without a VM

The VMs have no GPU render node, so Hyprland cannot start in them.
`nix run .#hyprland-sandbox` runs Hyprland and Waybar (with its tray) headless
on the host GPU instead, rootless, in a bubblewrap sandbox: its own PID, mount,
network, IPC and user namespaces, a private runtime directory, HOME and D-Bus
session bus, and no host Wayland, X11 or D-Bus sockets. Only
`/dev/dri/renderD128` is passed in, never a card node, so nothing takes DRM
master or touches the real monitors. It starts in about five seconds and
leaves no processes behind: everything ends with the sandbox's PID namespace,
also on `--timeout` or when the sandbox is killed.

```sh
nix run .#hyprland-sandbox                       # demo: a CopyQ tray icon, its menu, typing
nix run .#hyprland-sandbox -- --out shots -- sh -c 'my-tray-app & sandbox-wait-sni && sandbox-shot tray.png'
nix run .#hyprland-sandbox -- --help
```

The command runs inside with `WAYLAND_DISPLAY`, `HYPRLAND_INSTANCE_SIGNATURE`
and the private bus set, and helpers on `PATH`: `sandbox-shot`,
`sandbox-pointer` (a virtual pointer that stays connected, so GTK clients such
as Waybar receive clicks), `sandbox-wait-sni`, `sandbox-sni-items`,
`sandbox-wait-client`, plus `hyprctl`, `wtype` and `grim`. Its exit status is
returned. Screenshots and logs go to `--out`. Store paths are visible; other
host paths only with `--bind` or `--ro-bind`.

Aquamarine's headless backend has no allocator of its own and its DRM backend
needs a KMS card node, so Hyprland runs nested on its Wayland backend inside a
headless labwc, which renders on the render node without a seat. Hyprland's
own output is a headless one, rendered with OpenGL on the same GPU. Mesa comes
from this flake's nixpkgs.

## Layout

The application is in `runic/`:

- `Program.cs`: the entry point; the privileged helper runs first, then the
  command line.
- `Cli/`: the command line on Runic Command Line, the `software` commands, and
  terminal presentation.
- `Configuration/`, `Domain/`: checkout and host selection, requests, validation
  and command plans.
- `Execution/`, `Privileged/`: step processes, privileged steps through Polkit,
  and the `--privileged` helper that runs as root.
- `Operations/`: the operation service, the shared operation lock, history and
  logs, preferences, background tasks and hardware detection.
- `Observation/`, `Reviews/`: repository and system observations, generations,
  cleanup estimates, prepared reviews, and the change history.
- `Edits/`, `Nix/`: pending edits and source edits, readers for Nix's outputs and
  progress, and the tree-sitter syntax tree (`Nix/Syntax/`).
- `Software/`, `Updates/`, `Explain/`, `Setup/`: the package index, Flathub's
  app catalogue and curated features; update checks, package pins and updaters;
  failure explanations; the setup assistant.
- `Daemon/`: the per-user daemon, its instance protocol, the status icon and
  notifications, and the client that routes command lines to it.
- `Dashboard/`, `ViewModels/`, `BackendServices.cs`: the dashboard Window, the
  ViewModels its page binds, and the backend's service registration.
- `Frontend/`: the Svelte dashboard and setup assistant pages, their generated
  bridge, components and translations.
- `NixManager.Tests/`: the unit tests and their fixtures.
- `package.nix`, `NixManager.csproj`, `NixManager.slnx` and the lock files: the
  Nix package, the project with its Polkit action, and NuGet's locked restore.

`test/` has the CLI and daemon regression suites and the Nix error output the
explanation tests read; `nix/` has the VM tests, the lab and the Hyprland
sandbox.

Planned work, aimed at people who do not know Nix, is in [ROADMAP.md](ROADMAP.md).
