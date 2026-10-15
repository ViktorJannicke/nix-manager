"""Record the Bun version's command-line outputs that the Runic port must keep (Fixtures/bun-cli.json).

Run from the repository root in the development shell, with the Bun dependencies installed:

    nix develop -c python3 runic/NixManager.Tests/Fixtures/record-bun-cli.py

Every case runs read-only against a temporary checkout and a copy of Fixtures/bun-state, real operation records
of the Bun version. The checkout path is written as <flake>; CliParityTests runs the same cases with the C#
command line and compares the JSON documents, exit codes and error lines.

Each Fixtures/cli-cases*.json is recorded into the matching Fixtures/bun-cli*.json (cli-cases-software.json into
bun-cli-software.json). A case may add files before it runs: "files" maps paths relative to the checkout (state
files under state/nix-manager/) to their text, in which <flake> stands for the checkout. Pass case files as
arguments to record only those.
"""
import json
import os
from pathlib import Path
import shutil
import subprocess
import tempfile

HERE = Path(__file__).resolve().parent
ROOT = HERE.parents[2]
import sys


def prepare(root: Path, files: dict) -> dict:
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
    for name, text in files.items():
        path = root / name
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_text(text.replace("<flake>", str(root)))
    return os.environ | {
        "NIX_MANAGER_HOSTS": str(root / "hosts.json"), "NIX_MANAGER_FLAKE": str(root),
        "NIX_MANAGER_HOST": "test", "XDG_STATE_HOME": str(root / "state"), "NO_COLOR": "1",
        # Nothing may run: a missing tool fails instead.
        "PATH": "/nonexistent",
    }


def record(cases_file: Path):
    cases = json.loads(cases_file.read_text())
    results = []
    bun = shutil.which("bun")
    for case in cases:
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory).resolve()
            env = prepare(root, case.get("files", {}))
            result = subprocess.run([bun, str(ROOT / "src/main.ts"), *case["args"]],
                                    env=env, text=True, capture_output=True, timeout=60)
            stderr = [line for line in result.stderr.splitlines() if line.strip()]
            results.append({
                "name": case["name"],
                "args": case["args"],
                "exitCode": result.returncode,
                # The repository path only appears in the privileged entry point, which the tests normalise.
                "stdout": result.stdout.replace(str(root), "<flake>").replace(str(ROOT), "<repo>"),
                "error": next((line.replace(str(root), "<flake>").replace(str(ROOT), "<repo>")
                               for line in stderr if line.startswith("Error: ")), None),
            })
    target = HERE / cases_file.name.replace("cli-cases", "bun-cli")
    target.write_text(json.dumps(results, indent=2, ensure_ascii=False) + "\n")
    print(f"Recorded {len(results)} cases into {target.name}.")


def main():
    for cases_file in [Path(arg).resolve() for arg in sys.argv[1:]] or sorted(HERE.glob("cli-cases*.json")):
        record(cases_file)


if __name__ == "__main__":
    main()
