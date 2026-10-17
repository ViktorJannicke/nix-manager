# A smoke test of real speech: Orca speaks through speech-dispatcher's stock
# espeak-ng setup into a silent virtual sound card, the test records it, and
# whisper.cpp turns the recording back into text. The recording is copied to
# the test output.
{
  pkgs,
  self,
  runic ? false,
}:
let
  userAccount = pkgs.path + "/nixos/tests/common/user-account.nix";
  # The model nixpkgs' local-ai tests use: small, English only.
  model = pkgs.fetchurl {
    url = "https://huggingface.co/ggerganov/whisper.cpp/resolve/main/ggml-tiny.en-q5_1.bin";
    hash = "sha256-x3xXZvHO8JtrfUfyG1Rsvd1BV4hrO11tT3CekeZsfCs=";
  };
in
pkgs.testers.runNixOSTest {
  name = "nix-manager-speech-audio" + pkgs.lib.optionalString runic "-runic";

  nodes.machine =
    { lib, pkgs, ... }:
    {
      imports = [ (import ./machine.nix { inherit self userAccount runic; }) ];

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
        # The tiny model sometimes mishears "Screen" ("Spring reader on").
        assert "reader on" in heard.lower(), "the recording does not say 'reader on'"
  ''
  # The Runic port's Window: a Tab stop in its web page, spoken by Orca
  # through espeak-ng.
  + pkgs.lib.optionalString runic ''

    with subtest("whisper.cpp hears a Tab stop in the dashboard"):
        machine.succeed("lab-user systemd-run --user --unit nix-manager nix-manager ui")
        machine.wait_until_succeeds("lab-user lab-atspi nix-manager | grep -q \"landmark 'Sections'\"", timeout=timedelta(seconds=180))
        machine.sleep(10)
        machine.succeed(
            "lab-user systemd-run --user --unit record-window parecord --device=@DEFAULT_MONITOR@"
            " --rate=16000 --channels=1 --file-format=wav /run/lab/window.wav"
        )
        machine.sleep(2)
        machine.send_key("tab")
        machine.sleep(6)
        machine.succeed("lab-user systemctl --user kill -s INT record-window")
        machine.wait_until_fails("lab-user systemctl --user is-active record-window")
        machine.copy_from_machine("/run/lab/window.wav")
        heard = machine.succeed("lab-transcribe /run/lab/window.wav 2>/dev/null")
        print(f"whisper.cpp heard: {heard.strip()!r}")
        # The first stop is the skip link, or Overview when the page put
        # focus on the skip link itself; the tiny model mishears words.
        assert any(word in heard.lower() for word in ["content", "overview", "button"]), "the recording has no Tab stop"

    with subtest("Orca reads the navigation's list without a WebKitGTK abort"):
        # Tab into the navigation, whose list item aborted WebKitGTK's web process when Orca read it (README.md). What
        # Orca says there ("list with 8 items") is checked exactly in desktop-a11y-runic's transcript; whisper.cpp's
        # tiny model is too unsure of it to assert on.
        if "'Skip to content'" not in machine.execute("lab-user lab-atspi nix-manager | grep -w focused")[1]:
            machine.send_key("shift-tab")
            machine.sleep(6)
        machine.succeed(
            "lab-user systemd-run --user --unit record-list parecord --device=@DEFAULT_MONITOR@"
            " --rate=16000 --channels=1 --file-format=wav /run/lab/list.wav"
        )
        machine.sleep(2)
        machine.send_key("tab")
        machine.sleep(10)
        machine.succeed("lab-user systemctl --user kill -s INT record-list")
        machine.wait_until_fails("lab-user systemctl --user is-active record-list")
        machine.copy_from_machine("/run/lab/list.wav")
        print(f"whisper.cpp heard: {machine.succeed('lab-transcribe /run/lab/list.wav 2>/dev/null').strip()!r}")
        machine.succeed("lab-user lab-atspi nix-manager | grep -q \"landmark 'Sections'\"")
        assert "WebKitWebProces" not in machine.succeed("coredumpctl list --no-legend 2>/dev/null || true"), "WebKitWebProcess aborted"
  '';
}
