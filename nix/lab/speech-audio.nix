# A smoke test of real speech: Orca speaks through speech-dispatcher's stock
# espeak-ng setup into a silent virtual sound card, the test records it, and
# whisper.cpp turns the recording back into text. The recording is copied to
# the test output.
{ pkgs, self }:
let
  userAccount = pkgs.path + "/nixos/tests/common/user-account.nix";
  # The model nixpkgs' local-ai tests use: small, English only.
  model = pkgs.fetchurl {
    url = "https://huggingface.co/ggerganov/whisper.cpp/resolve/main/ggml-tiny.en-q5_1.bin";
    hash = "sha256-x3xXZvHO8JtrfUfyG1Rsvd1BV4hrO11tT3CekeZsfCs=";
  };
in
pkgs.testers.runNixOSTest {
  name = "nix-manager-speech-audio";

  nodes.machine =
    { lib, pkgs, ... }:
    {
      imports = [ (import ./machine.nix { inherit self userAccount; }) ];

      services.speechd = {
        config = lib.mkForce null;
        modules = lib.mkForce { };
      };
      # speech-dispatcher has no PipeWire output in nixpkgs and falls back to
      # libao, which picks PulseAudio unless configured, as on a real install.
      environment.etc."libao.conf".enable = lib.mkForce false;
      # A sound card that plays into nothing, so PipeWire has a real output to
      # record from.
      virtualisation.qemu.options = [
        "-audiodev none,id=snd0"
        "-device intel-hda"
        "-device hda-duplex,audiodev=snd0"
      ];
      environment.systemPackages = [
        pkgs.pulseaudio
        # lab-transcribe FILE: print what whisper.cpp hears in a WAV file.
        (pkgs.writeShellScriptBin "lab-transcribe" ''
          exec ${lib.getExe pkgs.whisper-cpp} -m ${model} -l en -nt -f "$1"
        '')
      ];
    };

  testScript = ''
    from datetime import timedelta

    with subtest("GNOME starts with a sound card"):
        machine.wait_for_unit("display-manager.service")
        machine.wait_for_file("/run/user/1000/wayland-0")
        machine.wait_for_unit("default.target", "alice")
        machine.wait_until_succeeds("lab-user pactl list short sinks | grep -q alsa_output", timeout=timedelta(seconds=60))

    with subtest("Orca speaks out loud"):
        machine.succeed(
            "lab-user systemd-run --user --unit record parecord --device=@DEFAULT_MONITOR@"
            " --rate=16000 --channels=1 --file-format=wav /run/lab/speech.wav"
        )
        machine.sleep(2)
        machine.succeed("lab-user gsettings set org.gnome.desktop.a11y.applications screen-reader-enabled true")
        machine.sleep(10)
        machine.succeed("lab-user systemctl --user kill -s INT record")
        machine.wait_until_fails("lab-user systemctl --user is-active record")
        machine.copy_from_machine("/run/lab/speech.wav")

    with subtest("whisper.cpp hears what Orca said"):
        heard = machine.succeed("lab-transcribe /run/lab/speech.wav 2>/dev/null")
        print(f"whisper.cpp heard: {heard.strip()!r}")
        assert "screen reader" in heard.lower(), "the recording does not say 'screen reader'"
  '';
}
