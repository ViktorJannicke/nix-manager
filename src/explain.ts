// Common Nix failures in words someone who does not know Nix can act on,
// shared by the dashboard and the command line. The original output stays
// available; an explanation only leads it.

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
const where = (files: readonly string[]) => (files.length ? ` It is set in ${list(files)}.` : "");

type Rule = (text: string) => Explanation | undefined;

const rules: Rule[] = [
  (text) =>
    /No space left on device/.test(text)
      ? {
          title: "The disk is full",
          body: "Nix ran out of space. Remove old versions of your system on the Generations page, then try again.",
        }
      : undefined,
  (text) =>
    /Request dismissed|Not authorized|Error executing command as another user/.test(text)
      ? {
          title: "Authentication was cancelled",
          body: "Changing the system needs an administrator password. Try again and enter it when asked.",
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
      title: `${list(names)} ${names.length === 1 ? "is" : "are"} not free software`,
      body: "Your configuration only allows software with a free license. Allow non-free software to install it, or remove it from your edits.",
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
      title: `${packageName(match[1])} has known security problems`,
      body: `Nixpkgs marks ${match[1]} as insecure${issues.length ? ` (${issues.slice(0, 3).join("; ")})` : ""}. Choose another package, or allow this version in your configuration if you need it.`,
    };
  },
  (text) => {
    const match = text.match(/Refusing to evaluate package '([^']+)' in \S+ because it is not available on the requested hostPlatform/);
    return match
      ? {
          title: `${packageName(match[1])} does not run on this computer`,
          body: "The package is not made for this kind of processor or operating system. Remove it from your configuration.",
        }
      : undefined;
  },
  (text) => {
    const match = text.match(/Refusing to evaluate package '([^']+)' in \S+ because it has problems:[\s\S]*?- broken/);
    return match
      ? {
          title: `${packageName(match[1])} does not work in this version of nixpkgs`,
          body: "It is marked as broken. Remove it for now; a later update may fix it.",
        }
      : undefined;
  },
  (text) => {
    const match = text.match(/error: '([^']+)' has been renamed to(?:\/replaced by)? '([^']+)'/);
    return match
      ? {
          title: `${match[1]} is now called ${match[2]}`,
          body: `Replace ${match[1]} with ${match[2]} in your configuration.`,
        }
      : undefined;
  },
  (text) => {
    const match = text.match(/error: '([^']+)' has been removed(.*)/);
    return match
      ? {
          title: `${match[1]} is no longer available`,
          body: `${sentence(`It was removed from nixpkgs${match[2]}`)} Remove it from your configuration.`,
        }
      : undefined;
  },
  (text) => {
    const match = text.match(/Path '([^']+)' in the repository "[^"]+" is not tracked by Git|error: path '\/nix\/store\/[a-z0-9]{32}-source\/([^']+)' does not exist/);
    if (!match) return undefined;
    const file = match[1] ?? match[2];
    return {
      title: `${file} is not part of the flake yet`,
      body: `Flakes only see files that Git tracks. Run git add ${file} in your configuration folder, then try again.`,
    };
  },
  (text) => {
    const match = text.match(/The option definition `([^']+)' in `([^']+)' no longer has any effect; please remove it\.\n?([^\n]*)/);
    if (!match) return undefined;
    const file = match[2].replace(/^\/nix\/store\/[a-z0-9]{32}-source\//, "");
    return {
      title: `${match[1]} no longer exists`,
      body: `${match[3] ? `${sentence(match[3])} ` : ""}${file === "<unknown-file>" ? "Remove the setting." : `Remove it from ${file}.`}`,
    };
  },
  (text) => {
    const match = text.match(/The option `([^']+)' does not exist\./);
    return match
      ? {
          title: `The setting ${match[1]} does not exist`,
          body: `It may have been renamed or removed in this version, or it belongs to a module your configuration does not import.${where(definedIn(text))}`,
        }
      : undefined;
  },
  (text) => {
    const match = text.match(/A definition for option `([^']+)' is not of type `(.+?)'\. Definition values:/);
    return match
      ? {
          title: `${match[1]} has a value it does not accept`,
          body: `It expects ${match[2].replace(/ matching the pattern .*/, " in a particular format")}.${where(definedIn(text))}`,
        }
      : undefined;
  },
  (text) => {
    const match = text.match(/The option `([^']+)' has conflicting definition values:/);
    return match
      ? {
          title: `${match[1]} is set to different values`,
          body: `Only one value can apply.${where(definedIn(text))} Remove all but one of them.`,
        }
      : undefined;
  },
  (text) => {
    const match = text.match(/The option `([^']+)' was accessed but has no value defined/);
    return match
      ? {
          title: `${match[1]} needs a value`,
          body: "Nothing in your configuration sets it, and it has no default. Set it, or remove what depends on it.",
        }
      : undefined;
  },
  (text) => {
    const match = text.match(/Failed assertions:\n([\s\S]*?)(?:\n\s*\n|$)/);
    if (!match) return undefined;
    const problems = match[1].split(/^- /m).filter((item) => item.trim()).map(sentence);
    return {
      title: problems.length === 1 ? "The configuration reports a problem" : `The configuration reports ${problems.length} problems`,
      body: problems.join(" "),
    };
  },
  (text) => {
    const files = unique([...text.matchAll(/Existing file '([^']+)' would be clobbered(?! by backing up)/g)].map((match) => match[1]));
    return files.length
      ? {
          title: files.length === 1 ? "A file is in the way" : `${files.length} files are in the way`,
          body: `Home Manager manages ${list(files)}, but ${files.length === 1 ? "a file already exists there" : "files already exist there"}. Move ${files.length === 1 ? "it" : "them"} somewhere else or delete ${files.length === 1 ? "it" : "them"}, then apply again.`,
        }
      : undefined;
  },
  (text) => {
    const match = text.match(/two given paths contain a conflicting subpath:\s*`?(\S+?)'? and\s*`?(\S+?)'?\n/);
    if (!match) return undefined;
    const [a, b] = [match[1], match[2]].map((path) => packageName(storeName(path).split("/")[0]));
    const file = storeName(match[1]).split("/").slice(1).join("/");
    return {
      title: "Two packages contain the same file",
      body: `${a} and ${b} both provide ${file}. Remove one of them.`,
    };
  },
  (text) => {
    const match = text.match(/Cannot build '\/nix\/store\/[a-z0-9]{32}-([^']+)\.drv'|builder for '\/nix\/store\/[a-z0-9]{32}-([^']+)\.drv' failed/);
    if (!match) return undefined;
    return {
      title: `Building ${packageName(match[1] ?? match[2])} failed`,
      body: "Its build log shows why. When the package comes from nixpkgs, this is rarely caused by your settings: try again after an update, or remove it for now.",
    };
  },
  (text) =>
    /unable to download|Could not resolve host|Couldn't resolve host|Connection timed out|Network is unreachable/.test(text)
      ? {
          title: "A download failed",
          body: "Check the internet connection and try again. If it keeps failing, the download server may be unavailable.",
        }
      : undefined,
  (text) => {
    const match = text.match(/error: attribute '([^']+)' missing(?:[\s\S]*?Did you mean (?:one of )?(.+?)\?)?/);
    if (!match) return undefined;
    const similar = match[2]?.split(/, | or /).filter(Boolean) ?? [];
    return {
      title: `${match[1]} does not exist`,
      body: `Your configuration refers to ${match[1]}, which does not exist. Check the spelling${similar.length ? `; similar names are ${list(similar)}` : ""}.`,
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
