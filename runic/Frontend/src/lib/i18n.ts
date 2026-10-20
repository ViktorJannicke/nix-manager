// Interface text through Runic Translations: components call the typed message functions of `m`, which follow the
// ViewModel's language (App and Setup call `useMessageLocale`). Text the backend writes for people, such as step
// labels, errors and failure explanations, arrives as `DisplayText`: a text reference to a message of the same
// catalogue (translations/stored) with its English, which `text` shows in the current language. Text that is not a
// message, such as Nix's output, or a record written before text references, has no reference and stays as it is.

import { createInlineRenderer, getLocale, type Locale, type MessageOptions } from "virtual:runic-translations/dashboard/runtime";
import { m } from "virtual:runic-translations/dashboard";
import { createTextReference, parseTextReference, resolveTextReference } from "virtual:runic-translations/dashboard/transport";
import { inlineFactory } from "@runic-artifex/translations-svelte/inline";
import type { DisplayText } from "../generated/types.js";

export { getLocale, m };
export type { DisplayText, Locale };

type Messages = typeof m;
type Key = keyof Messages;

/** A message without inputs, such as a curated feature's title, named by its key so it is shown in the current language. */
export type Phrase = { [K in Key]: Messages[K] extends (options?: MessageOptions) => string ? K : never }[Key];

/** A phrase in the current language. */
export const say = (phrase: Phrase) => m[phrase]();

/** Backend text in the current language: its message translated, or its English when it has none. */
export function text(value: DisplayText | null | undefined): string {
  if (!value) return "";
  if (!value.reference) return value.english;
  return resolveTextReference(value.reference, m, { locale: getLocale() }).text ?? value.english;
}

/**
 * The message `key` with its inputs as backend text, such as the label of a pending edit, which the backend stores
 * and shows in any language later.
 */
export function display<K extends Key>(key: K, ...inputs: K extends Phrase ? [] : [Parameters<Messages[K]>[0]]): DisplayText {
  const format = m[key] as (...args: unknown[]) => unknown;
  const values = inputs[0] as Readonly<Record<string, unknown>> | undefined;
  const english = String(values ? format(values, { locale: "en" }) : format({ locale: "en" }));
  return { english, reference: JSON.stringify(createTextReference(key, values ?? {}, { fallbackText: english })) };
}

/** The message key of backend text, such as `stored_build_home_configuration`; undefined for text that is not a message. */
export function textKey(value: DisplayText | null | undefined): string | undefined {
  if (!value?.reference) return undefined;
  const parsed = parseTextReference(value.reference);
  return parsed.ok && typeof parsed.value === "object" && parsed.value && "key" in parsed.value && typeof parsed.value.key === "string"
    ? parsed.value.key
    : undefined;
}

/** Whether two texts are the same: the same message with the same inputs, or the same plain text. */
export const sameText = (a: DisplayText | null | undefined, b: DisplayText | null | undefined) =>
  (a?.reference ?? null) === (b?.reference ?? null) && (a?.reference ? true : a?.english === b?.english);

/** A failure the backend reported as text, which `message` shows in the current language. */
export class TextError extends Error {
  constructor(readonly text: DisplayText) {
    super(text.english);
  }
}

/** What went wrong, in the current language where the backend said it as text. */
export const message = (error: unknown) => (error instanceof TextError ? text(error.text) : error instanceof Error ? error.message : String(error));

/** Action names (`Actions.Label` in the backend). */
const actionLabels: Record<string, () => string> = {
  build: m.stored_build_configuration,
  switch: m.stored_build_and_switch,
  boot: m.stored_apply_on_next_boot,
  rollback: m.stored_go_back_to_the_previous_version,
  gc: m.stored_remove_old_restore_points,
  check: m.stored_check_for_updates,
  "t3code-update": m.stored_update_t3_code,
  "opencode-update": m.stored_update_opencode,
  "pince-update": m.stored_update_pince,
  "review-build": m.stored_prepare_changes,
  "review-apply": m.stored_apply_changes,
};

/** The name of an action, such as "Check for updates". */
export const actionLabel = (action: string) => actionLabels[action]?.() ?? action;

/** "a, b, and c" in the current language. */
export const list = (items: readonly string[]) => m.common_list({ items });

// Messages with markup, such as `Detected as {#highlight}{$host}{/highlight}.`, render through `inline` and
// `LocalizedInline`. The page's own tags are declared in translations/runic.json; `LocalizedInline` renders them with
// the snippets its `custom` prop gives by tag name, such as "nm:highlight".

/** Renders a message with markup for `LocalizedInline`. */
export const inline = createInlineRenderer(inlineFactory);

/**
 * Text without its soft hyphens (U+00AD), for headings and names that never wrap: German compounds such as
 * "Wiederherstellungs\u00adpunkte" carry one for the narrow navigation, and a heading would otherwise give a landmark
 * a name with it.
 */
export const plain = (text: string) => text.replaceAll("\u00ad", "");
