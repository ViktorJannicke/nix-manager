// Starting points: curated features and apps that are staged together, so a
// new computer is set up for one kind of use with a single pending edit.

export interface Bundle {
  id: string;
  title: string;
  summary: string;
  /** An ExperienceIcon name. */
  icon: string;
  /** Curated experience ids. */
  features: string[];
  /** Attribute paths below `pkgs`, installed for the current user. */
  apps: string[];
}

export const bundles: Bundle[] = [
  {
    id: "gaming",
    title: "Gaming",
    summary: "Steam with Proton, a frame rate overlay, and launchers for other stores and Minecraft.",
    icon: "gamepad-2",
    features: ["steam", "gamescope", "mangohud"],
    apps: ["heroic", "prismlauncher", "discord"],
  },
  {
    id: "office",
    title: "Office and school",
    summary: "Documents and spreadsheets, mail, handwritten notes, sources and printing.",
    icon: "file-text",
    features: ["printing"],
    apps: ["libreoffice", "thunderbird", "xournalpp", "zotero", "keepassxc"],
  },
  {
    id: "creative",
    title: "Photo and video",
    summary: "Edit photos, draw, cut videos, record the screen and play anything.",
    icon: "clapperboard",
    features: ["obs-studio", "mpv"],
    apps: ["gimp", "darktable", "krita", "inkscape", "kdePackages.kdenlive", "audacity"],
  },
  {
    id: "programming",
    title: "Programming",
    summary: "An editor, Git with GitHub, containers and per-project environments.",
    icon: "code",
    features: ["git", "github-cli", "direnv", "podman"],
    apps: ["vscode"],
  },
];
