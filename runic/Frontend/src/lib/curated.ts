// Curated experiences: friendly bundles of NixOS and Home Manager options, from the Bun version's src/curated.ts,
// which this file replaces once the Bun version is removed. The backend reads the same options for the inventory
// (Software/CuratedOptions.cs, with StoreTests comparing them against a recording of src/); keep the option names
// of both identical. Titles, summaries, labels and help are English; `stored` translates them (translations/stored).
//
// Every option path below is validated against the evaluated option tree at
// runtime. An experience whose options are missing from the user's
// configuration (for example because a module is not imported) is hidden.

export interface CuratedSetting {
  /** Full option path, e.g. "programs.steam.remotePlay.openFirewall". */
  option: string;
  label: string;
  /** One short sentence; omit when the label says it all. */
  help?: string;
}

export interface Experience {
  id: string;
  title: string;
  summary: string;
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
    title: "Non-free software",
    summary: "Allow apps whose license is not free, such as Steam, Discord, Spotify or Chrome.",
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
    title: "Steam",
    summary: "Install Steam with Proton so you can play Windows and Linux games.",
    icon: "gamepad-2",
    category: "Games",
    scope: "system",
    enable: {
      "programs.steam.enable": true,
    },
    settings: [
      {
        option: "programs.steam.remotePlay.openFirewall",
        label: "Open firewall for Remote Play",
        help: "Lets other devices stream games from this computer.",
      },
      {
        option: "programs.steam.localNetworkGameTransfers.openFirewall",
        label: "Open firewall for local game transfers",
        help: "Copies games from other computers on your network instead of downloading them.",
      },
      {
        option: "programs.steam.gamescopeSession.enable",
        label: "Add a Steam Big Picture session",
        help: "Adds a console-like session to the login screen.",
      },
      {
        option: "programs.steam.protontricks.enable",
        label: "Install Protontricks",
        help: "Helps fix games that need extra Windows components.",
      },
      {
        option: "programs.steam.extest.enable",
        label: "Steam Input on Wayland",
        help: "Translates Steam Input mouse and keyboard events for Wayland apps.",
      },
      {
        option: "programs.gamemode.enable",
        label: "Use GameMode",
        help: "Lets games ask for more CPU performance while they run.",
      },
    ],
  },
  {
    id: "gamescope",
    title: "Gamescope",
    summary: "Run games in a separate compositor with upscaling and frame limits.",
    icon: "monitor-play",
    category: "Games",
    scope: "system",
    enable: {
      "programs.gamescope.enable": true,
    },
    settings: [
      {
        option: "programs.gamescope.capSysNice",
        label: "Allow higher scheduling priority",
        help: "Can reduce stutter when the system is busy.",
      },
      {
        option: "programs.gamescope.enableWsi",
        label: "Enable the Vulkan WSI layer",
        help: "Needed for HDR and some presentation modes.",
      },
      {
        option: "programs.gamescope.args",
        label: "Default arguments",
        help: "For example --rt or --adaptive-sync.",
      },
    ],
  },
  {
    id: "sunshine",
    title: "Sunshine",
    summary: "Stream your desktop and games to Moonlight on other devices.",
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
        label: "Open firewall for streaming",
        help: "Needed for devices on your network to connect.",
      },
      {
        option: "services.sunshine.autoStart",
        label: "Start when you log in",
      },
      {
        option: "services.sunshine.capSysAdmin",
        label: "Allow screen capture on Wayland",
        help: "Required to capture the screen in a Plasma Wayland session.",
      },
      {
        option: "services.sunshine.settings.port",
        label: "Base port",
      },
    ],
  },
  {
    id: "docker",
    title: "Docker",
    summary: "Build and run containers with the Docker engine.",
    icon: "container",
    category: "Development",
    scope: "system",
    enable: {
      "virtualisation.docker.enable": true,
    },
    settings: [
      {
        option: "virtualisation.docker.enableOnBoot",
        label: "Start at boot",
        help: "When off, Docker starts the first time it is used.",
      },
      {
        option: "virtualisation.docker.autoPrune.enable",
        label: "Clean up unused images automatically",
      },
      {
        option: "virtualisation.docker.autoPrune.dates",
        label: "Cleanup schedule",
        help: "A systemd calendar expression such as weekly.",
      },
      {
        option: "virtualisation.docker.rootless.enable",
        label: "Run Docker without root",
      },
      {
        option: "virtualisation.docker.rootless.setSocketVariable",
        label: "Point the docker command at the rootless daemon",
      },
      {
        option: "virtualisation.docker.logDriver",
        label: "Container log storage",
      },
    ],
  },
  {
    id: "virtual-machines",
    title: "Virtual Machine Manager",
    summary: "Create and run virtual machines with libvirt and QEMU.",
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
        label: "Virtual TPM",
        help: "Needed to install Windows 11.",
      },
      {
        option: "virtualisation.spiceUSBRedirection.enable",
        label: "Pass USB devices to virtual machines",
      },
      {
        option: "virtualisation.libvirtd.onBoot",
        label: "At boot",
        help: "Whether to start machines that were running before shutdown.",
      },
      {
        option: "virtualisation.libvirtd.onShutdown",
        label: "At shutdown",
        help: "Whether running machines are suspended or shut down.",
      },
      {
        option: "virtualisation.libvirtd.nss.enable",
        label: "Reach machines by name",
        help: "Resolves virtual machine host names on this computer.",
      },
    ],
  },
  {
    id: "ollama",
    title: "Ollama",
    summary: "Run large language models locally for chat and coding tools.",
    icon: "bot",
    category: "Development",
    scope: "system",
    enable: {
      "services.ollama.enable": true,
    },
    settings: [
      {
        option: "services.ollama.loadModels",
        label: "Models to download",
        help: "Model names such as llama3.2 or qwen2.5-coder.",
      },
      {
        option: "services.ollama.host",
        label: "Listen address",
        help: "Use 0.0.0.0 to share models with other devices.",
      },
      {
        option: "services.ollama.port",
        label: "Port",
      },
      {
        option: "services.ollama.openFirewall",
        label: "Open firewall for other devices",
      },
      {
        option: "services.ollama.rocmOverrideGfx",
        label: "AMD GPU override",
        help: "Set a GFX version for AMD cards that ROCm does not detect.",
      },
    ],
  },
  {
    id: "tailscale",
    title: "Tailscale",
    summary: "Connect your devices in a private network that works anywhere.",
    icon: "network",
    category: "Internet",
    scope: "system",
    enable: {
      "services.tailscale.enable": true,
    },
    settings: [
      {
        option: "services.tailscale.useRoutingFeatures",
        label: "Routing features",
        help: "Choose client to use exit nodes, server to offer one.",
      },
      {
        option: "services.tailscale.openFirewall",
        label: "Open firewall for direct connections",
        help: "Allows faster peer-to-peer connections.",
      },
      {
        option: "services.tailscale.disableTaildrop",
        label: "Turn off Taildrop file sharing",
      },
      {
        option: "services.tailscale.extraUpFlags",
        label: "Extra login flags",
        help: "For example --accept-routes.",
      },
    ],
    packages: ["trayscale"],
  },
  {
    id: "chromium",
    title: "Chromium",
    summary: "Install the Chromium browser with system-wide policies.",
    icon: "globe",
    category: "Internet",
    scope: "system",
    enable: {
      "programs.chromium.enable": true,
    },
    settings: [
      {
        option: "programs.chromium.enablePlasmaBrowserIntegration",
        label: "Plasma browser integration",
        help: "Shows media controls and tabs in Plasma.",
      },
      {
        option: "programs.chromium.homepageLocation",
        label: "Home page",
      },
      {
        option: "programs.chromium.defaultSearchProviderEnabled",
        label: "Use a custom search engine",
      },
      {
        option: "programs.chromium.defaultSearchProviderSearchURL",
        label: "Search URL",
        help: "Use {searchTerms} where the query goes.",
      },
    ],
    packages: ["chromium"],
  },
  {
    id: "printing",
    title: "Printing",
    summary: "Print to local and network printers, which are found automatically.",
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
        label: "Add a Print to PDF printer",
      },
      {
        option: "services.printing.browsing",
        label: "Show printers shared by other computers",
      },
      {
        option: "services.printing.defaultShared",
        label: "Share new printers on the network",
      },
      {
        option: "services.printing.openFirewall",
        label: "Open firewall for shared printers",
      },
      {
        option: "services.printing.webInterface",
        label: "Web interface",
        help: "Manage printers at localhost:631.",
      },
    ],
  },
  {
    id: "bluetooth",
    title: "Bluetooth",
    summary: "Connect headphones, controllers and other wireless devices.",
    icon: "bluetooth",
    category: "System",
    scope: "system",
    enable: {
      "hardware.bluetooth.enable": true,
    },
    settings: [
      {
        option: "hardware.bluetooth.powerOnBoot",
        label: "Turn on at startup",
      },
      {
        option: "hardware.bluetooth.disabledPlugins",
        label: "Disabled plugins",
        help: "BlueZ plugin names to skip, for example sap.",
      },
    ],
  },
  {
    id: "firmware-updates",
    title: "Firmware updates",
    summary: "Update device firmware from Discover or the command line.",
    icon: "hard-drive-download",
    category: "System",
    scope: "system",
    enable: {
      "services.fwupd.enable": true,
    },
    settings: [
      {
        option: "services.fwupd.extraRemotes",
        label: "Extra update sources",
        help: "Add lvfs-testing to get firmware before general release.",
      },
      {
        option: "services.fwupd.daemonSettings.DisabledPlugins",
        label: "Disabled plugins",
      },
      {
        option: "services.fwupd.daemonSettings.DisabledDevices",
        label: "Ignored devices",
        help: "Device GUIDs that should never be updated.",
      },
    ],
  },
  {
    id: "appimage",
    title: "AppImage",
    summary: "Run downloaded AppImage apps with a double click.",
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
        label: "Open AppImages directly",
        help: "Lets you start AppImage files like any other program.",
      },
      {
        option: "programs.nix-ld.enable",
        label: "Run other prebuilt Linux programs",
        help: "Provides a standard loader for binaries not built for NixOS.",
      },
    ],
  },
  {
    id: "zram",
    title: "Compressed memory",
    summary: "Fit more programs in memory by compressing part of it.",
    icon: "memory-stick",
    category: "System",
    scope: "system",
    enable: {
      "zramSwap.enable": true,
    },
    settings: [
      {
        option: "zramSwap.algorithm",
        label: "Compression algorithm",
        help: "zstd compresses best, lz4 is fastest.",
      },
      {
        option: "zramSwap.priority",
        label: "Swap priority",
        help: "Higher values are used before disk swap.",
      },
      {
        option: "zramSwap.memoryMax",
        label: "Maximum size in bytes",
        help: "Leave empty to use a share of total memory.",
      },
    ],
  },

  {
    id: "flatpak",
    title: "Flatpak",
    summary: "Install apps from Flathub alongside Nix packages.",
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
    title: "Podman",
    summary: "Run containers without a background daemon.",
    icon: "boxes",
    category: "Development",
    scope: "system",
    enable: {
      "virtualisation.podman.enable": true,
    },
    settings: [
      {
        option: "virtualisation.podman.dockerCompat",
        label: "Answer to the docker command",
        help: "Conflicts with Docker itself.",
      },
      {
        option: "virtualisation.podman.autoPrune.enable",
        label: "Clean up unused images automatically",
      },
    ],
  },
  {
    id: "wireshark",
    title: "Wireshark",
    summary: "Capture and inspect network traffic.",
    icon: "radar",
    category: "Development",
    scope: "system",
    enable: {
      "programs.wireshark.enable": true,
    },
    settings: [
      {
        option: "programs.wireshark.dumpcap.enable",
        label: "Capture without root",
        help: "Members of the wireshark group can capture.",
      },
      {
        option: "programs.wireshark.usbmon.enable",
        label: "Capture USB traffic",
      },
    ],
    packages: ["wireshark"],
  },
  {
    id: "openssh",
    title: "SSH server",
    summary: "Log in to this computer from other devices.",
    icon: "server",
    category: "Internet",
    scope: "system",
    enable: {
      "services.openssh.enable": true,
    },
    settings: [
      {
        option: "services.openssh.settings.PasswordAuthentication",
        label: "Allow password logins",
        help: "Turn off once your SSH keys are set up.",
      },
      {
        option: "services.openssh.settings.PermitRootLogin",
        label: "Root login",
      },
      {
        option: "services.openssh.openFirewall",
        label: "Open firewall for SSH",
      },
    ],
  },
  {
    id: "localsend",
    title: "LocalSend",
    summary: "Share files with nearby phones and computers.",
    icon: "share-2",
    category: "Internet",
    scope: "system",
    enable: {
      "programs.localsend.enable": true,
    },
    settings: [
      {
        option: "programs.localsend.openFirewall",
        label: "Open firewall for transfers",
      },
    ],
  },
  {
    id: "obs-studio",
    title: "OBS Studio",
    summary: "Record and stream your screen, camera and games.",
    icon: "video",
    category: "Media",
    scope: "system",
    enable: {
      "programs.obs-studio.enable": true,
    },
    settings: [
      {
        option: "programs.obs-studio.enableVirtualCamera",
        label: "Virtual camera",
        help: "Use OBS output as a webcam in calls.",
      },
    ],
  },
  {
    id: "power-profiles",
    title: "Power profiles",
    summary: "Switch between power saving and performance from the panel.",
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
    title: "NVIDIA graphics driver",
    summary: "Use NVIDIA's own driver for full speed in games, video and 3D apps. For GeForce 16 series and newer cards.",
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
        label: "Kernel mode setting",
        help: "Needed for Wayland desktops.",
      },
      {
        option: "hardware.nvidia.powerManagement.enable",
        label: "Save video memory on sleep",
        help: "Turn on if apps show garbage after waking up.",
      },
      {
        option: "hardware.nvidia.nvidiaSettings",
        label: "NVIDIA Settings app",
      },
    ],
    unfree: ["nvidia-x11", "nvidia-settings", "nvidia-persistenced"],
  },
  {
    id: "fingerprint",
    title: "Fingerprint login",
    summary: "Log in, unlock the screen and confirm admin actions with your finger.",
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
    title: "Out-of-memory protection",
    summary: "Stop the biggest program before the computer freezes when memory runs out.",
    icon: "shield-alert",
    category: "System",
    scope: "system",
    enable: {
      "services.earlyoom.enable": true,
    },
    settings: [
      {
        option: "services.earlyoom.freeMemThreshold",
        label: "Act below this much free memory (%)",
      },
      {
        option: "services.earlyoom.enableNotifications",
        label: "Notify me when a program is stopped",
      },
    ],
  },
  {
    id: "nix-cleanup",
    title: "Automatic Nix cleanup",
    summary: "Delete old generations and deduplicate the Nix store on a schedule.",
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
        label: "Delete unused store paths",
      },
      {
        option: "nix.gc.options",
        label: "Cleanup options",
        help: "For example --delete-older-than 14d.",
      },
      {
        option: "nix.optimise.automatic",
        label: "Deduplicate identical files",
      },
    ],
  },
  {
    id: "nh",
    title: "nh",
    summary: "Rebuild and clean up NixOS with clearer output.",
    icon: "square-terminal",
    category: "System",
    scope: "system",
    enable: {
      "programs.nh.enable": true,
    },
    settings: [
      {
        option: "programs.nh.clean.enable",
        label: "Clean up periodically",
        help: "Do not combine with automatic Nix cleanup.",
      },
      {
        option: "programs.nh.clean.dates",
        label: "Cleanup schedule",
      },
      {
        option: "programs.nh.clean.extraArgs",
        label: "Cleanup options",
        help: "For example --keep-since 7d --keep 5.",
      },
    ],
  },

  // Home experiences

  {
    id: "non-free-home",
    title: "Non-free software",
    summary: "Allow apps whose license is not free, such as Discord, Spotify or Chrome, for your user.",
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
    title: "Git",
    summary: "Track changes in your projects with Git.",
    icon: "git-branch",
    category: "Development",
    scope: "home",
    enable: {
      "programs.git.enable": true,
    },
    settings: [
      {
        option: "programs.git.lfs.enable",
        label: "Large file support",
        help: "Stores large files outside the repository history.",
      },
      {
        option: "programs.git.maintenance.enable",
        label: "Background maintenance",
        help: "Keeps registered repositories fast.",
      },
      {
        option: "programs.git.ignores",
        label: "Files to ignore everywhere",
        help: "Patterns such as .direnv or *.swp.",
      },
      {
        option: "programs.git.signing.format",
        label: "Signing format",
      },
      {
        option: "programs.git.signing.key",
        label: "Signing key",
        help: "A GPG key ID or the path to an SSH key.",
      },
      {
        option: "programs.git.signing.signByDefault",
        label: "Sign every commit",
      },
    ],
  },
  {
    id: "github-cli",
    title: "GitHub CLI",
    summary: "Work with GitHub pull requests and issues from the terminal.",
    icon: "git-pull-request",
    category: "Development",
    scope: "home",
    enable: {
      "programs.gh.enable": true,
    },
    settings: [
      {
        option: "programs.gh.gitCredentialHelper.enable",
        label: "Sign in Git through GitHub CLI",
        help: "Git uses your gh login for HTTPS pushes.",
      },
      {
        option: "programs.gh.gitCredentialHelper.hosts",
        label: "Hosts for Git sign-in",
      },
      {
        option: "programs.gh.settings.git_protocol",
        label: "Clone protocol",
        help: "https or ssh.",
      },
      {
        option: "programs.gh.settings.editor",
        label: "Editor",
        help: "Leave empty to use your default editor.",
      },
    ],
  },
  {
    id: "direnv",
    title: "direnv",
    summary: "Load each project's tools automatically when you enter its folder.",
    icon: "folder-cog",
    category: "Development",
    scope: "home",
    enable: {
      "programs.direnv.enable": true,
    },
    settings: [
      {
        option: "programs.direnv.nix-direnv.enable",
        label: "Cache Nix shells",
        help: "Makes entering Nix projects fast and keeps shells from being collected.",
      },
      {
        option: "programs.direnv.silent",
        label: "Hide loading messages",
      },
      {
        option: "programs.direnv.mise.enable",
        label: "Support mise",
      },
    ],
  },
  {
    id: "zed",
    title: "Zed",
    summary: "Write code in a fast editor with built-in AI and collaboration.",
    icon: "code",
    category: "Development",
    scope: "home",
    enable: {
      "programs.zed-editor.enable": true,
    },
    settings: [
      {
        option: "programs.zed-editor.extensions",
        label: "Extensions",
        help: "Extension names such as nix or toml.",
      },
      {
        option: "programs.zed-editor.defaultEditor",
        label: "Use as default editor",
      },
      {
        option: "programs.zed-editor.mutableUserSettings",
        label: "Allow changes from the settings screen",
      },
      {
        option: "programs.zed-editor.installRemoteServer",
        label: "Install the remote server",
        help: "Lets Zed on other computers edit files here over SSH.",
      },
    ],
  },
  {
    id: "claude-code",
    title: "Claude Code",
    summary: "Use Anthropic's AI coding assistant in your terminal.",
    icon: "sparkles",
    category: "Development",
    scope: "home",
    enable: {
      "programs.claude-code.enable": true,
    },
    settings: [
      {
        option: "programs.claude-code.mutableSettings",
        label: "Allow changes from inside Claude Code",
      },
      {
        option: "programs.claude-code.enableMcpIntegration",
        label: "Share MCP servers",
        help: "Uses MCP servers configured elsewhere in Home Manager.",
      },
    ],
  },
  {
    id: "neovim",
    title: "Neovim",
    summary: "Edit text and code in a fast, keyboard-driven editor.",
    icon: "file-code",
    category: "Development",
    scope: "home",
    enable: {
      "programs.neovim.enable": true,
    },
    settings: [
      {
        option: "programs.neovim.defaultEditor",
        label: "Use as default editor",
      },
      {
        option: "programs.neovim.viAlias",
        label: "Open with vi",
      },
      {
        option: "programs.neovim.vimAlias",
        label: "Open with vim",
      },
      {
        option: "programs.neovim.vimdiffAlias",
        label: "Use for vimdiff",
      },
      {
        option: "programs.neovim.withNodeJs",
        label: "Node.js plugin support",
      },
      {
        option: "programs.neovim.withPython3",
        label: "Python plugin support",
      },
    ],
  },
  {
    id: "tmux",
    title: "tmux",
    summary: "Split your terminal into panes and keep sessions running.",
    icon: "panels-top-left",
    category: "Productivity",
    scope: "home",
    enable: {
      "programs.tmux.enable": true,
    },
    settings: [
      {
        option: "programs.tmux.mouse",
        label: "Mouse support",
      },
      {
        option: "programs.tmux.keyMode",
        label: "Key bindings",
      },
      {
        option: "programs.tmux.prefix",
        label: "Prefix key",
        help: "For example C-a. Leave empty for the default C-b.",
      },
      {
        option: "programs.tmux.clock24",
        label: "24-hour clock",
      },
      {
        option: "programs.tmux.terminal",
        label: "Terminal type",
        help: "tmux-256color gives the best color support.",
      },
    ],
  },
  {
    id: "zsh",
    title: "Zsh",
    summary: "Use a shell with suggestions as you type and highlighted commands.",
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
        label: "Suggest commands from history",
      },
      {
        option: "programs.zsh.syntaxHighlighting.enable",
        label: "Highlight commands",
      },
      {
        option: "programs.zsh.enableCompletion",
        label: "Tab completion",
      },
      {
        option: "programs.zsh.enableVteIntegration",
        label: "Open new tabs in the same folder",
      },
    ],
  },
  {
    id: "starship",
    title: "Starship",
    summary: "Show a fast prompt with Git status and tool versions in any shell.",
    icon: "rocket",
    category: "Productivity",
    scope: "home",
    enable: {
      "programs.starship.enable": true,
    },
    settings: [
      {
        option: "programs.starship.presets",
        label: "Presets",
        help: "Built-in looks such as nerd-font-symbols or plain-text-symbols.",
      },
      {
        option: "programs.starship.enableTransience",
        label: "Simplify previous prompts",
        help: "Only works in fish.",
      },
      {
        option: "programs.starship.enableBashIntegration",
        label: "Use in Bash",
      },
      {
        option: "programs.starship.enableZshIntegration",
        label: "Use in Zsh",
      },
    ],
  },
  {
    id: "fzf",
    title: "fzf",
    summary: "Find files and past commands quickly with fuzzy search.",
    icon: "search",
    category: "Productivity",
    scope: "home",
    enable: {
      "programs.fzf.enable": true,
    },
    settings: [
      {
        option: "programs.fzf.defaultCommand",
        label: "File search command",
        help: "For example fd --type f.",
      },
      {
        option: "programs.fzf.defaultOptions",
        label: "Default options",
        help: "For example --height 40% or --border.",
      },
      {
        option: "programs.fzf.tmux.enableShellIntegration",
        label: "Open in a tmux pane",
      },
      {
        option: "programs.fzf.enableBashIntegration",
        label: "Use in Bash",
      },
      {
        option: "programs.fzf.enableZshIntegration",
        label: "Use in Zsh",
      },
    ],
  },
  {
    id: "zoxide",
    title: "zoxide",
    summary: "Jump to folders you use often by typing part of their name.",
    icon: "folder-search",
    category: "Productivity",
    scope: "home",
    enable: {
      "programs.zoxide.enable": true,
    },
    settings: [
      {
        option: "programs.zoxide.options",
        label: "Options",
        help: "Use --cmd cd to replace the cd command.",
      },
      {
        option: "programs.zoxide.enableBashIntegration",
        label: "Use in Bash",
      },
      {
        option: "programs.zoxide.enableZshIntegration",
        label: "Use in Zsh",
      },
      {
        option: "programs.zoxide.enableFishIntegration",
        label: "Use in fish",
      },
    ],
  },
  {
    id: "syncthing",
    title: "Syncthing",
    summary: "Keep folders in sync between your devices without a cloud service.",
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
        label: "Show a tray icon",
      },
      {
        option: "services.syncthing.guiAddress",
        label: "Web interface address",
      },
      {
        option: "services.syncthing.settings.options.relaysEnabled",
        label: "Use relays",
        help: "Helps devices connect through strict firewalls.",
      },
      {
        option: "services.syncthing.settings.options.localAnnounceEnabled",
        label: "Find devices on your local network",
      },
      {
        option: "services.syncthing.overrideFolders",
        label: "Remove folders not declared in Nix",
        help: "Leave off if you add folders in the web interface.",
      },
      {
        option: "services.syncthing.overrideDevices",
        label: "Remove devices not declared in Nix",
        help: "Leave off if you add devices in the web interface.",
      },
    ],
  },
  {
    id: "bat",
    title: "bat",
    summary: "Read files with syntax highlighting and line numbers.",
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
    title: "eza",
    summary: "List files with colors, icons and Git status.",
    icon: "list-tree",
    category: "Productivity",
    scope: "home",
    enable: {
      "programs.eza.enable": true,
    },
    settings: [
      {
        option: "programs.eza.icons",
        label: "Icons",
      },
      {
        option: "programs.eza.git",
        label: "Show Git status",
      },
    ],
  },
  {
    id: "btop",
    title: "btop",
    summary: "Watch processor, memory, disk and network use.",
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
    title: "lazygit",
    summary: "Stage, commit and rebase in a terminal interface.",
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
    title: "Helix",
    summary: "Edit code in a modal editor with language support built in.",
    icon: "pen-line",
    category: "Development",
    scope: "home",
    enable: {
      "programs.helix.enable": true,
    },
    settings: [
      {
        option: "programs.helix.defaultEditor",
        label: "Use as default editor",
      },
    ],
  },
  {
    id: "yazi",
    title: "Yazi",
    summary: "Browse files in the terminal with previews.",
    icon: "folder-open",
    category: "Productivity",
    scope: "home",
    enable: {
      "programs.yazi.enable": true,
    },
    settings: [
      {
        option: "programs.yazi.enableBashIntegration",
        label: "Use in Bash",
      },
      {
        option: "programs.yazi.enableZshIntegration",
        label: "Use in Zsh",
      },
      {
        option: "programs.yazi.enableFishIntegration",
        label: "Use in fish",
      },
    ],
  },
  {
    id: "atuin",
    title: "Atuin",
    summary: "Search your shell history across sessions and devices.",
    icon: "history",
    category: "Productivity",
    scope: "home",
    enable: {
      "programs.atuin.enable": true,
    },
    settings: [
      {
        option: "programs.atuin.flags",
        label: "Flags",
        help: "Use --disable-up-arrow to keep the normal up arrow.",
      },
      {
        option: "programs.atuin.enableBashIntegration",
        label: "Use in Bash",
      },
      {
        option: "programs.atuin.enableZshIntegration",
        label: "Use in Zsh",
      },
    ],
  },
  {
    id: "nix-index",
    title: "nix-index",
    summary: "Find which package provides a missing command.",
    icon: "book-search",
    category: "Development",
    scope: "home",
    enable: {
      "programs.nix-index.enable": true,
    },
    settings: [
      {
        option: "programs.nix-index.enableBashIntegration",
        label: "Suggest packages in Bash",
      },
      {
        option: "programs.nix-index.enableZshIntegration",
        label: "Suggest packages in Zsh",
      },
    ],
  },
  {
    id: "mpv",
    title: "mpv",
    summary: "Play almost any video or audio file.",
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
    title: "yt-dlp",
    summary: "Download videos and audio from many websites.",
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
    title: "EasyEffects",
    summary: "Improve microphone and speaker sound with effects.",
    icon: "audio-lines",
    category: "Media",
    scope: "home",
    enable: {
      "services.easyeffects.enable": true,
    },
    settings: [
      {
        option: "services.easyeffects.preset",
        label: "Preset to load",
        help: "The name of a preset you saved in EasyEffects.",
      },
    ],
  },
  {
    id: "mangohud",
    title: "MangoHud",
    summary: "Show frame rate and temperatures over games.",
    icon: "gauge",
    category: "Games",
    scope: "home",
    enable: {
      "programs.mangohud.enable": true,
    },
    settings: [
      {
        option: "programs.mangohud.enableSessionWide",
        label: "Show in every Vulkan and OpenGL program",
      },
    ],
  },
];
