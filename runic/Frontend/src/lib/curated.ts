// Curated experiences: friendly bundles of NixOS and Home Manager options. The backend reads the same options for the
// inventory (Software/CuratedOptions.cs); keep the option names identical in both. Titles, summaries, labels and help
// are messages of translations/stored, named by key; `say` shows them in the current language.
//
// Every option path below is validated against the evaluated option tree at
// runtime. An experience whose options are missing from the user's
// configuration (for example because a module is not imported) is hidden.

import type { Phrase } from "./i18n";

export interface CuratedSetting {
  /** Full option path, e.g. "programs.steam.remotePlay.openFirewall". */
  option: string;
  label: Phrase;
  /** One short sentence; omit when the label says it all. */
  help?: Phrase;
}

export interface Experience {
  id: string;
  title: Phrase;
  summary: Phrase;
  icon: string;
  category: "Games" | "Development" | "Internet" | "Media" | "System" | "Productivity";
  scope: "home" | "system";
  /** Options set when the experience is turned on. Values are JSON (booleans, strings, numbers, string lists). */
  enable: Record<string, unknown>;
  /** Curated subset of options shown on its settings screen (simple types only). */
  settings: CuratedSetting[];
  /** Optional extra packages (attribute path relative to pkgs, e.g. "gamescope") installed alongside. */
  packages?: string[];
  /** Names of non-free packages the experience installs, which the configuration must allow. */
  unfree?: string[];
  /**
   * Options also written when the experience is turned on, but never read:
   * their value cannot be evaluated while it is off.
   */
  alsoSet?: Record<string, unknown>;
}

/** Options that are written but never read, see `Experience.alsoSet`. */
export const unreadOptions = () => new Set(experiences.flatMap((experience) => Object.keys(experience.alsoSet ?? {})));

export const experiences: Experience[] = [
  // System experiences

  {
    id: "non-free",
    title: "stored_non_free_software",
    summary: "stored_allow_apps_whose_license_is_not_free",
    icon: "lock-open",
    category: "System",
    scope: "system",
    enable: {
      "nixpkgs.config.allowUnfree": true,
    },
    settings: [],
  },

  {
    id: "steam",
    title: "stored_steam",
    summary: "stored_install_steam_with_proton_so_you_can",
    icon: "gamepad-2",
    category: "Games",
    scope: "system",
    enable: {
      "programs.steam.enable": true,
    },
    settings: [
      {
        option: "programs.steam.remotePlay.openFirewall",
        label: "stored_open_firewall_for_remote_play",
        help: "stored_lets_other_devices_stream_games_from_this",
      },
      {
        option: "programs.steam.localNetworkGameTransfers.openFirewall",
        label: "stored_open_firewall_for_local_game_transfers",
        help: "stored_copies_games_from_other_computers_on_your",
      },
      {
        option: "programs.steam.gamescopeSession.enable",
        label: "stored_add_a_steam_big_picture_session",
        help: "stored_adds_a_console_like_session_to_the",
      },
      {
        option: "programs.steam.protontricks.enable",
        label: "stored_install_protontricks",
        help: "stored_helps_fix_games_that_need_extra_windows",
      },
      {
        option: "programs.steam.extest.enable",
        label: "stored_steam_input_on_wayland",
        help: "stored_translates_steam_input_mouse_and_keyboard_events",
      },
      {
        option: "programs.gamemode.enable",
        label: "stored_use_gamemode",
        help: "stored_lets_games_ask_for_more_cpu_performance",
      },
    ],
  },
  {
    id: "gamescope",
    title: "stored_gamescope",
    summary: "stored_run_games_in_a_separate_compositor_with",
    icon: "monitor-play",
    category: "Games",
    scope: "system",
    enable: {
      "programs.gamescope.enable": true,
    },
    settings: [
      {
        option: "programs.gamescope.capSysNice",
        label: "stored_allow_higher_scheduling_priority",
        help: "stored_can_reduce_stutter_when_the_system_is",
      },
      {
        option: "programs.gamescope.enableWsi",
        label: "stored_enable_the_vulkan_wsi_layer",
        help: "stored_needed_for_hdr_and_some_presentation_modes",
      },
      {
        option: "programs.gamescope.args",
        label: "stored_default_arguments",
        help: "stored_for_example_rt_or_adaptive_sync",
      },
    ],
  },
  {
    id: "sunshine",
    title: "stored_sunshine",
    summary: "stored_stream_your_desktop_and_games_to_moonlight",
    icon: "cast",
    category: "Games",
    scope: "system",
    enable: {
      "services.sunshine.enable": true,
      "services.sunshine.capSysAdmin": true,
    },
    settings: [
      {
        option: "services.sunshine.openFirewall",
        label: "stored_open_firewall_for_streaming",
        help: "stored_needed_for_devices_on_your_network_to",
      },
      {
        option: "services.sunshine.autoStart",
        label: "stored_start_when_you_log_in",
      },
      {
        option: "services.sunshine.capSysAdmin",
        label: "stored_allow_screen_capture_on_wayland",
        help: "stored_required_to_capture_the_screen_in_a",
      },
      {
        option: "services.sunshine.settings.port",
        label: "stored_base_port",
      },
    ],
  },
  {
    id: "docker",
    title: "stored_docker",
    summary: "stored_build_and_run_containers_with_the_docker",
    icon: "container",
    category: "Development",
    scope: "system",
    enable: {
      "virtualisation.docker.enable": true,
    },
    settings: [
      {
        option: "virtualisation.docker.enableOnBoot",
        label: "stored_start_at_boot",
        help: "stored_when_off_docker_starts_the_first_time",
      },
      {
        option: "virtualisation.docker.autoPrune.enable",
        label: "stored_clean_up_unused_images_automatically",
      },
      {
        option: "virtualisation.docker.autoPrune.dates",
        label: "stored_cleanup_schedule",
        help: "stored_a_systemd_calendar_expression_such_as_weekly",
      },
      {
        option: "virtualisation.docker.rootless.enable",
        label: "stored_run_docker_without_root",
      },
      {
        option: "virtualisation.docker.rootless.setSocketVariable",
        label: "stored_point_the_docker_command_at_the_rootless",
      },
      {
        option: "virtualisation.docker.logDriver",
        label: "stored_container_log_storage",
      },
    ],
  },
  {
    id: "virtual-machines",
    title: "stored_virtual_machine_manager",
    summary: "stored_create_and_run_virtual_machines_with_libvirt",
    icon: "monitor-cog",
    category: "System",
    scope: "system",
    enable: {
      "virtualisation.libvirtd.enable": true,
      "programs.virt-manager.enable": true,
    },
    settings: [
      {
        option: "virtualisation.libvirtd.qemu.swtpm.enable",
        label: "stored_virtual_tpm",
        help: "stored_needed_to_install_windows_11",
      },
      {
        option: "virtualisation.spiceUSBRedirection.enable",
        label: "stored_pass_usb_devices_to_virtual_machines",
      },
      {
        option: "virtualisation.libvirtd.onBoot",
        label: "stored_at_boot",
        help: "stored_whether_to_start_machines_that_were_running",
      },
      {
        option: "virtualisation.libvirtd.onShutdown",
        label: "stored_at_shutdown",
        help: "stored_whether_running_machines_are_suspended_or_shut",
      },
      {
        option: "virtualisation.libvirtd.nss.enable",
        label: "stored_reach_machines_by_name",
        help: "stored_resolves_virtual_machine_host_names_on_this",
      },
    ],
  },
  {
    id: "ollama",
    title: "stored_ollama",
    summary: "stored_run_large_language_models_locally_for_chat",
    icon: "bot",
    category: "Development",
    scope: "system",
    enable: {
      "services.ollama.enable": true,
    },
    settings: [
      {
        option: "services.ollama.loadModels",
        label: "stored_models_to_download",
        help: "stored_model_names_such_as_llama3_2_or",
      },
      {
        option: "services.ollama.host",
        label: "stored_listen_address",
        help: "stored_use_0_0_0_0_to_share",
      },
      {
        option: "services.ollama.port",
        label: "stored_port",
      },
      {
        option: "services.ollama.openFirewall",
        label: "stored_open_firewall_for_other_devices",
      },
      {
        option: "services.ollama.rocmOverrideGfx",
        label: "stored_amd_gpu_override",
        help: "stored_set_a_gfx_version_for_amd_cards",
      },
    ],
  },
  {
    id: "tailscale",
    title: "stored_tailscale",
    summary: "stored_connect_your_devices_in_a_private_network",
    icon: "network",
    category: "Internet",
    scope: "system",
    enable: {
      "services.tailscale.enable": true,
    },
    settings: [
      {
        option: "services.tailscale.useRoutingFeatures",
        label: "stored_routing_features",
        help: "stored_choose_client_to_use_exit_nodes_server",
      },
      {
        option: "services.tailscale.openFirewall",
        label: "stored_open_firewall_for_direct_connections",
        help: "stored_allows_faster_peer_to_peer_connections",
      },
      {
        option: "services.tailscale.disableTaildrop",
        label: "stored_turn_off_taildrop_file_sharing",
      },
      {
        option: "services.tailscale.extraUpFlags",
        label: "stored_extra_login_flags",
        help: "stored_for_example_accept_routes",
      },
    ],
    packages: ["trayscale"],
  },
  {
    id: "chromium",
    title: "stored_chromium",
    summary: "stored_install_the_chromium_browser_with_system_wide",
    icon: "globe",
    category: "Internet",
    scope: "system",
    enable: {
      "programs.chromium.enable": true,
    },
    settings: [
      {
        option: "programs.chromium.enablePlasmaBrowserIntegration",
        label: "stored_plasma_browser_integration",
        help: "stored_shows_media_controls_and_tabs_in_plasma",
      },
      {
        option: "programs.chromium.homepageLocation",
        label: "stored_home_page",
      },
      {
        option: "programs.chromium.defaultSearchProviderEnabled",
        label: "stored_use_a_custom_search_engine",
      },
      {
        option: "programs.chromium.defaultSearchProviderSearchURL",
        label: "stored_search_url",
        help: "stored_use_where_the_query_goes",
      },
    ],
    packages: ["chromium"],
  },
  {
    id: "printing",
    title: "stored_printing",
    summary: "stored_print_to_local_and_network_printers_which",
    icon: "printer",
    category: "System",
    scope: "system",
    enable: {
      "services.printing.enable": true,
      "services.avahi.enable": true,
      "services.avahi.nssmdns4": true,
    },
    settings: [
      {
        option: "services.printing.cups-pdf.enable",
        label: "stored_add_a_print_to_pdf_printer",
      },
      {
        option: "services.printing.browsing",
        label: "stored_show_printers_shared_by_other_computers",
      },
      {
        option: "services.printing.defaultShared",
        label: "stored_share_new_printers_on_the_network",
      },
      {
        option: "services.printing.openFirewall",
        label: "stored_open_firewall_for_shared_printers",
      },
      {
        option: "services.printing.webInterface",
        label: "stored_web_interface",
        help: "stored_manage_printers_at_localhost_631",
      },
    ],
  },
  {
    id: "bluetooth",
    title: "stored_bluetooth",
    summary: "stored_connect_headphones_controllers_and_other_wireless_devices",
    icon: "bluetooth",
    category: "System",
    scope: "system",
    enable: {
      "hardware.bluetooth.enable": true,
    },
    settings: [
      {
        option: "hardware.bluetooth.powerOnBoot",
        label: "stored_turn_on_at_startup",
      },
      {
        option: "hardware.bluetooth.disabledPlugins",
        label: "stored_disabled_plugins",
        help: "stored_bluez_plugin_names_to_skip_for_example",
      },
    ],
  },
  {
    id: "firmware-updates",
    title: "stored_firmware_updates",
    summary: "stored_update_device_firmware_from_discover_or_the",
    icon: "hard-drive-download",
    category: "System",
    scope: "system",
    enable: {
      "services.fwupd.enable": true,
    },
    settings: [
      {
        option: "services.fwupd.extraRemotes",
        label: "stored_extra_update_sources",
        help: "stored_add_lvfs_testing_to_get_firmware_before",
      },
      {
        option: "services.fwupd.daemonSettings.DisabledPlugins",
        label: "stored_disabled_plugins",
      },
      {
        option: "services.fwupd.daemonSettings.DisabledDevices",
        label: "stored_ignored_devices",
        help: "stored_device_guids_that_should_never_be_updated",
      },
    ],
  },
  {
    id: "appimage",
    title: "stored_appimage",
    summary: "stored_run_downloaded_appimage_apps_with_a_double",
    icon: "package",
    category: "System",
    scope: "system",
    enable: {
      "programs.appimage.enable": true,
      "programs.appimage.binfmt": true,
    },
    settings: [
      {
        option: "programs.appimage.binfmt",
        label: "stored_open_appimages_directly",
        help: "stored_lets_you_start_appimage_files_like_any",
      },
      {
        option: "programs.nix-ld.enable",
        label: "stored_run_other_prebuilt_linux_programs",
        help: "stored_provides_a_standard_loader_for_binaries_not",
      },
    ],
  },
  {
    id: "zram",
    title: "stored_compressed_memory",
    summary: "stored_fit_more_programs_in_memory_by_compressing",
    icon: "memory-stick",
    category: "System",
    scope: "system",
    enable: {
      "zramSwap.enable": true,
    },
    settings: [
      {
        option: "zramSwap.algorithm",
        label: "stored_compression_algorithm",
        help: "stored_zstd_compresses_best_lz4_is_fastest",
      },
      {
        option: "zramSwap.priority",
        label: "stored_swap_priority",
        help: "stored_higher_values_are_used_before_disk_swap",
      },
      {
        option: "zramSwap.memoryMax",
        label: "stored_maximum_size_in_bytes",
        help: "stored_leave_empty_to_use_a_share_of",
      },
    ],
  },

  {
    id: "flatpak",
    title: "stored_flatpak",
    summary: "stored_install_apps_from_flathub_alongside_nix_packages",
    icon: "store",
    category: "System",
    scope: "system",
    enable: {
      "services.flatpak.enable": true,
    },
    settings: [],
  },
  {
    id: "podman",
    title: "stored_podman",
    summary: "stored_run_containers_without_a_background_daemon",
    icon: "boxes",
    category: "Development",
    scope: "system",
    enable: {
      "virtualisation.podman.enable": true,
    },
    settings: [
      {
        option: "virtualisation.podman.dockerCompat",
        label: "stored_answer_to_the_docker_command",
        help: "stored_conflicts_with_docker_itself",
      },
      {
        option: "virtualisation.podman.autoPrune.enable",
        label: "stored_clean_up_unused_images_automatically",
      },
    ],
  },
  {
    id: "wireshark",
    title: "stored_wireshark",
    summary: "stored_capture_and_inspect_network_traffic",
    icon: "radar",
    category: "Development",
    scope: "system",
    enable: {
      "programs.wireshark.enable": true,
    },
    settings: [
      {
        option: "programs.wireshark.dumpcap.enable",
        label: "stored_capture_without_root",
        help: "stored_members_of_the_wireshark_group_can_capture",
      },
      {
        option: "programs.wireshark.usbmon.enable",
        label: "stored_capture_usb_traffic",
      },
    ],
    packages: ["wireshark"],
  },
  {
    id: "openssh",
    title: "stored_ssh_server",
    summary: "stored_log_in_to_this_computer_from_other",
    icon: "server",
    category: "Internet",
    scope: "system",
    enable: {
      "services.openssh.enable": true,
    },
    settings: [
      {
        option: "services.openssh.settings.PasswordAuthentication",
        label: "stored_allow_password_logins",
        help: "stored_turn_off_once_your_ssh_keys_are",
      },
      {
        option: "services.openssh.settings.PermitRootLogin",
        label: "stored_root_login",
      },
      {
        option: "services.openssh.openFirewall",
        label: "stored_open_firewall_for_ssh",
      },
    ],
  },
  {
    id: "localsend",
    title: "stored_localsend",
    summary: "stored_share_files_with_nearby_phones_and_computers",
    icon: "share-2",
    category: "Internet",
    scope: "system",
    enable: {
      "programs.localsend.enable": true,
    },
    settings: [
      {
        option: "programs.localsend.openFirewall",
        label: "stored_open_firewall_for_transfers",
      },
    ],
  },
  {
    id: "obs-studio",
    title: "stored_obs_studio",
    summary: "stored_record_and_stream_your_screen_camera_and",
    icon: "video",
    category: "Media",
    scope: "system",
    enable: {
      "programs.obs-studio.enable": true,
    },
    settings: [
      {
        option: "programs.obs-studio.enableVirtualCamera",
        label: "stored_virtual_camera",
        help: "stored_use_obs_output_as_a_webcam_in",
      },
    ],
  },
  {
    id: "power-profiles",
    title: "stored_power_profiles",
    summary: "stored_switch_between_power_saving_and_performance_from",
    icon: "zap",
    category: "System",
    scope: "system",
    enable: {
      "services.power-profiles-daemon.enable": true,
    },
    settings: [],
  },
  {
    id: "nvidia",
    title: "stored_nvidia_graphics_driver",
    summary: "stored_use_nvidia_s_own_driver_for_full",
    icon: "monitor-cog",
    category: "System",
    scope: "system",
    enable: {
      "services.xserver.videoDrivers": ["nvidia"],
    },
    // NVIDIA's open kernel module, which GeForce 16 series (Turing) and newer cards need.
    alsoSet: {
      "hardware.nvidia.open": true,
    },
    settings: [
      {
        option: "hardware.nvidia.modesetting.enable",
        label: "stored_kernel_mode_setting",
        help: "stored_needed_for_wayland_desktops",
      },
      {
        option: "hardware.nvidia.powerManagement.enable",
        label: "stored_save_video_memory_on_sleep",
        help: "stored_turn_on_if_apps_show_garbage_after",
      },
      {
        option: "hardware.nvidia.nvidiaSettings",
        label: "stored_nvidia_settings_app",
      },
    ],
    unfree: ["nvidia-x11", "nvidia-settings", "nvidia-persistenced"],
  },
  {
    id: "fingerprint",
    title: "stored_fingerprint_login",
    summary: "stored_log_in_unlock_the_screen_and_confirm",
    icon: "fingerprint",
    category: "System",
    scope: "system",
    enable: {
      "services.fprintd.enable": true,
    },
    settings: [],
  },
  {
    id: "earlyoom",
    title: "stored_out_of_memory_protection",
    summary: "stored_stop_the_biggest_program_before_the_computer",
    icon: "shield-alert",
    category: "System",
    scope: "system",
    enable: {
      "services.earlyoom.enable": true,
    },
    settings: [
      {
        option: "services.earlyoom.freeMemThreshold",
        label: "stored_act_below_this_much_free_memory",
      },
      {
        option: "services.earlyoom.enableNotifications",
        label: "stored_notify_me_when_a_program_is_stopped",
      },
    ],
  },
  {
    id: "nix-cleanup",
    title: "stored_automatic_nix_cleanup",
    summary: "stored_delete_old_generations_and_deduplicate_the_nix",
    icon: "recycle",
    category: "System",
    scope: "system",
    enable: {
      "nix.gc.automatic": true,
      "nix.optimise.automatic": true,
    },
    settings: [
      {
        option: "nix.gc.automatic",
        label: "stored_delete_unused_store_paths",
      },
      {
        option: "nix.gc.options",
        label: "stored_cleanup_options",
        help: "stored_for_example_delete_older_than_14d",
      },
      {
        option: "nix.optimise.automatic",
        label: "stored_deduplicate_identical_files",
      },
    ],
  },
  {
    id: "nh",
    title: "stored_nh",
    summary: "stored_rebuild_and_clean_up_nixos_with_clearer",
    icon: "square-terminal",
    category: "System",
    scope: "system",
    enable: {
      "programs.nh.enable": true,
    },
    settings: [
      {
        option: "programs.nh.clean.enable",
        label: "stored_clean_up_periodically",
        help: "stored_do_not_combine_with_automatic_nix_cleanup",
      },
      {
        option: "programs.nh.clean.dates",
        label: "stored_cleanup_schedule",
      },
      {
        option: "programs.nh.clean.extraArgs",
        label: "stored_cleanup_options",
        help: "stored_for_example_keep_since_7d_keep_5",
      },
    ],
  },

  // Home experiences

  {
    id: "non-free-home",
    title: "stored_non_free_software",
    summary: "stored_allow_apps_whose_license_is_not_free_2",
    icon: "lock-open",
    category: "System",
    scope: "home",
    enable: {
      "nixpkgs.config.allowUnfree": true,
    },
    settings: [],
  },

  {
    id: "git",
    title: "stored_git",
    summary: "stored_track_changes_in_your_projects_with_git",
    icon: "git-branch",
    category: "Development",
    scope: "home",
    enable: {
      "programs.git.enable": true,
    },
    settings: [
      {
        option: "programs.git.lfs.enable",
        label: "stored_large_file_support",
        help: "stored_stores_large_files_outside_the_repository_history",
      },
      {
        option: "programs.git.maintenance.enable",
        label: "stored_background_maintenance",
        help: "stored_keeps_registered_repositories_fast",
      },
      {
        option: "programs.git.ignores",
        label: "stored_files_to_ignore_everywhere",
        help: "stored_patterns_such_as_direnv_or_swp",
      },
      {
        option: "programs.git.signing.format",
        label: "stored_signing_format",
      },
      {
        option: "programs.git.signing.key",
        label: "stored_signing_key",
        help: "stored_a_gpg_key_id_or_the_path",
      },
      {
        option: "programs.git.signing.signByDefault",
        label: "stored_sign_every_commit",
      },
    ],
  },
  {
    id: "github-cli",
    title: "stored_github_cli",
    summary: "stored_work_with_github_pull_requests_and_issues",
    icon: "git-pull-request",
    category: "Development",
    scope: "home",
    enable: {
      "programs.gh.enable": true,
    },
    settings: [
      {
        option: "programs.gh.gitCredentialHelper.enable",
        label: "stored_sign_in_git_through_github_cli",
        help: "stored_git_uses_your_gh_login_for_https",
      },
      {
        option: "programs.gh.gitCredentialHelper.hosts",
        label: "stored_hosts_for_git_sign_in",
      },
      {
        option: "programs.gh.settings.git_protocol",
        label: "stored_clone_protocol",
        help: "stored_https_or_ssh",
      },
      {
        option: "programs.gh.settings.editor",
        label: "stored_editor",
        help: "stored_leave_empty_to_use_your_default_editor",
      },
    ],
  },
  {
    id: "direnv",
    title: "stored_direnv",
    summary: "stored_load_each_project_s_tools_automatically_when",
    icon: "folder-cog",
    category: "Development",
    scope: "home",
    enable: {
      "programs.direnv.enable": true,
    },
    settings: [
      {
        option: "programs.direnv.nix-direnv.enable",
        label: "stored_cache_nix_shells",
        help: "stored_makes_entering_nix_projects_fast_and_keeps",
      },
      {
        option: "programs.direnv.silent",
        label: "stored_hide_loading_messages",
      },
      {
        option: "programs.direnv.mise.enable",
        label: "stored_support_mise",
      },
    ],
  },
  {
    id: "zed",
    title: "stored_zed",
    summary: "stored_write_code_in_a_fast_editor_with",
    icon: "code",
    category: "Development",
    scope: "home",
    enable: {
      "programs.zed-editor.enable": true,
    },
    settings: [
      {
        option: "programs.zed-editor.extensions",
        label: "stored_extensions",
        help: "stored_extension_names_such_as_nix_or_toml",
      },
      {
        option: "programs.zed-editor.defaultEditor",
        label: "stored_use_as_default_editor",
      },
      {
        option: "programs.zed-editor.mutableUserSettings",
        label: "stored_allow_changes_from_the_settings_screen",
      },
      {
        option: "programs.zed-editor.installRemoteServer",
        label: "stored_install_the_remote_server",
        help: "stored_lets_zed_on_other_computers_edit_files",
      },
    ],
  },
  {
    id: "claude-code",
    title: "stored_claude_code",
    summary: "stored_use_anthropic_s_ai_coding_assistant_in",
    icon: "sparkles",
    category: "Development",
    scope: "home",
    enable: {
      "programs.claude-code.enable": true,
    },
    settings: [
      {
        option: "programs.claude-code.mutableSettings",
        label: "stored_allow_changes_from_inside_claude_code",
      },
      {
        option: "programs.claude-code.enableMcpIntegration",
        label: "stored_share_mcp_servers",
        help: "stored_uses_mcp_servers_configured_elsewhere_in_home",
      },
    ],
  },
  {
    id: "neovim",
    title: "stored_neovim",
    summary: "stored_edit_text_and_code_in_a_fast",
    icon: "file-code",
    category: "Development",
    scope: "home",
    enable: {
      "programs.neovim.enable": true,
    },
    settings: [
      {
        option: "programs.neovim.defaultEditor",
        label: "stored_use_as_default_editor",
      },
      {
        option: "programs.neovim.viAlias",
        label: "stored_open_with_vi",
      },
      {
        option: "programs.neovim.vimAlias",
        label: "stored_open_with_vim",
      },
      {
        option: "programs.neovim.vimdiffAlias",
        label: "stored_use_for_vimdiff",
      },
      {
        option: "programs.neovim.withNodeJs",
        label: "stored_node_js_plugin_support",
      },
      {
        option: "programs.neovim.withPython3",
        label: "stored_python_plugin_support",
      },
    ],
  },
  {
    id: "tmux",
    title: "stored_tmux",
    summary: "stored_split_your_terminal_into_panes_and_keep",
    icon: "panels-top-left",
    category: "Productivity",
    scope: "home",
    enable: {
      "programs.tmux.enable": true,
    },
    settings: [
      {
        option: "programs.tmux.mouse",
        label: "stored_mouse_support",
      },
      {
        option: "programs.tmux.keyMode",
        label: "stored_key_bindings",
      },
      {
        option: "programs.tmux.prefix",
        label: "stored_prefix_key",
        help: "stored_for_example_c_a_leave_empty_for",
      },
      {
        option: "programs.tmux.clock24",
        label: "stored_n_24_hour_clock",
      },
      {
        option: "programs.tmux.terminal",
        label: "stored_terminal_type",
        help: "stored_tmux_256color_gives_the_best_color_support",
      },
    ],
  },
  {
    id: "zsh",
    title: "stored_zsh",
    summary: "stored_use_a_shell_with_suggestions_as_you",
    icon: "terminal",
    category: "Productivity",
    scope: "home",
    enable: {
      "programs.zsh.enable": true,
      "programs.zsh.autosuggestion.enable": true,
      "programs.zsh.syntaxHighlighting.enable": true,
    },
    settings: [
      {
        option: "programs.zsh.autosuggestion.enable",
        label: "stored_suggest_commands_from_history",
      },
      {
        option: "programs.zsh.syntaxHighlighting.enable",
        label: "stored_highlight_commands",
      },
      {
        option: "programs.zsh.enableCompletion",
        label: "stored_tab_completion",
      },
      {
        option: "programs.zsh.enableVteIntegration",
        label: "stored_open_new_tabs_in_the_same_folder",
      },
    ],
  },
  {
    id: "starship",
    title: "stored_starship",
    summary: "stored_show_a_fast_prompt_with_git_status",
    icon: "rocket",
    category: "Productivity",
    scope: "home",
    enable: {
      "programs.starship.enable": true,
    },
    settings: [
      {
        option: "programs.starship.presets",
        label: "stored_presets",
        help: "stored_built_in_looks_such_as_nerd_font",
      },
      {
        option: "programs.starship.enableTransience",
        label: "stored_simplify_previous_prompts",
        help: "stored_only_works_in_fish",
      },
      {
        option: "programs.starship.enableBashIntegration",
        label: "stored_use_in_bash",
      },
      {
        option: "programs.starship.enableZshIntegration",
        label: "stored_use_in_zsh",
      },
    ],
  },
  {
    id: "fzf",
    title: "stored_fzf",
    summary: "stored_find_files_and_past_commands_quickly_with",
    icon: "search",
    category: "Productivity",
    scope: "home",
    enable: {
      "programs.fzf.enable": true,
    },
    settings: [
      {
        option: "programs.fzf.defaultCommand",
        label: "stored_file_search_command",
        help: "stored_for_example_fd_type_f",
      },
      {
        option: "programs.fzf.defaultOptions",
        label: "stored_default_options",
        help: "stored_for_example_height_40_or_border",
      },
      {
        option: "programs.fzf.tmux.enableShellIntegration",
        label: "stored_open_in_a_tmux_pane",
      },
      {
        option: "programs.fzf.enableBashIntegration",
        label: "stored_use_in_bash",
      },
      {
        option: "programs.fzf.enableZshIntegration",
        label: "stored_use_in_zsh",
      },
    ],
  },
  {
    id: "zoxide",
    title: "stored_zoxide",
    summary: "stored_jump_to_folders_you_use_often_by",
    icon: "folder-search",
    category: "Productivity",
    scope: "home",
    enable: {
      "programs.zoxide.enable": true,
    },
    settings: [
      {
        option: "programs.zoxide.options",
        label: "stored_options",
        help: "stored_use_cmd_cd_to_replace_the_cd",
      },
      {
        option: "programs.zoxide.enableBashIntegration",
        label: "stored_use_in_bash",
      },
      {
        option: "programs.zoxide.enableZshIntegration",
        label: "stored_use_in_zsh",
      },
      {
        option: "programs.zoxide.enableFishIntegration",
        label: "stored_use_in_fish",
      },
    ],
  },
  {
    id: "syncthing",
    title: "stored_syncthing",
    summary: "stored_keep_folders_in_sync_between_your_devices",
    icon: "folder-sync",
    category: "Productivity",
    scope: "home",
    enable: {
      "services.syncthing.enable": true,
      "services.syncthing.overrideDevices": false,
      "services.syncthing.overrideFolders": false,
    },
    settings: [
      {
        option: "services.syncthing.tray.enable",
        label: "stored_show_a_tray_icon",
      },
      {
        option: "services.syncthing.guiAddress",
        label: "stored_web_interface_address",
      },
      {
        option: "services.syncthing.settings.options.relaysEnabled",
        label: "stored_use_relays",
        help: "stored_helps_devices_connect_through_strict_firewalls",
      },
      {
        option: "services.syncthing.settings.options.localAnnounceEnabled",
        label: "stored_find_devices_on_your_local_network",
      },
      {
        option: "services.syncthing.overrideFolders",
        label: "stored_remove_folders_not_declared_in_nix",
        help: "stored_leave_off_if_you_add_folders_in",
      },
      {
        option: "services.syncthing.overrideDevices",
        label: "stored_remove_devices_not_declared_in_nix",
        help: "stored_leave_off_if_you_add_devices_in",
      },
    ],
  },
  {
    id: "bat",
    title: "stored_bat",
    summary: "stored_read_files_with_syntax_highlighting_and_line",
    icon: "file-text",
    category: "Productivity",
    scope: "home",
    enable: {
      "programs.bat.enable": true,
    },
    settings: [],
  },
  {
    id: "eza",
    title: "stored_eza",
    summary: "stored_list_files_with_colors_icons_and_git",
    icon: "list-tree",
    category: "Productivity",
    scope: "home",
    enable: {
      "programs.eza.enable": true,
    },
    settings: [
      {
        option: "programs.eza.icons",
        label: "stored_icons",
      },
      {
        option: "programs.eza.git",
        label: "stored_show_git_status",
      },
    ],
  },
  {
    id: "btop",
    title: "stored_btop",
    summary: "stored_watch_processor_memory_disk_and_network_use",
    icon: "activity",
    category: "System",
    scope: "home",
    enable: {
      "programs.btop.enable": true,
    },
    settings: [],
  },
  {
    id: "lazygit",
    title: "stored_lazygit",
    summary: "stored_stage_commit_and_rebase_in_a_terminal",
    icon: "git-merge",
    category: "Development",
    scope: "home",
    enable: {
      "programs.lazygit.enable": true,
    },
    settings: [],
  },
  {
    id: "helix",
    title: "stored_helix",
    summary: "stored_edit_code_in_a_modal_editor_with",
    icon: "pen-line",
    category: "Development",
    scope: "home",
    enable: {
      "programs.helix.enable": true,
    },
    settings: [
      {
        option: "programs.helix.defaultEditor",
        label: "stored_use_as_default_editor",
      },
    ],
  },
  {
    id: "yazi",
    title: "stored_yazi",
    summary: "stored_browse_files_in_the_terminal_with_previews",
    icon: "folder-open",
    category: "Productivity",
    scope: "home",
    enable: {
      "programs.yazi.enable": true,
    },
    settings: [
      {
        option: "programs.yazi.enableBashIntegration",
        label: "stored_use_in_bash",
      },
      {
        option: "programs.yazi.enableZshIntegration",
        label: "stored_use_in_zsh",
      },
      {
        option: "programs.yazi.enableFishIntegration",
        label: "stored_use_in_fish",
      },
    ],
  },
  {
    id: "atuin",
    title: "stored_atuin",
    summary: "stored_search_your_shell_history_across_sessions_and",
    icon: "history",
    category: "Productivity",
    scope: "home",
    enable: {
      "programs.atuin.enable": true,
    },
    settings: [
      {
        option: "programs.atuin.flags",
        label: "stored_flags",
        help: "stored_use_disable_up_arrow_to_keep_the",
      },
      {
        option: "programs.atuin.enableBashIntegration",
        label: "stored_use_in_bash",
      },
      {
        option: "programs.atuin.enableZshIntegration",
        label: "stored_use_in_zsh",
      },
    ],
  },
  {
    id: "nix-index",
    title: "stored_nix_index",
    summary: "stored_find_which_package_provides_a_missing_command",
    icon: "book-search",
    category: "Development",
    scope: "home",
    enable: {
      "programs.nix-index.enable": true,
    },
    settings: [
      {
        option: "programs.nix-index.enableBashIntegration",
        label: "stored_suggest_packages_in_bash",
      },
      {
        option: "programs.nix-index.enableZshIntegration",
        label: "stored_suggest_packages_in_zsh",
      },
    ],
  },
  {
    id: "mpv",
    title: "stored_mpv",
    summary: "stored_play_almost_any_video_or_audio_file",
    icon: "clapperboard",
    category: "Media",
    scope: "home",
    enable: {
      "programs.mpv.enable": true,
    },
    settings: [],
  },
  {
    id: "yt-dlp",
    title: "stored_yt_dlp",
    summary: "stored_download_videos_and_audio_from_many_websites",
    icon: "download",
    category: "Media",
    scope: "home",
    enable: {
      "programs.yt-dlp.enable": true,
    },
    settings: [],
  },
  {
    id: "easyeffects",
    title: "stored_easyeffects",
    summary: "stored_improve_microphone_and_speaker_sound_with_effects",
    icon: "audio-lines",
    category: "Media",
    scope: "home",
    enable: {
      "services.easyeffects.enable": true,
    },
    settings: [
      {
        option: "services.easyeffects.preset",
        label: "stored_preset_to_load",
        help: "stored_the_name_of_a_preset_you_saved",
      },
    ],
  },
  {
    id: "mangohud",
    title: "stored_mangohud",
    summary: "stored_show_frame_rate_and_temperatures_over_games",
    icon: "gauge",
    category: "Games",
    scope: "home",
    enable: {
      "programs.mangohud.enable": true,
    },
    settings: [
      {
        option: "programs.mangohud.enableSessionWide",
        label: "stored_show_in_every_vulkan_and_opengl_program",
      },
    ],
  },
];
