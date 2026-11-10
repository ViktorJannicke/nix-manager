// The page speaks its Window's language. The backend sets it for every Window (IRunicApplicationLocale): the language
// chosen in Settings, or the desktop's, and switches it when that preference changes. Runic pushes it to the page,
// which hands it to the catalogue's runtime: `m.*()` and backend text (`text` in i18n.ts) render again in it, and
// <html lang dir> follows.

import { configureTranslatedText, syncDocumentLocale } from "@runic-artifex/views";
import { useTextLocale } from "@runic-artifex/svelte";
import { m } from "virtual:runic-translations/dashboard";
import { configureLocaleResolver, createLocaleSource, localeDirection } from "virtual:runic-translations/dashboard/runtime";
import { resolveTextReference } from "virtual:runic-translations/dashboard/transport";

/** Connects the catalogue to the Window's language; call it once, before the page mounts. */
export function followWindowLocale() {
  // English until the Window says otherwise, such as in the moment before the page connects.
  const source = createLocaleSource({ initialLocale: "en" });
  // Reactive: a template that calls `m.*()` reads it through the resolver and renders again on a switch.
  const textLocale = useTextLocale();
  configureLocaleResolver(() => textLocale.current ?? source.getLocale());
  configureTranslatedText({
    ...source,
    resolve: (reference, locale) => resolveTextReference(reference, m, { locale }).text,
    direction: localeDirection,
  });
  syncDocumentLocale();
}
