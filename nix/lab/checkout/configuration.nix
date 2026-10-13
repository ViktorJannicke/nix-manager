{ pkgs, ... }:
{
  environment.systemPackages = [ pkgs.git ];
  system.stateVersion = "26.05";
}
