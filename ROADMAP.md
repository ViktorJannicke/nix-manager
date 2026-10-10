# Roadmap

Nix Manager should be usable by someone who has never written Nix: install an
app, change a setting, keep the computer updated and go back when something
breaks, without reading Nix errors or knowing what a flake is. The review flow
stays available to everyone; experts lose nothing.

Phases are in the order we intend to work on them. Each item says what is done
when it is finished. Tick items off in the commit that completes them.

## 1. Remove the dead ends

- [x] **Errors in plain words.** Recognise common evaluation and build failures
  and show one sentence and, where possible, a button that fixes it as a
  pending edit. The original output stays one click away. First cases:
  - unfree package refused: "Steam is not free software" with "Allow it" (writes
    `nixpkgs.config.allowUnfreePredicate` or `allowUnfree`, scope-aware);
  - option does not exist or was renamed;
  - package removed or renamed, with the replacement nixpkgs names;
  - broken package, insecure package, collision between two packages;
  - out of disk space during a build.
  Done when each case has a test with real Nix output and the review page and
  CLI show the explanation.
  Follow-up: when the flake sets `nixpkgs.config` itself (for example from a
  policy file), "Allow it" should edit that place instead of adding a module
  definition that loses to it. Today the review explains the conflict.
- [x] **Unfree up front.** Discover says before installing that an app is not free
  software and offers to allow it in the same pending edit, so prepare does not
  fail first.
- [x] **Undo.** A visible "Undo last change" that rolls back to the previous
  generation (and offers to revert the edited files), described as going back
  to how the computer was at a given time.
  Follow-up: the CLI has no `undo` command yet; a file edited since the change
  blocks Undo (the Generations page still works then).

## 2. A settings page for the computer

- [x] **Computer page.** Forms for computer name, time zone, language and
  formats, keyboard layout, users (add, remove, change password hint, admin),
  automatic login, printing and scanning, Bluetooth, sound, firewall. Built on
  the curated-experience machinery (typed fields, option docs, edits through
  review). Done when each setting round-trips through prepare on a scratch
  checkout.
  Follow-ups: settings defined in flake.nix or in a module with its own options
  are shown read-only; a user defined across several files cannot be removed
  from the page; port lists show ports that features open too; passwords are
  set after applying, never written into the configuration.
- [x] **Hardware suggestions.** Suggest features from the hardware found: power
  profiles on laptops, the NVIDIA driver setup, Bluetooth, printers, fingerprint
  readers.
  Follow-ups: hardware is read from `/sys` once when the dashboard starts, and
  only for the computer it runs on; the NVIDIA feature always uses the open
  kernel module (GeForce 16 series and newer) because `hardware.nvidia.open`
  cannot be read while the driver is off; network printers are not detected.

## 3. One-click changes

- [ ] **Install in one step.** For edits that only affect the home configuration
  (or that change no kernel, boot loader or service the user is running),
  "Install" prepares, verifies and applies in one action with progress. The
  review stays reachable from the progress view.
- [ ] **Automatic rollback.** If activation fails, return to the previous
  generation and restore the files, then explain what happened.
- [ ] **Setting: simple or detailed.** Simple by default for new users: one
  button and a plain summary. Detailed shows the current prepare, review and
  apply steps.

## 4. Look after the computer in the background

The tray already checks for updates read-only and sends notifications.

- [ ] **Prepared updates.** Optionally update sources and build the review in the
  background (on power and idle), then notify "Updates are ready".
- [ ] **Apply on next boot or shutdown** from the notification.
- [ ] **Free up space** in plain terms: how much removing old restore points
  frees and how many are kept; optional automatic cleanup.
- [ ] **History without Git.** Commit after each apply with a readable message
  ("Added Steam, turned on Bluetooth") and show that history; optional backup
  of the configuration to a Git remote.

## 5. Words people already know

- [ ] **Plain vocabulary.** "Generation" becomes restore point or system
  version, flake inputs become software sources, closure size becomes download
  and disk size. Technical names stay visible in detailed mode and tooltips.
- [ ] **Review summary line.** Every review starts with one sentence: what is
  added, updated and removed, download size, whether a restart is needed.
- [ ] **Time estimates** for builds and downloads based on earlier runs.

## 6. Discover as an app store

- [ ] **Apps first.** Separate graphical apps from command-line tools. Needs
  research: nixpkgs has no complete AppStream catalogue; options are matching
  Flathub/AppStream metadata by name and homepage, or reading `.desktop` files
  and icons from substituted outputs.
- [ ] **Categories and real icons** from that metadata.
- [ ] **Bundles.** Starting points such as Gaming, Office and school, Photo and
  video, Programming: sets of curated features and apps staged together.

## 7. Getting started

- [ ] **No host registry needed.** Today a checkout needs `data/hosts.nix` or
  `programs.nix-manager.hosts`. Detect `nixosConfigurations` and
  `homeConfigurations` (and NixOS-integrated Home Manager) directly and only ask
  when it is ambiguous.
- [ ] **Setup assistant.** On a system without a flake: turn `/etc/nixos` into a
  flake in a user-owned checkout, start Git, check that flakes are enabled, and
  explain prepare and apply in three sentences.
- [ ] **Starts like an app.** Menu entry and icon from the NixOS module, opening
  the dashboard without a terminal.

## 8. For everyone

- [ ] **Accessibility audit.** Screen reader labels, live regions for build
  progress, focus order in dialogs, contrast in both themes, larger text,
  reduced motion.
- [ ] **Translations.** Extract interface strings; German first.

## Open questions

- Which edits are safe enough for one-click installs, and how is that shown?
- Where does app metadata come from, and can it be locked with nixpkgs?
- Should automatic updates be on by default once the tray is enabled?
