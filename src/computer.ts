// Settings of the computer itself, shown on the Computer page as forms.
//
// Like the curated experiences, every option is checked against the evaluated
// option tree: a setting the configuration does not have is hidden. All of them
// are system settings.

import type { CuratedSetting } from "./curated";
import type { Hardware } from "./hardware";

/** Form controls the option type alone would not give. */
export type ComputerInput = "timezone" | "locale" | "formats" | "keyboard" | "ports" | "user";

export interface ComputerSetting extends CuratedSetting {
  input?: ComputerInput;
  /** Shown only when this option is true, such as the user to log in automatically. */
  when?: string;
}

export interface ComputerSection {
  id: string;
  title: string;
  summary: string;
  icon: string;
  settings: ComputerSetting[];
}

export const computerSections: ComputerSection[] = [
  {
    id: "name",
    title: "Name",
    summary: "How this computer appears to other devices on your network.",
    icon: "monitor",
    settings: [{ option: "networking.hostName", label: "Computer name", help: "Letters, digits and dashes." }],
  },
  {
    id: "time",
    title: "Date and time",
    summary: "The clock follows the time zone you pick.",
    icon: "clock",
    settings: [
      { option: "time.timeZone", label: "Time zone", input: "timezone" },
      {
        option: "services.automatic-timezoned.enable",
        label: "Set the time zone automatically",
        help: "Follows your location when you travel.",
      },
    ],
  },
  {
    id: "language",
    title: "Language and formats",
    summary: "The language of menus and messages, and how dates, numbers and money are written.",
    icon: "languages",
    settings: [
      { option: "i18n.defaultLocale", label: "Language", input: "locale" },
      {
        option: "i18n.extraLocaleSettings",
        label: "Formats",
        help: "Dates, numbers, money, paper size and units.",
        input: "formats",
      },
    ],
  },
  {
    id: "keyboard",
    title: "Keyboard",
    summary: "The layout used on the login screen and in the desktop.",
    icon: "keyboard",
    settings: [
      { option: "services.xserver.xkb.layout", label: "Layout", input: "keyboard" },
      { option: "services.xserver.xkb.variant", label: "Variant", help: "For example nodeadkeys or dvorak. Leave empty for the usual one." },
      { option: "console.useXkbConfig", label: "Use it on text screens too" },
    ],
  },
  {
    id: "login",
    title: "Login",
    summary: "Start the desktop without asking for a password.",
    icon: "log-in",
    settings: [
      { option: "services.displayManager.autoLogin.enable", label: "Log in automatically" },
      { option: "services.displayManager.autoLogin.user", label: "User", input: "user", when: "services.displayManager.autoLogin.enable" },
    ],
  },
  {
    id: "sound",
    title: "Sound",
    summary: "Speakers, headphones and microphones through PipeWire.",
    icon: "volume-2",
    settings: [
      { option: "services.pipewire.enable", label: "Sound" },
      { option: "services.pipewire.pulse.enable", label: "Support apps made for PulseAudio", when: "services.pipewire.enable" },
      { option: "services.pipewire.alsa.enable", label: "Support apps made for ALSA", when: "services.pipewire.enable" },
    ],
  },
  {
    id: "printing",
    title: "Printing and scanning",
    summary: "Printers on your network are found automatically.",
    icon: "printer",
    settings: [
      { option: "services.printing.enable", label: "Printing" },
      { option: "services.avahi.enable", label: "Find printers on the network", help: "Also finds scanners and other shared devices." },
      { option: "hardware.sane.enable", label: "Scanning" },
    ],
  },
  {
    id: "bluetooth",
    title: "Bluetooth",
    summary: "Headphones, controllers and other wireless devices.",
    icon: "bluetooth",
    settings: [
      { option: "hardware.bluetooth.enable", label: "Bluetooth" },
      { option: "hardware.bluetooth.powerOnBoot", label: "Turn on at startup", when: "hardware.bluetooth.enable" },
    ],
  },
  {
    id: "firewall",
    title: "Firewall",
    summary: "Blocks connections from other computers unless you open a port for them.",
    icon: "shield",
    settings: [
      { option: "networking.firewall.enable", label: "Firewall" },
      { option: "networking.firewall.allowedTCPPorts", label: "Open TCP ports", help: "Includes ports that features such as Steam open; those stay open while the feature is on.", input: "ports", when: "networking.firewall.enable" },
      { option: "networking.firewall.allowedUDPPorts", label: "Open UDP ports", help: "Includes ports that features such as Steam open; those stay open while the feature is on.", input: "ports", when: "networking.firewall.enable" },
    ],
  },
];

/** Options the Computer page reads, besides the users. */
export const computerOptions = [
  ...computerSections.flatMap((section) => section.settings.map((setting) => setting.option)),
  "users.mutableUsers",
];

/** A user account that can log in. */
export interface UserAccount {
  name: string;
  description: string | null;
  groups: string[];
  /** Flake files defining the account; flake files are relative. */
  files: string[];
}

/** Members of this group may administer the computer. */
export const ADMIN_GROUP = "wheel";
/** Groups a new user starts in besides the administrators' group. */
export const NEW_USER_GROUPS = ["networkmanager"];

/** A user name `useradd` accepts and a Nix attribute can be without quotes. */
export const userNameProblem = (name: string, taken: readonly string[]) => {
  if (!name) return "Enter a user name.";
  if (!/^[a-z_][a-z0-9_-]*$/.test(name)) return "Use lowercase letters, digits, dashes and underscores, starting with a letter.";
  if (name.length > 32) return "Use at most 32 characters.";
  if (taken.includes(name)) return `${name} already exists.`;
  if (["root", "nobody", "admin"].includes(name)) return `${name} is reserved.`;
  return undefined;
};

/** Variables `i18n.extraLocaleSettings` sets for formats, as the NixOS installer writes them. */
export const FORMAT_VARIABLES = [
  "LC_ADDRESS",
  "LC_IDENTIFICATION",
  "LC_MEASUREMENT",
  "LC_MONETARY",
  "LC_NAME",
  "LC_NUMERIC",
  "LC_PAPER",
  "LC_TELEPHONE",
  "LC_TIME",
];

export const formatSettings = (locale: string) => Object.fromEntries(FORMAT_VARIABLES.map((name) => [name, locale]));

/** The locale formats follow, when every format variable names the same one. */
export const formatLocale = (value: unknown): string | null | undefined => {
  if (!value || typeof value !== "object") return null;
  const set = value as Record<string, unknown>;
  if (!Object.keys(set).length) return null;
  const locales = new Set(FORMAT_VARIABLES.map((name) => set[name]));
  const [only] = locales;
  return locales.size === 1 && typeof only === "string" ? only : undefined;
};

/** Common locales; the language and region names come from the browser. */
export const locales = [
  "en_US.UTF-8", "en_GB.UTF-8", "en_AU.UTF-8", "en_CA.UTF-8", "en_IE.UTF-8", "en_IN.UTF-8", "en_NZ.UTF-8",
  "de_DE.UTF-8", "de_AT.UTF-8", "de_CH.UTF-8", "fr_FR.UTF-8", "fr_BE.UTF-8", "fr_CA.UTF-8", "fr_CH.UTF-8",
  "es_ES.UTF-8", "es_MX.UTF-8", "es_AR.UTF-8", "it_IT.UTF-8", "pt_PT.UTF-8", "pt_BR.UTF-8", "nl_NL.UTF-8",
  "nl_BE.UTF-8", "da_DK.UTF-8", "sv_SE.UTF-8", "nb_NO.UTF-8", "fi_FI.UTF-8", "is_IS.UTF-8", "pl_PL.UTF-8",
  "cs_CZ.UTF-8", "sk_SK.UTF-8", "hu_HU.UTF-8", "ro_RO.UTF-8", "bg_BG.UTF-8", "el_GR.UTF-8", "tr_TR.UTF-8",
  "ru_RU.UTF-8", "uk_UA.UTF-8", "et_EE.UTF-8", "lv_LV.UTF-8", "lt_LT.UTF-8", "hr_HR.UTF-8", "sl_SI.UTF-8",
  "sr_RS.UTF-8", "he_IL.UTF-8", "ar_EG.UTF-8", "hi_IN.UTF-8", "th_TH.UTF-8", "vi_VN.UTF-8", "id_ID.UTF-8",
  "ja_JP.UTF-8", "ko_KR.UTF-8", "zh_CN.UTF-8", "zh_TW.UTF-8",
];

/** Common XKB layouts with their names. */
export const keyboardLayouts: { code: string; label: string }[] = [
  { code: "us", label: "English (US)" },
  { code: "gb", label: "English (UK)" },
  { code: "de", label: "German" },
  { code: "ch", label: "German (Switzerland)" },
  { code: "at", label: "German (Austria)" },
  { code: "fr", label: "French" },
  { code: "be", label: "Belgian" },
  { code: "ca", label: "French (Canada)" },
  { code: "es", label: "Spanish" },
  { code: "latam", label: "Spanish (Latin America)" },
  { code: "it", label: "Italian" },
  { code: "pt", label: "Portuguese" },
  { code: "br", label: "Portuguese (Brazil)" },
  { code: "nl", label: "Dutch" },
  { code: "dk", label: "Danish" },
  { code: "se", label: "Swedish" },
  { code: "no", label: "Norwegian" },
  { code: "fi", label: "Finnish" },
  { code: "is", label: "Icelandic" },
  { code: "pl", label: "Polish" },
  { code: "cz", label: "Czech" },
  { code: "sk", label: "Slovak" },
  { code: "hu", label: "Hungarian" },
  { code: "ro", label: "Romanian" },
  { code: "bg", label: "Bulgarian" },
  { code: "gr", label: "Greek" },
  { code: "tr", label: "Turkish" },
  { code: "ru", label: "Russian" },
  { code: "ua", label: "Ukrainian" },
  { code: "ee", label: "Estonian" },
  { code: "lv", label: "Latvian" },
  { code: "lt", label: "Lithuanian" },
  { code: "hr", label: "Croatian" },
  { code: "si", label: "Slovenian" },
  { code: "rs", label: "Serbian" },
  { code: "il", label: "Hebrew" },
  { code: "ara", label: "Arabic" },
  { code: "in", label: "Indian" },
  { code: "th", label: "Thai" },
  { code: "jp", label: "Japanese" },
  { code: "kr", label: "Korean" },
  { code: "cn", label: "Chinese" },
];

/**
 * A module written directly in a flake.nix, which Nix reports as that flake's
 * store path. Settings defined there cannot be edited from a module file.
 */
export const inFlakeFile = (file: string) => /^\/nix\/store\/[a-z0-9]{32}-source\/flake\.nix$/.test(file);

/** Features to suggest for detected hardware, by curated experience id, with the reason. */
export const hardwareSuggestions: { hardware: keyof Hardware; experience: string; reason: string }[] = [
  { hardware: "nvidia", experience: "nvidia", reason: "This computer has an NVIDIA graphics card. Its driver gives games and video full speed." },
  { hardware: "laptop", experience: "power-profiles", reason: "This computer runs on a battery. Power profiles let you save power when you need to." },
  { hardware: "bluetooth", experience: "bluetooth", reason: "This computer has Bluetooth." },
  { hardware: "printer", experience: "printing", reason: "A printer is connected." },
  { hardware: "fingerprint", experience: "fingerprint", reason: "This computer has a fingerprint reader. Use it to log in and unlock." },
];
