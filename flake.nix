{
  description = "Nix Manager: CLI, desktop dashboard and tray for NixOS and Home Manager";

  inputs = {
    nixpkgs.url = "github:nixos/nixpkgs/nixos-unstable";
    home-manager = {
      url = "github:nix-community/home-manager";
      inputs.nixpkgs.follows = "nixpkgs";
    };
    # The application builder and the .NET development shell for runic/.
    runic-sdk = {
      url = "github:Runic-Artifex/runic-sdk/v0.8.0-preview.4";
      inputs.nixpkgs.follows = "nixpkgs";
    };
  };

  outputs =
    {
      self,
      nixpkgs,
      home-manager,
      runic-sdk,
    }:
    let
      systems = [
        "x86_64-linux"
        "aarch64-linux"
      ];
      forAllSystems = nixpkgs.lib.genAttrs systems;
      pkgsFor = system: import nixpkgs { inherit system; };
    in
    {
      packages = forAllSystems (
        system:
        let
          pkgs = pkgsFor system;
          manager = pkgs.callPackage ./runic/package.nix {
            buildRunicApplication = runic-sdk.lib.buildRunicApplication pkgs;
            homeManager = home-manager.packages.${system}.home-manager;
          };
        in
        {
          # runic/; its NuGet lock files cover linux-x64 and linux-arm64.
          default = manager;
          nix-manager = manager;
          # Slow VM tests, kept out of `nix flake check`; see nix/lab.
          desktop-a11y = import ./nix/lab/desktop-a11y.nix { inherit pkgs self; };
          speech-audio = import ./nix/lab/speech-audio.nix { inherit pkgs self; };
          # The WebKitGTK attribute-run abort in both Linux hosts; see nix/lab/webkit-attribute-run/REPORT.md.
          webkit-attribute-run = import ./nix/lab/webkit-attribute-run { inherit pkgs; };
          hyprland-sandbox = import ./nix/hyprland-sandbox { inherit pkgs; };
          desktop-apply = import ./nix/lab/desktop-apply.nix {
            inherit
              pkgs
              self
              nixpkgs
              home-manager
              ;
          };
          lab = import ./nix/lab/lab.nix {
            inherit pkgs;
            name = "nix-manager-lab";
            test = self.packages.${system}.desktop-a11y;
          };
          lab-apply = import ./nix/lab/lab.nix {
            inherit pkgs;
            name = "nix-manager-lab-apply";
            test = self.packages.${system}.desktop-apply;
            setup = self.packages.${system}.desktop-apply.labSetup;
          };
        }
      );

      apps = forAllSystems (
        system:
        let
          app = {
            type = "app";
            program = nixpkgs.lib.getExe self.packages.${system}.default;
            meta.description = "Manage NixOS and Home Manager configurations";
          };
        in
        {
          default = app;
          nix-manager = app;
          lab = {
            type = "app";
            program = nixpkgs.lib.getExe self.packages.${system}.lab;
            meta.description = "Drive a GNOME and Orca test VM with Nix Manager installed";
          };
          lab-apply = {
            type = "app";
            program = nixpkgs.lib.getExe self.packages.${system}.lab-apply;
            meta.description = "Drive the test VM with a configuration checkout that can apply for real";
          };
          hyprland-sandbox = {
            type = "app";
            program = nixpkgs.lib.getExe self.packages.${system}.hyprland-sandbox;
            meta.description = "Run Hyprland and Waybar headless on the GPU render node in a rootless sandbox";
          };
        }
      );

      # Lab VM systems and homes, for the configuration checkout in
      # nix/lab/desktop-apply.nix; not for real machines.
      lib.lab = forAllSystems (
        system:
        import ./nix/lab/system.nix {
          inherit
            nixpkgs
            home-manager
            self
            system
            ;
        }
      );

      nixosModules.default = import ./nixos-module.nix { inherit self; };
      nixosModules.nix-manager = self.nixosModules.default;

      devShells = forAllSystems (
        system:
        let
          pkgs = pkgsFor system;
        in
        {
          # The .NET SDK and Bun for runic/, GTK and WebKitGTK for the dashboard Window (nix-manager ui) on Runic
          # Desktop, Chromium for headless browser checks in serve-only mode, and Python and D-Bus for test/.
          default = runic-sdk.lib.mkDevShell pkgs {
            hosts = [ "desktop" ];
            browserTests = true;
            packageManagers = [ "bun" ];
            inherit (pkgs) nodejs;
            packages = with pkgs; [
              dbus
              python3
            ];
            # The program reads Nix files with tree-sitter's Nix grammar, as the package sets them.
            NIX_MANAGER_NIX_PARSER = "${pkgs.tree-sitter.builtGrammars.tree-sitter-nix}/parser";
            NIX_MANAGER_TREE_SITTER = "${pkgs.tree-sitter}/lib/libtree-sitter.so";
          };
        }
      );

      checks = forAllSystems (
        system:
        import ./checks.nix {
          pkgs = pkgsFor system;
          inherit self system;
        }
      );

      formatter = forAllSystems (system: (pkgsFor system).nixfmt-tree);
    };
}
