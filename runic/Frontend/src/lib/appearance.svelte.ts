// The desktop's contrast and motion preferences on <html>, from Runic's appearance store: the engine's media queries
// combined with what the Window's host reports of the desktop (appearance/1), which also follows a change the web
// view does not. app.css styles `data-contrast="more"` with stronger text and borders, and `data-motion="reduce"`
// without animations.

import { useAppearance } from "@runic-artifex/svelte";

/** Follows the appearance while the calling component lives; call it while App or Setup initialises. */
export function followAppearance() {
  const look = useAppearance();
  $effect(() => {
    const root = document.documentElement;
    if (look.current.contrast === "more") root.dataset.contrast = "more";
    else delete root.dataset.contrast;
    if (look.current.reducedMotion) root.dataset.motion = "reduce";
    else delete root.dataset.motion;
  });
}
