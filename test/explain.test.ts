import { expect, test } from "bun:test";
import { readFileSync } from "node:fs";
import { join } from "node:path";
import { explain, packageName } from "../src/explain";

// Output captured from Nix 2.34 and nixpkgs 26.05.
const fixture = (name: string) => readFileSync(join(import.meta.dir, "fixtures/nix-errors", `${name}.txt`), "utf8");

test("derivation names lose their version", () => {
  expect(packageName("steam-1.0.0.87")).toBe("steam");
  expect(packageName("ventoy-qt5-1.1.17")).toBe("ventoy-qt5");
  expect(packageName("hello")).toBe("hello");
});

test("refused packages say why and offer a fix where one exists", () => {
  expect(explain(fixture("unfree"))).toEqual({
    title: "steam is not free software",
    body: expect.stringContaining("Allow non-free software"),
    fix: { kind: "allow-unfree", packages: ["steam"] },
  });
  expect(explain(fixture("insecure"))).toEqual({
    title: "hello has known security problems",
    body: "Nixpkgs marks hello-2.12.3 as insecure (CVE-1). Choose another package, or allow this version in your configuration if you need it.",
  });
  expect(explain(fixture("broken"))?.title).toBe("hello does not work in this version of nixpkgs");
  expect(explain(fixture("platform"))?.title).toBe("hello does not run on this computer");
});

test("removed and misspelled packages", () => {
  expect(explain(fixture("aliasthrow"))).toEqual({
    title: "_0verkill is no longer available",
    body: "It was removed from nixpkgs due to lack of maintenance. Remove it from your configuration.",
  });
  expect(explain("error: '_1password' has been renamed to/replaced by '_1password-cli'")?.title).toBe(
    "_1password is now called _1password-cli",
  );
  expect(explain(fixture("misspelled"))?.body).toBe(
    "Your configuration refers to exa, which does not exist. Check the spelling; similar names are dxa, ea, era, eta and eva.",
  );
});

test("option problems name the setting and where it is set", () => {
  expect(explain(fixture("nooption"))?.title).toBe("The setting services.foobarxyz does not exist");
  expect(explain(fixture("type"))).toEqual({
    title: "networking.hostName has a value it does not accept",
    body: "It expects string in a particular format.",
  });
  expect(explain(fixture("conflict"))?.title).toBe("networking.hostName is set to different values");
  expect(explain(fixture("assertion"))).toEqual({
    title: "services.xserver.desktopManager.plasma5 no longer exists",
    body: "The Plasma 5 desktop environment has been removed from nixpkgs, as it has reached EOL upstream. Remove the setting.",
  });
  expect(
    explain("error:\n       Failed assertions:\n       - Swap needs a device.\n       - Users need a\n       password\n")?.body,
  ).toBe("Swap needs a device. Users need a password.");
  const located = "error: The option `a.b' has conflicting definition values:\n       - In `/nix/store/0r7xswpqa49y26bl05lydybpw86sywmq-source/hosts/x.nix': 1\n       - In `/nix/store/0r7xswpqa49y26bl05lydybpw86sywmq-source/modules/y.nix': 2\n";
  expect(explain(located)?.body).toBe("Only one value can apply. It is set in hosts/x.nix and modules/y.nix. Remove all but one of them.");
});

test("files, builds, disks and downloads", () => {
  expect(explain(fixture("untracked"))?.title).toBe("a.nix is not part of the flake yet");
  expect(explain(fixture("build"))?.title).toBe("Building libfoo failed");
  // Message templates from Home Manager's check-link-targets.sh and nixpkgs' buildEnv.
  expect(explain("Existing file '/home/u/.config/foo' would be clobbered\nExisting file '/home/u/.bashrc' would be clobbered")?.title).toBe(
    "2 files are in the way",
  );
  expect(
    explain(
      "two given paths contain a conflicting subpath:\n  `/nix/store/0r7xswpqa49y26bl05lydybpw86sywmq-vim-9.1/bin/vi' and\n  `/nix/store/1r7xswpqa49y26bl05lydybpw86sywmq-neovim-0.11/bin/vi'\nhint: this may be caused by two different versions",
    )?.body,
  ).toBe("vim and neovim both provide bin/vi. Remove one of them.");
  expect(explain("error: writing to file: No space left on device")?.title).toBe("The disk is full");
  expect(explain("error: unable to download 'https://cache.nixos.org/x': Could not resolve host")?.title).toBe("A download failed");
  expect(explain("error: something nobody has seen before")).toBeUndefined();
});

test("long insecurity notes are shortened to their first sentence", () => {
  expect(explain(fixture("insecure-notes"))?.body).toBe(
    "Nixpkgs marks olm-3.2.16 as insecure (The libolm end‐to‐end encryption library used in many Matrix clients and Jitsi Meet has been deprecated upstream, and relies on a cryptography library that has known side‐channel issues and disclaims that its implementations are not cryptographically secure and should not be used when cryptographic security is required). Choose another package, or allow this version in your configuration if you need it.",
  );
});
