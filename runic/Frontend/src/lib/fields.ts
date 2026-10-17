import { getLocale } from "./i18n";

export type Field =
  | { kind: "boolean" }
  | { kind: "text"; nullable: boolean }
  | { kind: "number"; min?: number; max?: number }
  | { kind: "choice"; choices: string[] }
  | { kind: "lines" }
  | { kind: "other" };

/** Form control for an option type as `optionAttrSetToDocList` describes it. */
export function field(type: string | null | undefined): Field {
  if (!type) return { kind: "other" };
  if (type === "boolean" || type === "null or boolean") return { kind: "boolean" };
  if (/^(null or )?one of /.test(type)) return { kind: "choice", choices: [...type.matchAll(/"([^"]*)"/g)].map((match) => match[1]) };
  if (type === "list of string") return { kind: "lines" };
  if (/integer/.test(type) && !type.startsWith("list of")) {
    const range = type.match(/between (-?\d+) and (-?\d+)/);
    return { kind: "number", min: range ? Number(range[1]) : /unsigned|positive/.test(type) ? 0 : undefined, max: range ? Number(range[2]) : undefined };
  }
  if (/^(null or )?(non-empty )?(\(optionally newline-terminated\) )?(single-line )?string/.test(type) || type.startsWith("strings concatenated")) return { kind: "text", nullable: type.startsWith("null or") };
  return { kind: "other" };
}

/** The default as data, when its Nix text is a plain literal. */
export function parseDefault(text: string | null | undefined): unknown {
  if (!text) return undefined;
  if (text === "true" || text === "false") return text === "true";
  if (text === "null") return null;
  if (/^-?\d+$/.test(text)) return Number(text);
  if (/^"([^"\\]|\\.)*"$/.test(text)) return JSON.parse(text);
  if (/^\[\s*\]$/.test(text)) return [];
  if (/^\{\s*\}$/.test(text)) return {};
  return undefined;
}

/** Port numbers from text such as "22, 8080", or undefined when one is not a port. */
export function parsePorts(text: string): number[] | undefined {
  const parts = text.split(/[\s,]+/).filter(Boolean);
  const ports = parts.map(Number);
  return ports.every((port) => Number.isInteger(port) && port > 0 && port < 65536) ? [...new Set(ports)] : undefined;
}

/** "German (Germany)" for de_DE.UTF-8, using the browser's names. */
export function localeName(locale: string) {
  const tag = locale.replace(/\..*$/, "").replace("_", "-");
  try {
    const name = new Intl.DisplayNames([getLocale()], { type: "language" }).of(tag);
    if (name && name !== tag) return name;
  } catch {
    // An unknown tag shows as written.
  }
  return locale;
}

/** Every IANA time zone the browser knows, or none. */
export function timeZones(): string[] {
  try {
    return (Intl as unknown as { supportedValuesOf(key: string): string[] }).supportedValuesOf("timeZone");
  } catch {
    return [];
  }
}
