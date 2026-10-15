"""Record the Bun version's software edit commands, which read the configuration with nix (Fixtures/bun-software-edits.json).

Run from the repository root in the development shell:

    python3 runic/NixManager.Tests/Fixtures/record-bun-software-edits.py

Each case of Fixtures/software-edit-cases.json runs like a case of record-bun-cli.py, with a fake nix first on PATH:
`nix flake metadata` names the checkout as the flake source, and `nix eval` prints the case's "eval" document
(<flake> is the checkout); nothing else exists. SoftwareEditTests answers the C# command line's commands the same
way and compares standard output, the notes on standard error, the error line, the exit code and the saved draft.
"""
import importlib.util
import json
import os
from pathlib import Path
import shutil
import subprocess
import tempfile

HERE = Path(__file__).resolve().parent
spec = importlib.util.spec_from_file_location("record_bun_cli", HERE / "record-bun-cli.py")
cli = importlib.util.module_from_spec(spec)
spec.loader.exec_module(cli)

FAKE_NIX = """#!/bin/sh
if [ "$1 $2" = "flake metadata" ]; then printf '{"path":"%s"}' "$FAKE_SOURCE"; exit 0; fi
if [ "$1" = "eval" ]; then cat "$FAKE_EVAL"; exit 0; fi
echo "unexpected nix $*" >&2; exit 1
"""


def main():
    cases = json.loads((HERE / "software-edit-cases.json").read_text())
    shell = shutil.which("sh")
    bun = shutil.which("bun")
    results = []
    for case in cases:
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory).resolve()
            checkout = root / "flake"
            checkout.mkdir()
            env = cli.prepare(checkout, case.get("files", {}))
            bin_dir = root / "bin"
            bin_dir.mkdir()
            (bin_dir / "nix").write_text(FAKE_NIX.replace("#!/bin/sh", f"#!{shell}").replace("cat ", f"{shutil.which('cat')} "))
            (bin_dir / "nix").chmod(0o755)
            (root / "eval.json").write_text(json.dumps(case["eval"]).replace("<flake>", str(checkout)))
            env |= {"PATH": str(bin_dir), "FAKE_SOURCE": str(checkout), "FAKE_EVAL": str(root / "eval.json")}
            result = subprocess.run([bun, str(cli.ROOT / "src/main.ts"), *case["args"]],
                                    env=env, text=True, capture_output=True, timeout=60)
            draft = checkout / "state" / "nix-manager" / "draft.json"
            norm = lambda text: text.replace(str(checkout), "<flake>")
            stderr = [norm(line) for line in result.stderr.splitlines() if line.strip()]
            results.append({
                "name": case["name"],
                "args": case["args"],
                "exitCode": result.returncode,
                "stdout": norm(result.stdout),
                "stderr": stderr,
                "error": next((line for line in stderr if line.startswith("Error: ")), None),
                "draft": json.loads(norm(draft.read_text()))["changes"] if draft.exists() else None,
            })
    (HERE / "bun-software-edits.json").write_text(json.dumps(results, indent=2, ensure_ascii=False) + "\n")
    print(f"Recorded {len(results)} cases into bun-software-edits.json.")


if __name__ == "__main__":
    main()
