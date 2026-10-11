# Nix Manager

The manager's CLI, dashboard and Linux tray use one Bun + Effect application.
The CLI is defined with `@effect/cli`; its generated help,
version output, and shell completions stay in sync with the accepted options.
Existing action and modifier syntax is preserved. Run `nix-manager --help` for
the complete command reference.

```sh
nix run github:ViktorJannicke/nix-manager -- --flake /path/to/configuration ui
nix run . -- --flake /path/to/configuration --dry-run update cleanup switch
nix run . -- --flake /path/to/configuration check --json
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

The module installs the application, its Polkit policy and optional tray service.
It writes the host registry to `/etc/nix-manager/hosts.json` and sets
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

## CI and binary cache

GitHub Actions evaluates both Linux architectures and builds/tests x86_64-linux
using the committed lock file. It also builds the `npm-deps` output explicitly,
so the build-time dependency cache can be uploaded even when the application is
already available from a cache. Cachix support is optional and initially disabled.

To enable it, create or choose a public Cachix cache, then set:

- Repository variable `CACHIX_CACHE_NAME` to its name.
- Repository secret `CACHIX_AUTH_TOKEN` to a write token for that cache.

Rerun the workflow. Pull requests read the cache without receiving its write
token. Main-branch and tag builds can upload; the Cachix post-build hook includes
the npm dependency cache and native WebUI library as well as the application.
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

`ui` opens a desktop window using [WebUI](https://webui.me), loaded directly
through [Bun FFI](https://bun.sh/docs/runtime/ffi). The Nix derivation builds a
pinned WebUI native library; nothing is downloaded at application startup.
An installed browser is required. `--browser` uses the desktop's default browser;
`--no-open` prints the local session link for manual use.

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
locked nixpkgs. Icons are fetched through the dashboard server and kept, so the
window itself never contacts Flathub. Settings can turn this off. Search lists
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
reboot notices and recent activity), Updates (flake inputs and package pins, each
selectable before preparing), Restore points (generations, rollback, cleanup and history), Activity (step
outcomes and a filterable live log with copy and download) and Settings. The
Svelte 5 interface supports light, dark and system themes.

Settings chooses how much the dashboard shows. Simple, the default for a new
state directory, gives pending edits one button: "Apply changes" when they can be
applied in one step, otherwise "Review and apply", which prepares them. Reviews
start with a plain summary; sizes, file diffs, flake inputs and the full package
list are one click away. Detailed shows every prepare, review and apply step.
A state directory with earlier operations starts detailed. Preferences are kept in
`preferences.json` in the state directory, because each launch serves the
dashboard from a new port and browser storage does not carry over. Flake input updates and
package-pin updates are distinct: refreshing the lock does not refresh the three
local package pins. After updating a pin, prepare a home build to apply it.

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

`tray` registers a StatusNotifierItem and D-Bus menu, supported by Plasma and
other compatible Linux desktops. Its menu opens the dashboard, checks updates,
prepares all updates for review, or quits. Preparing builds with every input
updated and opens the dashboard, where the changes are reviewed before anything
is applied. Checks run ten seconds after startup and
then every six hours (`--interval MINUTES` changes this). Notifications report
new update sets, failed operations, and completed changes. Clicking a notification
opens the dashboard. Checks never activate a configuration automatically.

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
  flakePath = "%h/my-flakes";
  intervalMinutes = 360;
};
```

Tray autostart defaults to disabled. After a system switch that enables it, the
user service starts with the next graphical session, or with
`systemctl --user start nix-manager-tray`. Only one tray instance per user can
register. Restarting Plasma's tray host causes the item to register again.

Closing the last standalone dashboard exits its backend after a one-second
reload grace period. An active operation finishes before the backend exits.
Hidden or minimized dashboards remain connected without relying on UI polling.
Tray mode and `--no-open` stay resident until quit or interrupted.

## Execution and state

All selected configurations build once, before either activation, and the CLI's
`switch` and `boot` activate those build results directly instead of rebuilding.
Arguments after `--` are forwarded to `nix build`. Home activates before system;
activation is sequential, not transactional. Failure during the second activation
can leave home already switched. Cleanup runs only after all selected activations
succeed and retains three generations in each selected profile. Garbage
collection removes unreferenced store paths, which can require downloads or
rebuilds if needed again.

Effect services isolate subprocesses and network access, and scoped finalizers
clean temporary workspaces. Subprocess output is streamed to the CLI and saved
for the dashboard. Interactive terminals use Clack for a compact task log;
`--plain` disables dynamic presentation and `--verbose` retains successful task
output. `--json` keeps stdout machine-readable and sends command output to stderr.
Cancelling a build terminates its process group. During a privileged step,
cancellation waits for that step to finish, then stops further
steps. Completed activation, lock changes or pin updates are not undone by
cancellation. Interrupted operations retain their logs.

System activation, system rollback and system cleanup go through the immutable
packaged executable's `--privileged` entry point and the existing Polkit policy.
Its `activate-output` command accepts only a NixOS system store path, sets the
system profile to it and runs its `switch-to-configuration`.
`activate-generation` accepts only the number of an existing system generation
and activates it the same way, for going back after a failed apply. `rollback`
takes no arguments, and `gc` accepts only a retention count from 1 to 100.
The dashboard and tray run as the ordinary user. The HTTP server binds only to
`127.0.0.1` on a random port and checks a per-session bearer token, Host and Origin.
The token is passed in the initial URL fragment and then kept in session storage.
The native WebUI server has an empty temporary document root. There is no general
shell-command endpoint or remote listening option.

History, logs, update reports and the operation lock live under
`$XDG_STATE_HOME/nix-manager` (default `~/.local/state/nix-manager`). The dashboard
lists the latest 50 operations and displays/exports the last 128,000 characters
of the selected log. Individual stored logs are capped at 16 MiB; older operation
files remain on disk. The OS releases the shared advisory lock even if a manager
process crashes. It covers CLI, dashboard, tray, and different checkout selections
using that state directory. It does not lock out native Nix commands or other users.
`NIX_MANAGER_STATE_DIR` can select an isolated state directory for testing. `NIX_MANAGER_SYSFS` points hardware detection at a fake `/sys` tree, and
`NIX_MANAGER_SETUP_SOURCE` points the setup assistant at another classic configuration.
Pending software edits are stored in `draft.json` and the package index under
`catalog/`, keyed by the lock file; building an index evaluates every package and
needs several gigabytes of memory for about a quarter of a minute. Flathub's app
catalogue and icons are kept under `apps/`.

New operation records include the status and timestamps of each planned step.
Older records without step data remain readable and appear without inferred
outcomes. The manager publishes ordered operation and output events to
authenticated dashboard sessions over a same-origin WebSocket. Reconnecting
clients refresh a complete snapshot and log tail before consuming more events;
a periodic snapshot also covers missed events. This live stream is process-local.
Another manager process is represented by the shared lock rather than claimed
live state.

Update checks write a proposed flake lock to a temporary directory using Nix's
`--output-lock-file`, then compare input revisions. Stable GitHub releases are
compared against the local package pins. A package may already be newer in locked
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
bun install --frozen-lockfile --ignore-scripts
bun run typecheck
bun test
bun run build
bun run start -- --flake /path/to/configuration --host desktop ui --browser
```

The shell supplies Bun, Node/npm, D-Bus, and the pinned WebUI library. The UI uses
Svelte 5, Vite, Tailwind CSS 4, and project-owned shadcn-svelte component source,
with no external asset requests. `bun build.ts` builds the browser app with Vite
and embeds its explicit HTML/CSS/JS asset map in the Bun entry point. Effect 3,
`@effect/cli`, and Clack are pinned in both dependency lock files.

Generated completions are available for the shells supported by `@effect/cli`:

```sh
nix-manager --completions bash > ~/.local/share/bash-completion/completions/nix-manager
nix-manager --completions zsh > ~/.local/share/zsh/site-functions/_nix-manager
nix-manager --completions fish > ~/.config/fish/completions/nix-manager.fish
```

`bun.lock` serves Bun development. `package-lock.json` lets Nix use its fixed-output
`fetchNpmDeps` cache for a network-free application build. After changing
dependencies, regenerate both lock files (`npm install --package-lock-only
--ignore-scripts`, then `bun install --ignore-scripts`) and update `npmDepsHash`.
Package installation scripts are disabled. The native library revision and source
hash are pinned separately in `webui.nix`.

`nix build .` runs type checking and Bun tests, including a native WebUI
ABI smoke test and a D-Bus tray test against a private daemon. The tests mock Nix
commands and GitHub responses: they never activate a machine or change real pins.
`nix build .#checks.x86_64-linux.cli` also runs the preserved Python CLI regression
suite against the packaged Bun entry point. `nix flake check` additionally checks
Nix formatting and the NixOS module, including runtime host configuration that
does not alter the application derivation. Updater and fixed-output hash tests
run as part of the package's Bun test suite.

Core modules:

- `domain.ts`, `contracts.ts`, `cli.ts`, `config.ts`: schemas, browser-safe state,
  Effect CLI definitions, validation, command plans and selection.
- `process.ts`, `updates.ts`: Effect services, process lifecycle, update checks and pins.
- `advanced.ts`: repository observations, generations, reviewed outputs, and exact lock proposals.
- `manager.ts`: shared operation lock, execution, step outcomes, events, history,
  and cancellation.
- `server.ts`, `window.ts`, `tray.ts`: authenticated UI transport and desktop adapters.
- `terminal.ts`: Clack and plain terminal presentation.
- `ui/`: Svelte dashboard, browser transport and shadcn-svelte components.

Planned work, aimed at people who do not know Nix, is in [ROADMAP.md](ROADMAP.md).
