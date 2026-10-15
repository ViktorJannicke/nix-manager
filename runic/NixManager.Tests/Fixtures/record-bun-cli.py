"""Record the Bun version's command-line outputs that the Runic port must keep (Fixtures/bun-cli.json).

Run from the repository root in the development shell, with the Bun dependencies installed:

    nix develop -c python3 runic/NixManager.Tests/Fixtures/record-bun-cli.py

Every case runs read-only against a temporary checkout and a copy of Fixtures/bun-state, real operation records
of the Bun version. The checkout path is written as <flake>; CliParityTests runs the same cases with the C#
command line and compares the JSON documents, exit codes and error lines.
"""
import json
import os
from pathlib import Path
import shutil
import subprocess
import tempfile

HERE = Path(__file__).resolve().parent
ROOT = HERE.parents[2]
CASES = HERE / "cli-cases.json"


def prepare(root: Path) -> dict:
    (root / "flake.nix").write_text("{}")
    (root / "hosts.json").write_text(json.dumps({"test": {
        "homeConfiguration": "user-test", "nixosConfiguration": "test-machine",
        "nixosHostName": "test-machine", "system": "x86_64-linux"}}))
    state = root / "state" / "nix-manager"
    shutil.copytree(HERE / "bun-state", state)
    for file in state.rglob("*.json"):
        file.write_text(file.read_text().replace("/home/user/my-flakes", str(root)))
    profile = root / "state" / "nix" / "profiles" / "home-manager"
    profile.parent.mkdir(parents=True)
    profile.touch()
    return os.environ | {
        "NIX_MANAGER_HOSTS": str(root / "hosts.json"), "NIX_MANAGER_FLAKE": str(root),
        "NIX_MANAGER_HOST": "test", "XDG_STATE_HOME": str(root / "state"), "NO_COLOR": "1",
        # Nothing may run: a missing tool fails instead.
        "PATH": "/nonexistent",
    }


def main():
    cases = json.loads(CASES.read_text())
    results = []
    bun = shutil.which("bun")
    for case in cases:
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory).resolve()
            env = prepare(root)
            result = subprocess.run([bun, str(ROOT / "src/main.ts"), *case["args"]],
                                    env=env, text=True, capture_output=True, timeout=60)
            stderr = [line for line in result.stderr.splitlines() if line.strip()]
            results.append({
                "name": case["name"],
                "args": case["args"],
                "exitCode": result.returncode,
                "stdout": result.stdout.replace(str(root), "<flake>"),
                "error": next((line.replace(str(root), "<flake>") for line in stderr if line.startswith("Error: ")), None),
            })
    (HERE / "bun-cli.json").write_text(json.dumps(results, indent=2, ensure_ascii=False) + "\n")
    print(f"Recorded {len(results)} cases.")


if __name__ == "__main__":
    main()
