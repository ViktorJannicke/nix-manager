"""Exercise the actual CLI with mocked build tools; never activate the machine."""
import json
import os
from pathlib import Path
import subprocess
import shutil
import tempfile
import unittest

MANAGER = Path(__file__).resolve().parents[1] / "src/main.ts"

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
            for name in ("home-manager", "nixos-rebuild", "nix-env", "nix-store"):
                executable = bindir / name
                executable.write_text(f'#!{shutil.which("bash")}\n'
                    'printf "%s\\n" "${0##*/} $*" >> "$CALLS"\n'
                    '[[ "${0##*/} ${1:-}" != "$FAIL_COMMAND" ]]\n')
                executable.chmod(0o755)
            state = root / "state"
            profile = state / "nix/profiles/home-manager"
            profile.parent.mkdir(parents=True)
            profile.touch()
            env = os.environ | {
                "PATH": str(bindir) + ":" + os.environ["PATH"],
                "NIX_MANAGER_HOSTS": str(root / "hosts.json"),
                "NIX_MANAGER_FLAKE": str(root), "NIX_MANAGER_HOST": "test",
                "FAIL_COMMAND": fail, "CALLS": str(root / "calls"),
                "XDG_STATE_HOME": str(state),
            }
            # Test the packaged Bun entry point while keeping native tools mocked.
            command = ["bun", os.environ.get("NIX_MANAGER_TEST_SCRIPT", str(MANAGER))]
            result = subprocess.run([*command, *args],
                                    env=env, text=True, capture_output=True)
            calls = (root / "calls").read_text().splitlines() if (root / "calls").exists() else []
            return result, calls, str(profile)

    def test_prebuild_failure_prevents_activation_and_cleanup(self):
        for fail in ("home-manager build", "nixos-rebuild build"):
            result, calls, _ = self.invoke(["cleanup", "switch"], fail=fail)
            self.assertNotEqual(result.returncode, 0)
            self.assertTrue(calls)
            self.assertTrue(all(" build " in call for call in calls), calls)

    def test_home_arguments_forwarded_and_cleanup_uses_home_profile(self):
        result, calls, profile = self.invoke(["home", "cleanup", "switch", "--", "--show-trace"])
        self.assertEqual(result.returncode, 0, result.stderr)
        self.assertIn("--show-trace", calls[0])
        self.assertIn("--show-trace", calls[1])
        self.assertIn(" switch ", calls[1])
        self.assertEqual(calls[2], f"nix-env --profile {profile} --delete-generations +3")
        self.assertEqual(calls[3], "nix-store --gc")

    def test_activation_failure_prevents_cleanup(self):
        result, calls, _ = self.invoke(["home", "cleanup", "switch"], fail="home-manager switch")
        self.assertNotEqual(result.returncode, 0)
        self.assertEqual(len(calls), 2)

    def test_dry_run_order_and_one_lock_update(self):
        result, calls, _ = self.invoke(["--dry-run", "update", "cleanup", "switch"])
        self.assertEqual(result.returncode, 0, result.stderr)
        self.assertEqual(calls, [])
        output = result.stdout
        self.assertEqual(output.count("nix flake update"), 1)
        steps = ["home-manager build", "nixos-rebuild build", "home-manager switch", "--privileged switch", "nix-env --profile", "--privileged gc"]
        positions = [output.index(step) for step in steps]
        self.assertEqual(positions, sorted(positions))

    def test_boot_defaults_to_system_and_rejects_home(self):
        result, calls, _ = self.invoke(["--dry-run", "boot"])
        self.assertEqual(result.returncode, 0, result.stderr)
        self.assertNotIn("home-manager", result.stdout)
        self.assertEqual(calls, [])
        self.assertNotEqual(self.invoke(["home", "boot"])[0].returncode, 0)

    def test_invalid_combinations_fail_without_side_effects(self):
        for args in (["switch", "boot"], ["home", "all", "switch"], ["cleanup", "build"],
                     ["update", "rollback"], ["cleanup", "rollback"], ["--force", "switch"],
                     ["system", "t3code-update"], ["--host", "missing", "switch"]):
            with self.subTest(args=args):
                result, calls, _ = self.invoke(args)
                self.assertNotEqual(result.returncode, 0)
                self.assertEqual(calls, [])

    def test_home_rollback_uses_native_cli(self):
        result, calls, _ = self.invoke(["home", "rollback"])
        self.assertEqual(result.returncode, 0, result.stderr)
        self.assertEqual(calls, ["home-manager switch --rollback"])

if __name__ == "__main__":
    unittest.main()
