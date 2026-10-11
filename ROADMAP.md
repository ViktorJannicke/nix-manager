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

- [x] **Install in one step.** For edits that only affect the home configuration
  (or that change no kernel, boot loader or service the user is running),
  "Install" prepares, verifies and applies in one action with progress. The
  review stays reachable from the progress view.
  Decided: only installing and removing home packages (and allowing non-free
  software for them) qualifies; settings, even home ones, can restart user
  services and keep the review. The rule lives in `oneStepProblem` and is
  checked again against the prepared review before applying.
  Follow-ups: curated home features and the CLI have no one-step path yet; Undo
  from the dialog prepares a review rather than applying in one step; a failed
  activation now goes back (next item).
- [x] **Automatic rollback.** If activation fails, return to the previous
  generation and restore the files, then explain what happened.
  Applies to every apply from a review, not only one-step installs. Each part
  goes back on its own, so files are put back even when the earlier generation
  cannot be activated; the explanation names what is left.
  Follow-ups: going back on the system uses the new privileged
  `activate-generation`, which is covered by tests but has not run as root on a
  real machine yet; with no earlier home generation the new one stays selected.
- [x] **Setting: simple or detailed.** Simple by default for new users: one
  button and a plain summary. Detailed shows the current prepare, review and
  apply steps.
  Decided: "new" means a state directory without earlier operations, so existing
  users keep the detailed view. Edits that cannot be applied in one step keep
  their review in simple mode too ("Review and apply"); the review then starts
  with the plain summary and hides the rest behind "Show all details".
  Preferences (this and the theme) moved from browser storage to the state
  directory, as each launch uses a new port.
  Follow-ups: the Updates and Generations pages look the same in both modes;
  option names in the review are still technical (Phase 5).

## 4. Look after the computer in the background

The tray already checks for updates read-only and sends notifications.

- [x] **Prepared updates.** Optionally update sources and build the review in the
  background (on power and idle), then notify "Updates are ready".
  Decided: off by default, switched on in Settings. Only source updates are
  prepared, never package pins, which change files in the checkout. "Idle"
  means the load of the last minute is below half the processors; "on power"
  means no system battery or mains online. The same set of updates is
  prepared once. The notification uses the plain review summary.
  Follow-ups: it needs the tray to run; the dashboard does not show that a
  review came from the background.
- [x] **Apply on next boot or shutdown** from the notification.
  "Apply now" and, when the system changes, "Apply on next boot". A boot apply
  keeps the review's home part and applies it by itself once the computer runs
  the new system, so apps and system change together.
  Decided: no separate shutdown action; boot plus the home part after it covers
  it without running Nix while the computer shuts down.
  Follow-ups: the home part is applied when the tray or dashboard next starts,
  not by a boot service.
- [x] **Free up space** in plain terms: how much removing old restore points
  frees and how many are kept; optional automatic cleanup.
  The clean-up section measures, before anything is removed, the store paths
  only the removed generations use (`nix path-info`), so the number is what
  goes at least; other unused paths are collected on top. Automatic cleanup
  is off by default; once a week, on mains power and not busy, it keeps the
  newest five home generations and collects garbage, then notifies how much
  was freed.
  Decided: automatic cleanup never touches system generations, because that
  needs the password and boot entries.
  Follow-up: the number of kept generations for automatic cleanup is fixed.
- [x] **History without Git.** Commit after each apply with a readable message
  ("Added Steam, turned on Bluetooth") and show that history; optional backup
  of the configuration to a Git remote.
  After a successful apply, exactly the files it wrote are committed (`git
  commit --only`), so other work in the checkout stays uncommitted. The message
  uses the same words as the undo point, marked with an `Applied-with: Nix
  Manager` trailer. The Generations page lists the history. Backup pushes after
  each commit when the branch has an upstream.
  Decided: on by default for new users, off for existing ones, like simple
  mode. Without a Git identity, commits are made as "Nix Manager". A failed
  commit or push never fails the apply; the log says what happened.
  Follow-ups: messages are imperative ("Install steam"); setting up a remote
  for the backup is left to Git (Phase 7); there is no way yet to go back to a
  commit from the history.

## 5. Words people already know

- [x] **Plain vocabulary.** "Generation" becomes restore point or system
  version, flake inputs become software sources, closure size becomes download
  and disk size. Technical names stay visible in detailed mode and tooltips.
  Decided: one vocabulary in both modes. Home is "your user" (as the Software
  page already said), the checkout is "your configuration", store paths are
  "files nothing uses", "activate" becomes apply or switch. In detailed mode a
  muted technical name follows the main headings (generations, flake inputs,
  closure). The Generations page is now Restore points.
  Follow-ups: step labels in progress lists and logs, and the CLI, keep their
  technical names; package names and input names (nixpkgs) are shown as they
  are.
- [x] **Review summary line.** Every review starts with one sentence: what is
  added, updated and removed, download size, whether a restart is needed.
  `reviewSummary` names installed and removed packages, counts updates and gives
  the change in disk use, what was downloaded and a kernel restart. The review
  records the bytes Nix reported transferring while preparing it.
  Decided: the download is reported as done ("were downloaded to prepare it"),
  since a review exists only after its build; amounts under 1 MiB are left out.
  Follow-up: an estimate before preparing would need a dry run of the build.
- [x] **Time estimates** for builds and downloads based on earlier runs: the
  median of the last five runs of the same kind gives "about N minutes left".

## 6. Discover as an app store

- [x] **Apps first.** Separate graphical apps from command-line tools. Needs
  research: nixpkgs has no complete AppStream catalogue; options are matching
  Flathub/AppStream metadata by name and homepage, or reading `.desktop` files
  and icons from substituted outputs.
  Decided: Flathub's AppStream catalogue (about 3,300 desktop apps, 10 MB
  compressed) is downloaded on demand and refreshed weekly; about 1,400 match a
  nixpkgs package by homepage, or by name on the same site. Where homepages
  moved apart, the name must also end the id and either the id's developer
  appears in the package homepage or the descriptions share a word; sampling
  found no wrong pairs left among the popular apps. Reading `.desktop` files
  would need every output downloaded first. The catalog also records a cheap
  hint, GTK or Qt wrapping or desktop items, for apps Flathub does not list.
- [x] **Categories and real icons** from that metadata. Discover shows popular
  apps and the freedesktop main categories; icons are proxied and cached by the
  server. Follow-up: screenshots and longer descriptions in the app dialog.
- [x] **Bundles.** Starting points such as Gaming, Office and school, Photo and
  video, Programming: sets of curated features and apps staged together.
  Decided: a starting point lists every feature and app with a checkbox, marks
  what is already on or installed, and stages the rest as one pending edit,
  allowing non-free software where needed. Apps go to the user's first package
  list; features that would need a new file without an obvious place are left
  for Discover.

## 7. Getting started

- [x] **No host registry needed.** Today a checkout needs `data/hosts.nix` or
  `programs.nix-manager.hosts`. Detect `nixosConfigurations` and
  `homeConfigurations` (and NixOS-integrated Home Manager) directly and only ask
  when it is ambiguous.
  Decided: without a registry, the names decide first as `nixos-rebuild` and
  `home-manager` would, which costs only listing them; `networking.hostName`
  and `home.username` are evaluated only when the names do not. An ambiguous
  layout is an error that names the candidates. Follow-up: NixOS-integrated
  Home Manager needs the home scope to build and activate through the system,
  which changes generations, history and undo, so it is left for later.
- [x] **Setup assistant.** On a system without a flake: turn `/etc/nixos` into a
  flake in a user-owned checkout, start Git, check that flakes are enabled, and
  explain prepare and apply in three sentences.
  Decided: the assistant runs in the dashboard window on its own small server
  before there is a configuration, then hands the window over. nixpkgs is locked
  to the running revision while `flake.nix` follows the release branch, so
  updates move on as usual. A configuration that does not evaluate as a flake
  (missing files, channel imports) is reported and the folder removed again;
  converting channel imports is left to the user. An `/etc/nixos` that is a
  flake already is not converted.
- [x] **Starts like an app.** Menu entry and icon from the NixOS module, opening
  the dashboard without a terminal.
  Decided: the package installs its own icon with the menu entry. Opening it
  while the tray runs shows the tray's dashboard over D-Bus instead of starting
  a second one, and a configuration that cannot be read is explained in the
  window rather than on a terminal nobody sees. The tray follows the window's
  checkout unless `tray.flakePath` is set.

## 8. For everyone

- [x] **Accessibility audit.** Screen reader labels, live regions for build
  progress, focus order in dialogs, contrast in both themes, larger text,
  reduced motion.
  *Decided:*
  - **Contrast.** All text meets 4.5:1 in both themes, and form fields have a
    3:1 outline. Panel borders stay soft because they only group content.
  - **Focus.** Every focusable element shows focus, and a skip link leads to
    the content. When the button that opened a dialog is gone or disabled,
    focus moves to the content instead of the top of the window. The
    segmented choices move with the arrow keys.
  - **Announcements.** Running operations announce each step but never the
    percentage. The one-step dialog announces its stage, search announces how
    many results it found, and toasts use a single live region. Toasts wait
    while the pointer or focus is on them, and toasts with an action stay
    until it is used or dismissed.
  - **Text size and motion.** Text follows the reader's default font size.
    Reduced motion stops spinners after one turn and shows a still bar for
    progress of unknown length.
- [x] **Translations.** Extract interface strings; German first.
  *Decided:*
  - **Keys.** English sentences are the keys, gettext-style, so the code reads
    as it is shown and anything not translated yet stays readable. Counts use
    separate singular and plural sentences.
  - **Who speaks German.** The dashboard, the setup assistant, the tray menu
    and its notifications. The command line stays in English, so its output
    can be searched for and pasted into reports; output from Nix itself is
    shown as Nix wrote it.
  - **Which language.** The desktop's language (`LC_ALL`, `LC_MESSAGES`,
    `LANG`) unless Settings chooses one. The server decides, so every window
    and the tray agree; changing it reloads the dashboard and the tray menu.
  - **Stored text.** Step labels, failure explanations and notes are stored in
    English and translated where they are shown, so the history follows a
    change of language and Git commit messages stay English. Stored text
    with values in it is matched against its key.
  - **Checks.** A test fails when a text has no German, a placeholder is lost,
    a text is translated two ways or a translation is no longer used. It
    covers the curated features and other data shown through `t()`.

## Open questions

- App metadata comes from Flathub and is not locked with nixpkgs; a mirror in
  the flake (for example a pinned AppStream input) would make it reproducible.
- Should automatic updates be on by default once the tray is enabled?
