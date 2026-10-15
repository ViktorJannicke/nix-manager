{
  description = "Nix Manager: CLI, desktop dashboard and tray for NixOS and Home Manager";

  inputs = {
    nixpkgs.url = "github:nixos/nixpkgs/nixos-unstable";
    home-manager = {
      url = "github:nix-community/home-manager";
      inputs.nixpkgs.follows = "nixpkgs";
    };
    # The Runic port in runic/: the .NET development shell (and later the application builder).
    runic-sdk = {
      url = "github:Runic-Artifex/runic-sdk/v0.8.0-preview.3";
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
          manager = pkgs.callPackage ./package.nix {
            homeManager = home-manager.packages.${system}.home-manager;
            nixGrammar = pkgs.pkgsCross.wasm32-wasip1.tree-sitter.builtGrammars.tree-sitter-nix;
          };
        in
        {
          default = manager;
          nix-manager = manager;
          npm-deps = manager.npmDeps;
          # The Runic port in runic/; its NuGet lock files cover linux-x64 and linux-arm64.
          runic = pkgs.callPackage ./runic/package.nix {
            buildRunicApplication = runic-sdk.lib.buildRunicApplication pkgs;
            homeManager = home-manager.packages.${system}.home-manager;
          };
          # Slow VM test, kept out of `nix flake check`; see nix/lab.
          desktop-a11y = import ./nix/lab/desktop-a11y.nix { inherit pkgs self; };
          speech-audio = import ./nix/lab/speech-audio.nix { inherit pkgs self; };
          # The same with the Runic port installed (programs.nix-manager.runic.enable).
          desktop-a11y-runic = import ./nix/lab/desktop-a11y.nix {
            inherit pkgs self;
            runic = true;
          };
          speech-audio-runic = import ./nix/lab/speech-audio.nix {
            inherit pkgs self;
            runic = true;
          };
          hyprland-sandbox = import ./nix/hyprland-sandbox { inherit pkgs; };
          # Runic against Bun: start-up, memory and size, in the sandbox; see runic/COMPARISON.md.
          compare = import ./nix/compare {
            inherit pkgs;
            runic = self.packages.${system}.runic;
            bun = self.packages.${system}.default;
            sandbox = self.packages.${system}.hyprland-sandbox;
          };
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
          lab-runic = import ./nix/lab/lab.nix {
            inherit pkgs;
            name = "nix-manager-lab-runic";
            test = self.packages.${system}.desktop-a11y-runic;
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
          lab-runic = {
            type = "app";
            program = nixpkgs.lib.getExe self.packages.${system}.lab-runic;
            meta.description = "Drive the GNOME and Orca test VM with the Runic port installed";
          };
          lab-apply = {
            type = "app";
            program = nixpkgs.lib.getExe self.packages.${system}.lab-apply;
            meta.description = "Drive the test VM with a configuration checkout that can apply for real";
          };
          compare = {
            type = "app";
            program = nixpkgs.lib.getExe self.packages.${system}.compare;
            meta.description = "Measure the Runic port against the Bun version in the headless sandbox";
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
          # The Bun application's tools, plus the .NET 10 SDK for the Runic port in runic/. No Runic host
          # yet: the port has no Window (W300-030), and GTK on LD_LIBRARY_PATH would reach the WebUI tests.
          default = runic-sdk.lib.mkDevShell pkgs {
            hosts = [ ];
            packageManagers = [ "bun" ];
            inherit (pkgs) nodejs;
            packages = with pkgs; [
              dbus
              python3
            ];
            NIX_MANAGER_WEBUI_LIBRARY = "${self.packages.${system}.default.webui}/lib/libwebui-2.so";
            NIX_MANAGER_NIX_GRAMMAR = "${self.packages.${system}.default.nixGrammar}/parser.wasm";
            # The Runic port reads Nix files with the same grammar, built as a native library.
            NIX_MANAGER_NIX_PARSER = "${pkgs.tree-sitter.builtGrammars.tree-sitter-nix}/parser";
            NIX_MANAGER_TREE_SITTER = "${pkgs.tree-sitter}/lib/libtree-sitter.so";
          };
          # The Runic port's dashboard Window (nix-manager ui) on Runic Desktop: GTK and WebKitGTK on the library
          # path, and Chromium for headless browser checks in serve-only mode.
          runic = runic-sdk.lib.mkDevShell pkgs {
            hosts = [ "desktop" ];
            browserTests = true;
            packageManagers = [ "bun" ];
            inherit (pkgs) nodejs;
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
