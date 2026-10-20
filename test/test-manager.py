"""Exercise the actual CLI with mocked build tools; never activate the machine."""
import json
import os
from pathlib import Path
import shlex
import subprocess
import shutil
import tempfile
import unittest

# The program under test, such as the package's executable without its wrapper (checks.nix).
COMMAND = shlex.split(os.environ["NIX_MANAGER_TEST_COMMAND"])

class ManagerTests(unittest.TestCase):
    def invoke(self, args, fail=""):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            (root / "flake.nix").touch()
            (root / "hosts.json").write_text(json.dumps({"test": {
                "homeConfiguration": "user-test", "nixosConfiguration": "test-machine",
                "nixosHostName": "test-machine", "system": "x86_64-linux"}}))
            bindir = root / "bin"
            bindir.mkdir()
            bash = shutil.which("bash")
            for name in ("home-manager", "nix-env", "nix-store"):
                executable = bindir / name
                executable.write_text(f'#!{bash}\n'
                    'printf "%s\\n" "${0##*/} $*" >> "$CALLS"\n'
                    '[[ "${0##*/} ${1:-}" != "$FAIL_COMMAND" ]]\n')
                executable.chmod(0o755)
            # `nix build --out-link` produces an output whose activate script
            # records its invocation, so activation of the exact output is visible.
            activate = root / "activate"
            activate.write_text(f'#!{bash}\n'
                'printf "%s\\n" "activate $*" >> "$CALLS"\n'
                '[[ "activate ${1:-}" != "$FAIL_COMMAND" ]]\n')
            activate.chmod(0o755)
            (bindir / "nix").write_text(f'#!{bash}\n'
                'printf "%s\\n" "nix $*" >> "$CALLS"\n'
                'link=""; prev=""\n'
                'for arg in "$@"; do [[ $prev == --out-link ]] && link=$arg; prev=$arg; done\n'
                '[[ "nix $1 ${link##*/}" == "$FAIL_COMMAND" ]] && exit 1\n'
                'if [[ -n $link ]]; then\n'
                '  out="$OUTPUTS/${link##*/}"; mkdir -p "$out"\n'
                f'  cp {activate} "$out/activate"; ln -sfn "$out" "$link"\n'
                'fi\n')
            (bindir / "nix").chmod(0o755)
            (root / "outputs").mkdir()
            state = root / "state"
            profile = state / "nix/profiles/home-manager"
            profile.parent.mkdir(parents=True)
            profile.touch()
            env = os.environ | {
                "PATH": str(bindir) + ":" + os.environ["PATH"],
                "NIX_MANAGER_HOSTS": str(root / "hosts.json"),
                "NIX_MANAGER_FLAKE": str(root), "NIX_MANAGER_HOST": "test",
                "FAIL_COMMAND": fail, "CALLS": str(root / "calls"),
                "XDG_STATE_HOME": str(state), "OUTPUTS": str(root / "outputs"),
            }
            result = subprocess.run([*COMMAND, *args],
                                    env=env, text=True, capture_output=True)
            calls = (root / "calls").read_text().splitlines() if (root / "calls").exists() else []
            return result, calls, str(profile), str(state / "nix-manager/build")

    def test_prebuild_failure_prevents_activation_and_cleanup(self):
        for fail in ("nix build home", "nix build system"):
            result, calls, *_ = self.invoke(["cleanup", "switch"], fail=fail)
            self.assertNotEqual(result.returncode, 0)
            self.assertTrue(calls)
            self.assertTrue(all(call.startswith("nix build ") for call in calls), calls)

    def test_home_arguments_forwarded_and_cleanup_uses_home_profile(self):
        result, calls, profile, build = self.invoke(["home", "cleanup", "switch", "--", "--show-trace"])
        self.assertEqual(result.returncode, 0, result.stderr)
        self.assertTrue(calls[0].startswith(f"nix build --out-link {build}/home "), calls[0])
        self.assertTrue(calls[0].endswith("#homeConfigurations.user-test.activationPackage --show-trace"), calls[0])
        self.assertEqual(calls[1], f"nix-env --profile {profile} --set {build}/home")
        self.assertEqual(calls[2], "activate --driver-version 1")
        self.assertEqual(calls[3], f"nix-env --profile {profile} --delete-generations +3")
        self.assertEqual(calls[4], "nix-store --gc")
        # The build out-links are removed once the profile references the output.
        self.assertFalse(Path(build).exists())

    def test_activation_failure_prevents_cleanup(self):
        result, calls, *_ = self.invoke(["home", "cleanup", "switch"], fail="activate --driver-version")
        self.assertNotEqual(result.returncode, 0)
        self.assertEqual(len(calls), 3)

    def test_dry_run_order_and_one_lock_update(self):
        result, calls, *_ = self.invoke(["--dry-run", "update", "cleanup", "switch"])
        self.assertEqual(result.returncode, 0, result.stderr)
        self.assertEqual(calls, [])
        output = result.stdout
        self.assertEqual(output.count("nix flake update"), 1)
        steps = ["#homeConfigurations", "#nixosConfigurations", "--set", "/activate", "--privileged activate-output switch", "--delete-generations", "--privileged gc"]
        positions = [output.index(step) for step in steps]
        self.assertEqual(positions, sorted(positions))

    def test_boot_defaults_to_system_and_rejects_home(self):
        result, calls, *_ = self.invoke(["--dry-run", "boot"])
        self.assertEqual(result.returncode, 0, result.stderr)
        self.assertNotIn("homeConfigurations", result.stdout)
        self.assertIn("activate-output boot", result.stdout)
        self.assertEqual(calls, [])
        self.assertNotEqual(self.invoke(["home", "boot"])[0].returncode, 0)

    def test_invalid_combinations_fail_without_side_effects(self):
        for args in (["switch", "boot"], ["home", "all", "switch"], ["cleanup", "build"],
                     ["update", "rollback"], ["cleanup", "rollback"], ["--force", "switch"],
                     ["system", "t3code-update"], ["--host", "missing", "switch"]):
            with self.subTest(args=args):
                result, calls, *_ = self.invoke(args)
                self.assertNotEqual(result.returncode, 0)
                self.assertEqual(calls, [])

    def test_home_rollback_uses_native_cli(self):
        result, calls, *_ = self.invoke(["home", "rollback"])
        self.assertEqual(result.returncode, 0, result.stderr)
        self.assertEqual(calls, ["home-manager switch --rollback"])

    def test_software_commands_use_the_draft_without_building(self):
        result, calls, *_ = self.invoke(["software", "pending"])
        self.assertEqual(result.returncode, 0, result.stderr)
        self.assertIn("No pending edits.", result.stdout)
        for args in (["software", "prepare"], ["software", "drop", "1"], ["software", "apply"],
                     ["software", "add", "--file", "../x.nix"], ["software", "frobnicate"]):
            with self.subTest(args=args):
                result, calls, *_ = self.invoke(args)
                self.assertNotEqual(result.returncode, 0)
                self.assertEqual(calls, [])

if __name__ == "__main__":
    unittest.main()
