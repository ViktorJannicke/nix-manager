// Common Nix failures in words someone who does not know Nix can act on,
// shared by the dashboard and the command line. The original output stays
// available; an explanation only leads it. The text is English, filled in with
// `msg`, and translated where it is shown (see src/i18n.ts).

import { msg } from "./i18n";

/** A change the dashboard can offer to make the failure go away. */
export interface Fix {
  kind: "allow-unfree";
  packages: string[];
  /** The configuration that refused the packages, when known. */
  scope?: "home" | "system";
}

/** The command-line equivalent of a fix. */
export const fixCommand = (fix: Fix) =>
  `nix-manager software set nixpkgs.config.allowUnfree true${fix.scope === "system" ? " --system" : ""}`;

export interface Explanation {
  /** What went wrong, as one sentence. */
  title: string;
  /** What it means and what to do about it. */
  body: string;
  fix?: Fix;
}

const ansi = /\x1b\[[0-9;]*[a-zA-Z]/g;
const storeName = (path: string) => path.replace(/^\/nix\/store\/[a-z0-9]{32}-/, "");

/** The package name of a derivation name, as `builtins.parseDrvName` splits it. */
export const packageName = (name: string) => name.match(/^(.+?)-(?![a-zA-Z])/)?.[1] ?? name;

const list = (names: readonly string[]) =>
  names.length <= 1 ? (names[0] ?? "") : `${names.slice(0, -1).join(", ")} and ${names.at(-1)}`;
const unique = (items: readonly string[]) => [...new Set(items)];
const sentence = (text: string) => {
  const trimmed = text.trim().replace(/\s+/g, " ");
  return trimmed ? `${trimmed[0].toUpperCase()}${trimmed.slice(1)}${/[.!?]$/.test(trimmed) ? "" : "."}` : "";
};
/** Files named in a module system message, flake-relative where possible. */
const definedIn = (text: string) =>
  unique(
    [...text.matchAll(/^\s*- In `([^']+)'/gm)]
      .map((match) => match[1].replace(/^\/nix\/store\/[a-z0-9]{32}-source\//, ""))
      .filter((file) => file !== "<unknown-file>"),
  );

type Rule = (text: string) => Explanation | undefined;

const rules: Rule[] = [
  (text) =>
    /No space left on device/.test(text)
      ? {
          title: msg("The disk is full"),
          body: msg("Nix ran out of space. Remove old restore points on the Restore points page, then try again."),
        }
      : undefined,
  (text) =>
    /Request dismissed|Not authorized|Error executing command as another user/.test(text)
      ? {
          title: msg("Authentication was cancelled"),
          body: msg("Changing the system needs an administrator password. Try again and enter it when asked."),
        }
      : undefined,
  (text) => {
    const names = unique(
      [...text.matchAll(/Refusing to evaluate package '([^']+)' in \S+ because it has an unfree license/g)].map((match) =>
        packageName(match[1]),
      ),
    );
    if (!names.length) return undefined;
    return {
      title:
        names.length === 1
          ? msg("{names} is not free software", { names: list(names) })
          : msg("{names} are not free software", { names: list(names) }),
      body: msg("Your configuration only allows software with a free license. Allow non-free software to install it, or remove it from your edits."),
      fix: { kind: "allow-unfree", packages: names },
    };
  },
  (text) => {
    const match = text.match(/Refusing to evaluate package '([^']+)' in \S+ because it is marked as insecure(?:\s*Known issues:\n([\s\S]*?)\n\s*You can install it anyway)?/);
    if (!match) return undefined;
    // Each issue can be paragraphs long; its first sentence names it.
    const issues = (match[2] ?? "")
      .split(/^\s*- /m)
      .map((issue) => issue.trim().replace(/\s+/g, " ").match(/^.*?(?:\.(?=\s)|$)/)![0].replace(/\.$/, ""))
      .filter(Boolean);
    return {
      title: msg("{name} has known security problems", { name: packageName(match[1]) }),
      body: issues.length
        ? msg("Nixpkgs marks {package} as insecure ({issues}). Choose another package, or allow this version in your configuration if you need it.", {
            package: match[1],
            issues: issues.slice(0, 3).join("; "),
          })
        : msg("Nixpkgs marks {package} as insecure. Choose another package, or allow this version in your configuration if you need it.", {
            package: match[1],
          }),
    };
  },
  (text) => {
    const match = text.match(/Refusing to evaluate package '([^']+)' in \S+ because it is not available on the requested hostPlatform/);
    return match
      ? {
          title: msg("{name} does not run on this computer", { name: packageName(match[1]) }),
          body: msg("The package is not made for this kind of processor or operating system. Remove it from your configuration."),
        }
      : undefined;
  },
  (text) => {
    const match = text.match(/Refusing to evaluate package '([^']+)' in \S+ because it has problems:[\s\S]*?- broken/);
    return match
      ? {
          title: msg("{name} does not work in this version of nixpkgs", { name: packageName(match[1]) }),
          body: msg("It is marked as broken. Remove it for now; a later update may fix it."),
        }
      : undefined;
  },
  (text) => {
    const match = text.match(/error: '([^']+)' has been renamed to(?:\/replaced by)? '([^']+)'/);
    return match
      ? {
          title: msg("{old} is now called {new}", { old: match[1], new: match[2] }),
          body: msg("Replace {old} with {new} in your configuration.", { old: match[1], new: match[2] }),
        }
      : undefined;
  },
  (text) => {
    const match = text.match(/error: '([^']+)' has been removed(.*)/);
    if (!match) return undefined;
    // Nix may add why, such as ", use foo instead".
    const reason = sentence(`It was removed from nixpkgs${match[2]}`);
    return {
      title: msg("{name} is no longer available", { name: match[1] }),
      body:
        reason === "It was removed from nixpkgs."
          ? msg("It was removed from nixpkgs. Remove it from your configuration.")
          : msg("{reason} Remove it from your configuration.", { reason }),
    };
  },
  (text) => {
    const match = text.match(/Path '([^']+)' in the repository "[^"]+" is not tracked by Git|error: path '\/nix\/store\/[a-z0-9]{32}-source\/([^']+)' does not exist/);
    if (!match) return undefined;
    const file = match[1] ?? match[2];
    return {
      title: msg("{file} is not part of the flake yet", { file }),
      body: msg("Flakes only see files that Git tracks. Run git add {file} in your configuration folder, then try again.", { file }),
    };
  },
  (text) => {
    const match = text.match(/The option definition `([^']+)' in `([^']+)' no longer has any effect; please remove it\.\n?([^\n]*)/);
    if (!match) return undefined;
    const file = match[2].replace(/^\/nix\/store\/[a-z0-9]{32}-source\//, "");
    const hint = match[3] ? sentence(match[3]) : "";
    const unknown = file === "<unknown-file>";
    return {
      title: msg("{option} no longer exists", { option: match[1] }),
      body: hint
        ? unknown
          ? msg("{hint} Remove the setting.", { hint })
          : msg("{hint} Remove it from {file}.", { hint, file })
        : unknown
          ? msg("Remove the setting.")
          : msg("Remove it from {file}.", { file }),
    };
  },
  (text) => {
    const match = text.match(/The option `([^']+)' does not exist\./);
    if (!match) return undefined;
    const files = definedIn(text);
    return {
      title: msg("The setting {option} does not exist", { option: match[1] }),
      body: files.length
        ? msg("It may have been renamed or removed in this version, or it belongs to a module your configuration does not import. It is set in {files}.", { files: list(files) })
        : msg("It may have been renamed or removed in this version, or it belongs to a module your configuration does not import."),
    };
  },
  (text) => {
    const match = text.match(/A definition for option `([^']+)' is not of type `(.+?)'\. Definition values:/);
    if (!match) return undefined;
    const files = definedIn(text);
    const type = match[2].replace(/ matching the pattern .*/, " in a particular format");
    return {
      title: msg("{option} has a value it does not accept", { option: match[1] }),
      body: files.length ? msg("It expects {type}. It is set in {files}.", { type, files: list(files) }) : msg("It expects {type}.", { type }),
    };
  },
  (text) => {
    const match = text.match(/The option `([^']+)' has conflicting definition values:/);
    if (!match) return undefined;
    const files = definedIn(text);
    return {
      title: msg("{option} is set to different values", { option: match[1] }),
      body: files.length
        ? msg("Only one value can apply. It is set in {files}. Remove all but one of them.", { files: list(files) })
        : msg("Only one value can apply. Remove all but one of them."),
    };
  },
  (text) => {
    const match = text.match(/The option `([^']+)' was accessed but has no value defined/);
    return match
      ? {
          title: msg("{option} needs a value", { option: match[1] }),
          body: msg("Nothing in your configuration sets it, and it has no default. Set it, or remove what depends on it."),
        }
      : undefined;
  },
  (text) => {
    const match = text.match(/Failed assertions:\n([\s\S]*?)(?:\n\s*\n|$)/);
    if (!match) return undefined;
    const problems = match[1].split(/^- /m).filter((item) => item.trim()).map(sentence);
    return {
      title: problems.length === 1 ? msg("The configuration reports a problem") : msg("The configuration reports {count} problems", { count: problems.length }),
      // The configuration's own words, shown as they are.
      body: problems.join(" "),
    };
  },
  (text) => {
    const files = unique([...text.matchAll(/Existing file '([^']+)' would be clobbered(?! by backing up)/g)].map((match) => match[1]));
    if (!files.length) return undefined;
    return files.length === 1
      ? {
          title: msg("A file is in the way"),
          body: msg("Home Manager manages {files}, but a file already exists there. Move it somewhere else or delete it, then apply again.", { files: list(files) }),
        }
      : {
          title: msg("{count} files are in the way", { count: files.length }),
          body: msg("Home Manager manages {files}, but files already exist there. Move them somewhere else or delete them, then apply again.", { files: list(files) }),
        };
  },
  (text) => {
    const match = text.match(/two given paths contain a conflicting subpath:\s*`?(\S+?)'? and\s*`?(\S+?)'?\n/);
    if (!match) return undefined;
    const [a, b] = [match[1], match[2]].map((path) => packageName(storeName(path).split("/")[0]));
    const file = storeName(match[1]).split("/").slice(1).join("/");
    return {
      title: msg("Two packages contain the same file"),
      body: msg("{first} and {second} both provide {file}. Remove one of them.", { first: a, second: b, file }),
    };
  },
  (text) => {
    const match = text.match(/Cannot build '\/nix\/store\/[a-z0-9]{32}-([^']+)\.drv'|builder for '\/nix\/store\/[a-z0-9]{32}-([^']+)\.drv' failed/);
    if (!match) return undefined;
    return {
      title: msg("Building {name} failed", { name: packageName(match[1] ?? match[2]) }),
      body: msg("Its build log shows why. When the package comes from nixpkgs, this is rarely caused by your settings: try again after an update, or remove it for now."),
    };
  },
  (text) =>
    /unable to download|Could not resolve host|Couldn't resolve host|Connection timed out|Network is unreachable/.test(text)
      ? {
          title: msg("A download failed"),
          body: msg("Check the internet connection and try again. If it keeps failing, the download server may be unavailable."),
        }
      : undefined,
  (text) => {
    const match = text.match(/error: attribute '([^']+)' missing(?:[\s\S]*?Did you mean (?:one of )?(.+?)\?)?/);
    if (!match) return undefined;
    const similar = match[2]?.split(/, | or /).filter(Boolean) ?? [];
    return {
      title: msg("{name} does not exist", { name: match[1] }),
      body: similar.length
        ? msg("Your configuration refers to {name}, which does not exist. Check the spelling; similar names are {names}.", { name: match[1], names: list(similar) })
        : msg("Your configuration refers to {name}, which does not exist. Check the spelling.", { name: match[1] }),
    };
  },
];

/** The first known failure in a command's output, if any. */
export function explain(output: string): Explanation | undefined {
  // Nix indents the lines of a message below its first one.
  const text = output.replace(ansi, "").replace(/^ {7}/gm, "");
  for (const rule of rules) {
    const explanation = rule(text);
    if (explanation) return explanation;
  }
  return undefined;
}
