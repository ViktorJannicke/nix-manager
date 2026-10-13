# Prints the accessibility tree of every running application, one node per
# line: depth, role, name, an entry's text and the states a screen reader
# announces.
import sys

import gi

gi.require_version("Atspi", "2.0")
from gi.repository import Atspi

STATES = ["focused", "focusable", "checked", "selected", "expanded", "sensitive"]


def states(node):
    set_ = node.get_state_set()
    return [s for s in STATES if set_.contains(getattr(Atspi.StateType, s.upper()))]


def walk(node, depth, limit):
    if depth > limit:
        return
    try:
        role = node.get_role_name()
        name = node.get_name() or ""
    except Exception:
        return
    value = ""
    # Accessible.get_text shadows the Text interface's method; call it directly.
    if role == "entry" and node.get_text_iface() is not None:
        value = f" value={Atspi.Text.get_text(node, 0, -1)!r}"
    print(f"{'  ' * depth}{role} {name!r}{value} {' '.join(states(node))}".rstrip())
    for i in range(node.get_child_count()):
        child = node.get_child_at_index(i)
        if child is not None:
            walk(child, depth + 1, limit)


desktop = Atspi.get_desktop(0)
wanted = sys.argv[1].lower() if len(sys.argv) > 1 else ""
for i in range(desktop.get_child_count()):
    app = desktop.get_child_at_index(i)
    if app is not None and wanted in (app.get_name() or "").lower():
        walk(app, 0, 40)
