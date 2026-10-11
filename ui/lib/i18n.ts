import type { Locale } from "../../src/i18n";

export { getLocale, list, msg, t, tn } from "../../src/i18n";

/** Reloads the interface in `locale`, which it remembers until the server says otherwise. */
export function switchLocale(locale: Locale) {
  localStorage.setItem("nix-manager-locale", locale);
  location.reload();
}
