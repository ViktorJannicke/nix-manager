{
  description = "Nix Manager: CLI, desktop dashboard and tray for NixOS and Home Manager";

  inputs = {
    nixpkgs.url = "github:nixos/nixpkgs/nixos-unstable";
    home-manager = {
      url = "github:nix-community/home-manager";
      inputs.nixpkgs.follows = "nixpkgs";
    };
  };

  outputs =
    {
      self,
      nixpkgs,
      home-manager,
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
          # Slow VM test, kept out of `nix flake check`; see nix/lab.
          desktop-a11y = import ./nix/lab/desktop-a11y.nix { inherit pkgs self; };
          speech-audio = import ./nix/lab/speech-audio.nix { inherit pkgs self; };
          lab = import ./nix/lab/lab.nix {
            inherit pkgs;
            test = self.packages.${system}.desktop-a11y;
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
          default = pkgs.mkShell {
            packages = with pkgs; [
              bun
              nodejs
              dbus
              python3
            ];
            NIX_MANAGER_WEBUI_LIBRARY = "${self.packages.${system}.default.webui}/lib/libwebui-2.so";
            NIX_MANAGER_NIX_GRAMMAR = "${self.packages.${system}.default.nixGrammar}/parser.wasm";
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
