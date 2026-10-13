# configuration.nix after adding GNU Hello.
{ pkgs, ... }:
{
  environment.systemPackages = [
    pkgs.git
    pkgs.hello
  ];
  system.stateVersion = "26.05";
}
