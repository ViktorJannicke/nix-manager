{
  description = "machine: system and home configuration";

  # The lab locks these to nix-manager's own inputs and fills in its store
  # path and the system, so the VM evaluates offline with exactly the inputs
  # the host prebuilt the configurations with.
  inputs = {
    nixpkgs.url = "github:nixos/nixpkgs/nixos-unstable";
    home-manager = {
      url = "github:nix-community/home-manager";
      inputs.nixpkgs.follows = "nixpkgs";
    };
    nix-manager = {
      url = "path:@nix-manager@";
      inputs.nixpkgs.follows = "nixpkgs";
      inputs.home-manager.follows = "home-manager";
    };
  };

  outputs =
    { nix-manager, ... }:
    let
      lab = nix-manager.lib.lab."@system@";
    in
    {
      nixosConfigurations.machine = lab.nixos ./configuration.nix;
      homeConfigurations."alice@machine" = lab.home ./home.nix;
    };
}
