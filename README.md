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
`data/hosts.nix` from the selected configuration checkout. An explicit
`NIX_MANAGER_HOSTS` environment variable can point to another JSON registry.
New login sessions pick up changed system environment variables.

The application retains its own locked nixpkgs and Home Manager inputs. Do not
set `inputs.nix-manager.inputs.nixpkgs.follows = "nixpkgs"` if you want system
dependency updates to leave the application build unchanged. Consume
`inputs.nix-manager.packages.${system}.default` directly instead of rebuilding
its recipe with the consumer's package set. Host metadata is runtime
configuration, so changing it leaves the application store path unchanged.

The manager still expects the selected configuration's `data/hosts.nix` layout
when discovering a checkout automatically. The optional T3 Code, OpenCode and
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

The dashboard provides host and scope selection, build/activation previews,
update results, package-pin updates, rollback and garbage collection, operation
history, durable per-step outcomes, live log output and log export. Its Svelte 5
interface uses local shadcn-svelte components and supports light, dark, and system
themes. Flake input updates and package-pin updates are distinct: refreshing the
lock does not automatically refresh the
three local package pins. After updating a pin, use a home switch to apply it.
Host selection chooses a configuration to build or apply on the local machine;
it does not connect to another computer.

The Generations page observes the actual Home Manager and system profile links,
marks their active store paths, and shows which entries the selected retention
count would remove. A rollback first creates a review for the selected exact
generation, then uses the same reviewed-output activation path as a fresh build.
Retention can be set from 1–100 generations and is validated again by the
privileged system-cleanup entry point.

“Build for review” creates GC roots under the manager state directory and records
the exact Home Manager activation package and NixOS toplevel. Where Nix can provide
them, the review includes closure size and `nix store diff-closures` package changes.
Applying a review sets the matching profile and activates those retained paths; it
does not reevaluate the checkout. Repository fingerprints cover the lock file and
working-tree state, so changed checkouts make old reviews visibly stale. Removing
a review removes its manager-owned GC roots, allowing a later garbage collection
to reclaim the outputs.

Input rows can be selected individually. Nix resolves those inputs into a separate
proposed lock file, and the UI displays every changed lock node, including transitive
changes. Applying copies that exact reviewed lock atomically and refuses if the
checkout changed in the meantime.

`tray` registers a StatusNotifierItem and D-Bus menu, supported by Plasma and
other compatible Linux desktops. Its menu opens the dashboard, checks updates,
updates inputs and switches, or quits. The update shortcut starts an explicit
update-and-switch operation immediately and opens its dashboard; system changes
still require Polkit authentication. Checks run ten seconds after startup and
then every six hours (`--interval MINUTES` changes this). Notifications report
new update sets, failed operations, and completed changes. Clicking a notification
opens the dashboard. Checks never activate a configuration automatically.

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

All selected configurations build before either activation. Home activates
before system; activation is sequential, not transactional. Failure during the
second activation can leave home already switched. Cleanup runs only after all
selected activations succeed and retains three generations in each selected
profile. Garbage collection removes unreferenced store paths, which can require
downloads or rebuilds if needed again.

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
`NIX_MANAGER_STATE_DIR` can select an isolated state directory for testing.

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
