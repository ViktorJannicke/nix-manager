import { existsSync, mkdirSync, readFileSync, readdirSync } from "node:fs";
import { join } from "node:path";
import { Schema } from "effect";
import type { Config } from "./domain";
import { localeFrom, type Locale } from "./i18n";
import { atomicJson } from "./manager";

/**
 * Dashboard preferences. They live in the state directory, not in browser
 * storage, because every launch serves the dashboard from a new port.
 */
export const Preferences = Schema.Struct({
  theme: Schema.optional(Schema.Literal("system", "light", "dark")),
  /** The interface language; "system" follows the desktop's language. */
  language: Schema.optional(Schema.Literal("system", "en", "de")),
  /** Simple shows one button and a plain summary; detailed shows every prepare, review and apply step. */
  detail: Schema.optional(Schema.Literal("simple", "detailed")),
  /** Prepare updates the tray finds while the computer is plugged in and not busy. */
  prepareUpdates: Schema.optional(Schema.Boolean),
  /** Remove older restore points of the apps once a week while plugged in and not busy. */
  cleanUp: Schema.optional(Schema.Boolean),
  /** Commit the files each applied change writes, with a message saying what it did. */
  history: Schema.optional(Schema.Boolean),
  /** Push those commits when the branch has an upstream. */
  backup: Schema.optional(Schema.Boolean),
  /** Name, describe and picture apps with Flathub's catalogue; on unless turned off. */
  appDetails: Schema.optional(Schema.Boolean),
});
export type Preferences = typeof Preferences.Type;

const decode = Schema.decodeUnknownSync(Preferences, { onExcessProperty: "error" });
const path = (config: Config) => join(config.stateDir, "preferences.json");

/**
 * Someone who has run operations before keeps the detailed view and the Git
 * workflow they know; everyone else starts simple, with a kept history.
 */
const usedBefore = (config: Config) => {
  const operations = join(config.stateDir, "operations");
  return existsSync(operations) && readdirSync(operations).some((name) => name.endsWith(".json"));
};

/** The stored preferences; a missing or unreadable file counts as none. */
function stored(config: Config): Preferences {
  try {
    return Schema.decodeUnknownSync(Preferences)(JSON.parse(readFileSync(path(config), "utf8")));
  } catch {
    return {};
  }
}

/** The language the dashboard and tray speak: the chosen one, or the desktop's. */
export const resolveLocale = (preferences: Preferences, env: Record<string, string | undefined> = process.env): Locale =>
  preferences.language && preferences.language !== "system"
    ? preferences.language
    : localeFrom([env.LC_ALL, env.LC_MESSAGES, env.LANG]);

export function readPreferences(config: Config): Preferences {
  const current = stored(config);
  // The first choice is written, so it does not change once operations exist.
  if (current.detail) return current;
  return writePreferences(config, usedBefore(config) ? { detail: "detailed" } : { detail: "simple", history: true });
}

/** Merges `patch`, which must only hold known preferences, into the stored ones. */
export function writePreferences(config: Config, patch: unknown): Preferences {
  const next = { ...stored(config), ...decode(patch) };
  mkdirSync(config.stateDir, { recursive: true, mode: 0o700 });
  atomicJson(path(config), next);
  return next;
}
