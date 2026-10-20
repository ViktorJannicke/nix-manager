// Settings of the computer itself, shown on the Computer page as forms (Software/CuratedOptions.cs reads the same
// options). Labels, help and reasons are messages, named by key (`say`).
//
// Like the curated experiences, every option is checked against the evaluated
// option tree: a setting the configuration does not have is hidden. All of them
// are system settings.

import type { HardwareFeatures } from "../generated/types.js";
import type { CuratedSetting } from "./curated";
import { m, type Phrase } from "./i18n";

/** Form controls the option type alone would not give. */
export type ComputerInput = "timezone" | "locale" | "formats" | "keyboard" | "ports" | "user";

export interface ComputerSetting extends CuratedSetting {
  input?: ComputerInput;
  /** Shown only when this option is true, such as the user to log in automatically. */
  when?: string;
}

export interface ComputerSection {
  id: string;
  title: Phrase;
  summary: Phrase;
  icon: string;
  settings: ComputerSetting[];
}

export const computerSections: ComputerSection[] = [
  {
    id: "name",
    title: "stored_name",
    summary: "stored_how_this_computer_appears_to_other_devices",
    icon: "monitor",
    settings: [{ option: "networking.hostName", label: "stored_computer_name", help: "stored_letters_digits_and_dashes" }],
  },
  {
    id: "time",
    title: "stored_date_and_time",
    summary: "stored_the_clock_follows_the_time_zone_you",
    icon: "clock",
    settings: [
      { option: "time.timeZone", label: "stored_time_zone", input: "timezone" },
      {
        option: "services.automatic-timezoned.enable",
        label: "stored_set_the_time_zone_automatically",
        help: "stored_follows_your_location_when_you_travel",
      },
    ],
  },
  {
    id: "language",
    title: "stored_language_and_formats",
    summary: "stored_the_language_of_menus_and_messages_and",
    icon: "languages",
    settings: [
      { option: "i18n.defaultLocale", label: "stored_language", input: "locale" },
      {
        option: "i18n.extraLocaleSettings",
        label: "stored_formats",
        help: "stored_dates_numbers_money_paper_size_and_units",
        input: "formats",
      },
    ],
  },
  {
    id: "keyboard",
    title: "stored_keyboard",
    summary: "stored_the_layout_used_on_the_login_screen",
    icon: "keyboard",
    settings: [
      { option: "services.xserver.xkb.layout", label: "stored_layout", input: "keyboard" },
      { option: "services.xserver.xkb.variant", label: "stored_variant", help: "stored_for_example_nodeadkeys_or_dvorak_leave_empty" },
      { option: "console.useXkbConfig", label: "stored_use_it_on_text_screens_too" },
    ],
  },
  {
    id: "login",
    title: "stored_login",
    summary: "stored_start_the_desktop_without_asking_for_a",
    icon: "log-in",
    settings: [
      { option: "services.displayManager.autoLogin.enable", label: "stored_log_in_automatically" },
      { option: "services.displayManager.autoLogin.user", label: "stored_user_2", input: "user", when: "services.displayManager.autoLogin.enable" },
    ],
  },
  {
    id: "sound",
    title: "stored_sound",
    summary: "stored_speakers_headphones_and_microphones_through_pipewire",
    icon: "volume-2",
    settings: [
      { option: "services.pipewire.enable", label: "stored_sound" },
      { option: "services.pipewire.pulse.enable", label: "stored_support_apps_made_for_pulseaudio", when: "services.pipewire.enable" },
      { option: "services.pipewire.alsa.enable", label: "stored_support_apps_made_for_alsa", when: "services.pipewire.enable" },
    ],
  },
  {
    id: "printing",
    title: "stored_printing_and_scanning",
    summary: "stored_printers_on_your_network_are_found_automatically",
    icon: "printer",
    settings: [
      { option: "services.printing.enable", label: "stored_printing" },
      { option: "services.avahi.enable", label: "stored_find_printers_on_the_network", help: "stored_also_finds_scanners_and_other_shared_devices" },
      { option: "hardware.sane.enable", label: "stored_scanning" },
    ],
  },
  {
    id: "bluetooth",
    title: "stored_bluetooth",
    summary: "stored_headphones_controllers_and_other_wireless_devices",
    icon: "bluetooth",
    settings: [
      { option: "hardware.bluetooth.enable", label: "stored_bluetooth" },
      { option: "hardware.bluetooth.powerOnBoot", label: "stored_turn_on_at_startup", when: "hardware.bluetooth.enable" },
    ],
  },
  {
    id: "firewall",
    title: "stored_firewall",
    summary: "stored_blocks_connections_from_other_computers_unless_you",
    icon: "shield",
    settings: [
      { option: "networking.firewall.enable", label: "stored_firewall" },
      { option: "networking.firewall.allowedTCPPorts", label: "stored_open_tcp_ports", help: "stored_includes_ports_that_features_such_as_steam", input: "ports", when: "networking.firewall.enable" },
      { option: "networking.firewall.allowedUDPPorts", label: "stored_open_udp_ports", help: "stored_includes_ports_that_features_such_as_steam", input: "ports", when: "networking.firewall.enable" },
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

/** Why `name` is not a user name `useradd` accepts and a Nix attribute can be without quotes. */
export const userNameProblem = (name: string, taken: readonly string[]) => {
  if (!name) return m.stored_enter_a_user_name();
  if (!/^[a-z_][a-z0-9_-]*$/.test(name)) return m.stored_use_lowercase_letters_digits_dashes_and_underscores();
  if (name.length > 32) return m.stored_use_at_most_32_characters();
  if (taken.includes(name)) return m.stored_already_exists_2({ name });
  if (["root", "nobody", "admin"].includes(name)) return m.stored_is_reserved({ name });
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
export const keyboardLayouts: { code: string; label: Phrase }[] = [
  { code: "us", label: "stored_english_us" },
  { code: "gb", label: "stored_english_uk" },
  { code: "de", label: "stored_german" },
  { code: "ch", label: "stored_german_switzerland" },
  { code: "at", label: "stored_german_austria" },
  { code: "fr", label: "stored_french" },
  { code: "be", label: "stored_belgian" },
  { code: "ca", label: "stored_french_canada" },
  { code: "es", label: "stored_spanish" },
  { code: "latam", label: "stored_spanish_latin_america" },
  { code: "it", label: "stored_italian" },
  { code: "pt", label: "stored_portuguese" },
  { code: "br", label: "stored_portuguese_brazil" },
  { code: "nl", label: "stored_dutch" },
  { code: "dk", label: "stored_danish" },
  { code: "se", label: "stored_swedish" },
  { code: "no", label: "stored_norwegian" },
  { code: "fi", label: "stored_finnish" },
  { code: "is", label: "stored_icelandic" },
  { code: "pl", label: "stored_polish" },
  { code: "cz", label: "stored_czech" },
  { code: "sk", label: "stored_slovak" },
  { code: "hu", label: "stored_hungarian" },
  { code: "ro", label: "stored_romanian" },
  { code: "bg", label: "stored_bulgarian" },
  { code: "gr", label: "stored_greek" },
  { code: "tr", label: "stored_turkish" },
  { code: "ru", label: "stored_russian" },
  { code: "ua", label: "stored_ukrainian" },
  { code: "ee", label: "stored_estonian" },
  { code: "lv", label: "stored_latvian" },
  { code: "lt", label: "stored_lithuanian" },
  { code: "hr", label: "stored_croatian" },
  { code: "si", label: "stored_slovenian" },
  { code: "rs", label: "stored_serbian" },
  { code: "il", label: "stored_hebrew" },
  { code: "ara", label: "stored_arabic" },
  { code: "in", label: "stored_indian" },
  { code: "th", label: "stored_thai" },
  { code: "jp", label: "stored_japanese" },
  { code: "kr", label: "stored_korean" },
  { code: "cn", label: "stored_chinese" },
];

/**
 * A module written directly in a flake.nix, which Nix reports as that flake's
 * store path. Settings defined there cannot be edited from a module file.
 */
export const inFlakeFile = (file: string) => /^\/nix\/store\/[a-z0-9]{32}-source\/flake\.nix$/.test(file);

/** Features to suggest for detected hardware, by curated experience id, with the reason. */
export const hardwareSuggestions: { hardware: keyof HardwareFeatures; experience: string; reason: Phrase }[] = [
  { hardware: "nvidia", experience: "nvidia", reason: "stored_this_computer_has_an_nvidia_graphics_card" },
  { hardware: "laptop", experience: "power-profiles", reason: "stored_this_computer_runs_on_a_battery_power" },
  { hardware: "bluetooth", experience: "bluetooth", reason: "stored_this_computer_has_bluetooth" },
  { hardware: "printer", experience: "printing", reason: "stored_a_printer_is_connected" },
  { hardware: "fingerprint", experience: "fingerprint", reason: "stored_this_computer_has_a_fingerprint_reader_use" },
];
