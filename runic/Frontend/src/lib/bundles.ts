// Starting points: curated features and apps that are staged together, so a new computer is set up for one kind
// of use with a single pending edit. Titles and summaries are messages, named by key (`say`).

import type { Phrase } from "./i18n";

export interface Bundle {
  id: string;
  title: Phrase;
  summary: Phrase;
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
    title: "stored_gaming",
    summary: "stored_steam_with_proton_a_frame_rate_overlay",
    icon: "gamepad-2",
    features: ["steam", "gamescope", "mangohud"],
    apps: ["heroic", "prismlauncher", "discord"],
  },
  {
    id: "office",
    title: "stored_office_and_school",
    summary: "stored_documents_and_spreadsheets_mail_handwritten_notes_sources",
    icon: "file-text",
    features: ["printing"],
    apps: ["libreoffice", "thunderbird", "xournalpp", "zotero", "keepassxc"],
  },
  {
    id: "creative",
    title: "stored_photo_and_video",
    summary: "stored_edit_photos_draw_cut_videos_record_the",
    icon: "clapperboard",
    features: ["obs-studio", "mpv"],
    apps: ["gimp", "darktable", "krita", "inkscape", "kdePackages.kdenlive", "audacity"],
  },
  {
    id: "programming",
    title: "stored_programming",
    summary: "stored_an_editor_git_with_github_containers_and",
    icon: "code",
    features: ["git", "github-cli", "direnv", "podman"],
    apps: ["vscode"],
  },
];
