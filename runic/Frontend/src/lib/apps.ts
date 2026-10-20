// The app list's categories and the apps shown first. Fetching and matching
// Flathub's catalogue is the backend's (Software/Apps.cs).

import { m } from "./i18n";

/** Freedesktop main categories, as the app list groups them. */
export const appCategories = {
  AudioVideo: "Audio and video",
  Development: "Development",
  Education: "Education",
  Game: "Games",
  Graphics: "Graphics",
  Network: "Internet",
  Office: "Office",
  Science: "Science",
  System: "System",
  Utility: "Utilities",
} as const;
export type AppCategory = keyof typeof appCategories;

/** A category's name in the current language. */
export const appCategoryLabel = (id: string) =>
  (<Record<string, () => string>>{
    AudioVideo: m.apps_audio_video,
    Development: m.apps_development,
    Education: m.apps_education,
    Game: m.apps_games,
    Graphics: m.apps_graphics,
    Network: m.apps_internet,
    Office: m.apps_office,
    Science: m.apps_science,
    System: m.apps_system,
    Utility: m.apps_utilities,
  })[id]?.() ?? id;

/** Well-known apps shown before anyone picks a category. */
export const featuredApps = [
  "org.mozilla.firefox",
  "org.libreoffice.LibreOffice",
  "org.mozilla.thunderbird",
  "org.gimp.GIMP",
  "org.inkscape.Inkscape",
  "org.kde.krita",
  "org.videolan.VLC",
  "com.obsproject.Studio",
  "org.audacityteam.Audacity",
  "org.blender.Blender",
  "com.valvesoftware.Steam",
  "org.signal.Signal",
  "com.discordapp.Discord",
  "com.spotify.Client",
  "com.visualstudio.code",
  "org.keepassxc.KeePassXC.desktop",
];
