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
          };
        in
        {
          default = manager;
          nix-manager = manager;
          npm-deps = manager.npmDeps;
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
