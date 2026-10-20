using NixManager.Edits;

namespace NixManager.Software;

/// <summary>A curated experience's options: those that turn it on and those its settings screen shows.</summary>
/// <param name="Id">The experience's name.</param>
/// <param name="Scope"><c>home</c> or <c>system</c>.</param>
/// <param name="Enable">The options set when it is turned on (the keys of <c>enable</c>).</param>
/// <param name="Settings">The options of its settings screen.</param>
public sealed record CuratedExperienceOptions(string Id, string Scope, IReadOnlyList<string> Enable, IReadOnlyList<string> Settings);

/// <summary>
/// Options of the curated experiences and the Computer page, so the inventory reports their state. Only the
/// option names: the experiences' texts, values and packages are the dashboard's data. StoreTests compares the
/// names with the lists in <c>Fixtures/recorded-store.json</c>.
/// </summary>
public static class CuratedOptions
{
    /// <summary>Each experience in the order of <c>Experiences</c>.</summary>
    public static readonly IReadOnlyList<CuratedExperienceOptions> Experiences =
    [
        new("non-free", Targets.System, ["nixpkgs.config.allowUnfree"], []),
        new("steam", Targets.System, ["programs.steam.enable"], ["programs.steam.remotePlay.openFirewall", "programs.steam.localNetworkGameTransfers.openFirewall", "programs.steam.gamescopeSession.enable", "programs.steam.protontricks.enable", "programs.steam.extest.enable", "programs.gamemode.enable"]),
        new("gamescope", Targets.System, ["programs.gamescope.enable"], ["programs.gamescope.capSysNice", "programs.gamescope.enableWsi", "programs.gamescope.args"]),
        new("sunshine", Targets.System, ["services.sunshine.enable", "services.sunshine.capSysAdmin"], ["services.sunshine.openFirewall", "services.sunshine.autoStart", "services.sunshine.capSysAdmin", "services.sunshine.settings.port"]),
        new("docker", Targets.System, ["virtualisation.docker.enable"], ["virtualisation.docker.enableOnBoot", "virtualisation.docker.autoPrune.enable", "virtualisation.docker.autoPrune.dates", "virtualisation.docker.rootless.enable", "virtualisation.docker.rootless.setSocketVariable", "virtualisation.docker.logDriver"]),
        new("virtual-machines", Targets.System, ["virtualisation.libvirtd.enable", "programs.virt-manager.enable"], ["virtualisation.libvirtd.qemu.swtpm.enable", "virtualisation.spiceUSBRedirection.enable", "virtualisation.libvirtd.onBoot", "virtualisation.libvirtd.onShutdown", "virtualisation.libvirtd.nss.enable"]),
        new("ollama", Targets.System, ["services.ollama.enable"], ["services.ollama.loadModels", "services.ollama.host", "services.ollama.port", "services.ollama.openFirewall", "services.ollama.rocmOverrideGfx"]),
        new("tailscale", Targets.System, ["services.tailscale.enable"], ["services.tailscale.useRoutingFeatures", "services.tailscale.openFirewall", "services.tailscale.disableTaildrop", "services.tailscale.extraUpFlags"]),
        new("chromium", Targets.System, ["programs.chromium.enable"], ["programs.chromium.enablePlasmaBrowserIntegration", "programs.chromium.homepageLocation", "programs.chromium.defaultSearchProviderEnabled", "programs.chromium.defaultSearchProviderSearchURL"]),
        new("printing", Targets.System, ["services.printing.enable", "services.avahi.enable", "services.avahi.nssmdns4"], ["services.printing.cups-pdf.enable", "services.printing.browsing", "services.printing.defaultShared", "services.printing.openFirewall", "services.printing.webInterface"]),
        new("bluetooth", Targets.System, ["hardware.bluetooth.enable"], ["hardware.bluetooth.powerOnBoot", "hardware.bluetooth.disabledPlugins"]),
        new("firmware-updates", Targets.System, ["services.fwupd.enable"], ["services.fwupd.extraRemotes", "services.fwupd.daemonSettings.DisabledPlugins", "services.fwupd.daemonSettings.DisabledDevices"]),
        new("appimage", Targets.System, ["programs.appimage.enable", "programs.appimage.binfmt"], ["programs.appimage.binfmt", "programs.nix-ld.enable"]),
        new("zram", Targets.System, ["zramSwap.enable"], ["zramSwap.algorithm", "zramSwap.priority", "zramSwap.memoryMax"]),
        new("flatpak", Targets.System, ["services.flatpak.enable"], []),
        new("podman", Targets.System, ["virtualisation.podman.enable"], ["virtualisation.podman.dockerCompat", "virtualisation.podman.autoPrune.enable"]),
        new("wireshark", Targets.System, ["programs.wireshark.enable"], ["programs.wireshark.dumpcap.enable", "programs.wireshark.usbmon.enable"]),
        new("openssh", Targets.System, ["services.openssh.enable"], ["services.openssh.settings.PasswordAuthentication", "services.openssh.settings.PermitRootLogin", "services.openssh.openFirewall"]),
        new("localsend", Targets.System, ["programs.localsend.enable"], ["programs.localsend.openFirewall"]),
        new("obs-studio", Targets.System, ["programs.obs-studio.enable"], ["programs.obs-studio.enableVirtualCamera"]),
        new("power-profiles", Targets.System, ["services.power-profiles-daemon.enable"], []),
        new("nvidia", Targets.System, ["services.xserver.videoDrivers"], ["hardware.nvidia.modesetting.enable", "hardware.nvidia.powerManagement.enable", "hardware.nvidia.nvidiaSettings"]),
        new("fingerprint", Targets.System, ["services.fprintd.enable"], []),
        new("earlyoom", Targets.System, ["services.earlyoom.enable"], ["services.earlyoom.freeMemThreshold", "services.earlyoom.enableNotifications"]),
        new("nix-cleanup", Targets.System, ["nix.gc.automatic", "nix.optimise.automatic"], ["nix.gc.automatic", "nix.gc.options", "nix.optimise.automatic"]),
        new("nh", Targets.System, ["programs.nh.enable"], ["programs.nh.clean.enable", "programs.nh.clean.dates", "programs.nh.clean.extraArgs"]),
        new("non-free-home", Targets.Home, ["nixpkgs.config.allowUnfree"], []),
        new("git", Targets.Home, ["programs.git.enable"], ["programs.git.lfs.enable", "programs.git.maintenance.enable", "programs.git.ignores", "programs.git.signing.format", "programs.git.signing.key", "programs.git.signing.signByDefault"]),
        new("github-cli", Targets.Home, ["programs.gh.enable"], ["programs.gh.gitCredentialHelper.enable", "programs.gh.gitCredentialHelper.hosts", "programs.gh.settings.git_protocol", "programs.gh.settings.editor"]),
        new("direnv", Targets.Home, ["programs.direnv.enable"], ["programs.direnv.nix-direnv.enable", "programs.direnv.silent", "programs.direnv.mise.enable"]),
        new("zed", Targets.Home, ["programs.zed-editor.enable"], ["programs.zed-editor.extensions", "programs.zed-editor.defaultEditor", "programs.zed-editor.mutableUserSettings", "programs.zed-editor.installRemoteServer"]),
        new("claude-code", Targets.Home, ["programs.claude-code.enable"], ["programs.claude-code.mutableSettings", "programs.claude-code.enableMcpIntegration"]),
        new("neovim", Targets.Home, ["programs.neovim.enable"], ["programs.neovim.defaultEditor", "programs.neovim.viAlias", "programs.neovim.vimAlias", "programs.neovim.vimdiffAlias", "programs.neovim.withNodeJs", "programs.neovim.withPython3"]),
        new("tmux", Targets.Home, ["programs.tmux.enable"], ["programs.tmux.mouse", "programs.tmux.keyMode", "programs.tmux.prefix", "programs.tmux.clock24", "programs.tmux.terminal"]),
        new("zsh", Targets.Home, ["programs.zsh.enable", "programs.zsh.autosuggestion.enable", "programs.zsh.syntaxHighlighting.enable"], ["programs.zsh.autosuggestion.enable", "programs.zsh.syntaxHighlighting.enable", "programs.zsh.enableCompletion", "programs.zsh.enableVteIntegration"]),
        new("starship", Targets.Home, ["programs.starship.enable"], ["programs.starship.presets", "programs.starship.enableTransience", "programs.starship.enableBashIntegration", "programs.starship.enableZshIntegration"]),
        new("fzf", Targets.Home, ["programs.fzf.enable"], ["programs.fzf.defaultCommand", "programs.fzf.defaultOptions", "programs.fzf.tmux.enableShellIntegration", "programs.fzf.enableBashIntegration", "programs.fzf.enableZshIntegration"]),
        new("zoxide", Targets.Home, ["programs.zoxide.enable"], ["programs.zoxide.options", "programs.zoxide.enableBashIntegration", "programs.zoxide.enableZshIntegration", "programs.zoxide.enableFishIntegration"]),
        new("syncthing", Targets.Home, ["services.syncthing.enable", "services.syncthing.overrideDevices", "services.syncthing.overrideFolders"], ["services.syncthing.tray.enable", "services.syncthing.guiAddress", "services.syncthing.settings.options.relaysEnabled", "services.syncthing.settings.options.localAnnounceEnabled", "services.syncthing.overrideFolders", "services.syncthing.overrideDevices"]),
        new("bat", Targets.Home, ["programs.bat.enable"], []),
        new("eza", Targets.Home, ["programs.eza.enable"], ["programs.eza.icons", "programs.eza.git"]),
        new("btop", Targets.Home, ["programs.btop.enable"], []),
        new("lazygit", Targets.Home, ["programs.lazygit.enable"], []),
        new("helix", Targets.Home, ["programs.helix.enable"], ["programs.helix.defaultEditor"]),
        new("yazi", Targets.Home, ["programs.yazi.enable"], ["programs.yazi.enableBashIntegration", "programs.yazi.enableZshIntegration", "programs.yazi.enableFishIntegration"]),
        new("atuin", Targets.Home, ["programs.atuin.enable"], ["programs.atuin.flags", "programs.atuin.enableBashIntegration", "programs.atuin.enableZshIntegration"]),
        new("nix-index", Targets.Home, ["programs.nix-index.enable"], ["programs.nix-index.enableBashIntegration", "programs.nix-index.enableZshIntegration"]),
        new("mpv", Targets.Home, ["programs.mpv.enable"], []),
        new("yt-dlp", Targets.Home, ["programs.yt-dlp.enable"], []),
        new("easyeffects", Targets.Home, ["services.easyeffects.enable"], ["services.easyeffects.preset"]),
        new("mangohud", Targets.Home, ["programs.mangohud.enable"], ["programs.mangohud.enableSessionWide"]),
    ];

    /// <summary>Options the Computer page reads, besides the users.</summary>
    public static readonly IReadOnlyList<string> Computer =
    [
        "networking.hostName",
        "time.timeZone",
        "services.automatic-timezoned.enable",
        "i18n.defaultLocale",
        "i18n.extraLocaleSettings",
        "services.xserver.xkb.layout",
        "services.xserver.xkb.variant",
        "console.useXkbConfig",
        "services.displayManager.autoLogin.enable",
        "services.displayManager.autoLogin.user",
        "services.pipewire.enable",
        "services.pipewire.pulse.enable",
        "services.pipewire.alsa.enable",
        "services.printing.enable",
        "services.avahi.enable",
        "hardware.sane.enable",
        "hardware.bluetooth.enable",
        "hardware.bluetooth.powerOnBoot",
        "networking.firewall.enable",
        "networking.firewall.allowedTCPPorts",
        "networking.firewall.allowedUDPPorts",
        "users.mutableUsers",
    ];

    /// <summary>The options the inventory of <paramref name="scope"/> reads, in a fixed order, with repeats.</summary>
    public static IReadOnlyList<string> For(string scope) =>
    [
        .. Experiences.Where(experience => experience.Scope == scope).SelectMany(experience => experience.Enable.Concat(experience.Settings)),
        .. scope == Targets.System ? Computer : [],
    ];
}
