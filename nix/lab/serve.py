# Test script for the interactive lab: boots the desktop-a11y VM, then answers
# requests that nix/lab/lab.sh leaves in LAB_DIR, one at a time, until "stop".
import json
import os
import shlex
import time
from datetime import timedelta

lab = os.environ["LAB_DIR"]
request_path = f"{lab}/request.json"
response_path = f"{lab}/response.json"


def user(command):
    return machine.execute(f"lab-user {command}")


def handle(kind, args):
    if kind == "key":
        for key in args:
            machine.send_key(key)
        return 0, ""
    if kind == "type":
        machine.send_chars(" ".join(args))
        return 0, ""
    if kind == "shot":
        name = args[0] if args else time.strftime("shot-%H%M%S")
        machine.screenshot(name)
        return 0, f"{machine.out_dir}/{name}.png\n"
    if kind == "tree":
        return user(f"lab-atspi {shlex.quote(args[0] if args else '')}")
    if kind == "focused":
        return user(f"lab-atspi {shlex.quote(args[0] if args else '')} | grep -w focused")
    if kind == "speech":
        status, text = machine.execute("cat /run/lab/spoken.txt")
        if args[:1] == ["--clear"]:
            machine.succeed("truncate -s 0 /run/lab/spoken.txt")
        return status, text
    if kind == "sh":
        return machine.execute(" ".join(args))
    if kind == "user":
        return user(" ".join(args))
    if kind == "copy":
        machine.copy_from_machine(args[0])
        return 0, f"{machine.out_dir}/{os.path.basename(args[0])}\n"
    return 2, f"unknown request {kind}\n"


machine.wait_for_unit("display-manager.service")
machine.wait_for_file("/run/user/1000/wayland-0")
machine.wait_for_unit("default.target", "alice")
machine.wait_until_succeeds(
    "lab-user gdbus call --session -d org.gnome.Shell -o /org/gnome/Shell"
    " -m org.gnome.Shell.Eval Main.layoutManager._startingUp | grep -q 'true,..false'",
    timeout=timedelta(minutes=5),
)
machine.send_key("esc")
open(f"{lab}/ready", "w").close()

while True:
    if not os.path.exists(request_path):
        time.sleep(0.1)
        continue
    with open(request_path) as file:
        text = file.read()
    os.remove(request_path)
    request = {}
    try:
        request = json.loads(text)
        if request["kind"] == "stop":
            break
        status, output = handle(request["kind"], request["args"])
    except Exception as error:
        status, output = 1, f"{type(error).__name__}: {error}\n"
    with open(f"{response_path}.tmp", "w") as file:
        json.dump({"id": request.get("id"), "status": status, "output": output}, file)
    os.rename(f"{response_path}.tmp", response_path)
