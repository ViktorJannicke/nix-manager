// German interface text, by the part of the interface it belongs to. A key in
// more than one part must be translated the same way; the tests check that.
//
// Style: formal "Sie", sentence case, „German quotes“, and the words of the
// glossary in README.md, "Translations", so the same thing has the same name
// everywhere.

import { changes } from "./changes";
import { dialogs } from "./dialogs";
import { features } from "./features";
import { general } from "./general";
import { labels } from "./labels";
import { messages } from "./messages";
import { overview } from "./overview";
import { software } from "./software";

export const parts: Record<string, Readonly<Record<string, string>>> = {
  general,
  overview,
  software,
  changes,
  dialogs,
  features,
  messages,
  labels,
};

export const de: Readonly<Record<string, string>> = Object.assign({}, ...Object.values(parts));

/** The keys stored text is matched against; see labels.ts and messages.ts. */
export const filled: Readonly<Record<string, string>> = { ...labels, ...messages };
