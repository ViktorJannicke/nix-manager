"""Asks every text object of an application for its attribute runs, at every offset, as Orca may.

Usage: probe.py APP [SECONDS]. Waits up to SECONDS (default 60) for a list item in the first application whose name
contains APP, prints the lists and list items as AT-SPI exposes them, then calls
org.a11y.atspi.Text.GetAttributeRun at every offset of every text object, with and without defaults, twice each.
Exits with 0 when every call was answered, 1 when the application stopped answering (its web process aborted),
and 2 when no such application or list item appeared. Every call has a timeout, so a frozen application cannot hang it.
"""
import sys
import time

import gi

gi.require_version("Atspi", "2.0")
from gi.repository import Atspi, GLib

Atspi.set_timeout(3000, 6000)
want = sys.argv[1].lower()
deadline = time.monotonic() + (float(sys.argv[2]) if len(sys.argv) > 2 else 60)


def walk(node, depth=0, out=None):
    out = [] if out is None else out
    out.append((depth, node))
    for index in range(node.get_child_count()):
        child = node.get_child_at_index(index)
        if child is not None:
            walk(child, depth + 1, out)
    return out


def application():
    desktop = Atspi.get_desktop(0)
    for index in range(desktop.get_child_count()):
        candidate = desktop.get_child_at_index(index)
        if candidate is not None and want in (candidate.get_name() or "").lower():
            return candidate
    return None


def text(node):
    try:
        return Atspi.Text.get_text(node, 0, -1)
    except GLib.Error:
        return None


nodes = []
while time.monotonic() < deadline:
    try:
        app = application()
        nodes = walk(app) if app is not None else []
    except GLib.Error:
        nodes = []
    if any(node.get_role_name() == "list item" for _, node in nodes):
        break
    time.sleep(1)
else:
    print(f"no list item in an application named like {want!r}")
    sys.exit(2)

for depth, node in nodes:
    role = node.get_role_name()
    if role in ("list", "list item"):
        print(f"{'  ' * depth}{role} {node.get_name()!r} children={node.get_child_count()} text={text(node)!r}")

calls = 0
try:
    for _, node in nodes:
        try:
            count = Atspi.Text.get_character_count(node)
        except GLib.Error:
            continue
        for offset in range(count + 1):
            for defaults in (False, True):
                for _ in range(2):
                    Atspi.Text.get_attribute_run(node, offset, defaults)
                    calls += 1
except GLib.Error as error:
    print(f"no answer after {calls} attribute runs: {error.message}")
    sys.exit(1)
print(f"answered {calls} attribute runs")
