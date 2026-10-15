"""WebKitGTK 2.54.1 aborts its web process when a list item's text attributes are asked for twice.

Open crash.html in a WebKitGTK browser with the accessibility bus on (for example WebKitGTK's own
libexec/webkit2gtk-4.1/MiniBrowser, served over HTTP), then run this script with python3 and PyGObject
(GI_TYPELIB_PATH must reach Atspi-2.0). The first org.a11y.atspi.Text.GetAttributeRun(0, false) on a list item
returns (no attributes, 0, 0); the second aborts WebKitWebProcess with SIGABRT:

  WTF::CrashOnOverflow::crash()
  WebCore::AccessibilityObjectAtspi::textAttributesWithUTF8Offset(std::optional<int>, bool) const

The list item's text is one U+FFFC for its button, so it is a 16-bit string and offsetMapping() builds a table
of two entries. textAttributes() computes the run from the button's own text renderers, whose positions give
offsets past the item's one character; UTF16OffsetToUTF8() then indexes the table out of bounds. Needed: the
list item without a marker (list-style: none), a flex button, and whitespace around its span. WebKit main still
indexes the table without a bounds check (UTF16OffsetToUTF8 in AccessibilityObjectTextAtspi.cpp).

Orca asks for these attributes when it reads a line, so a screen reader user crashes such a page by pressing Tab.
Nix Manager avoids it with display: contents on its navigation's list items (runic/Frontend/src/App.svelte).
"""
import gi

gi.require_version("Atspi", "2.0")
from gi.repository import Atspi


def list_items(node, found):
    if node.get_role_name() == "list item":
        found.append(node)
    for index in range(node.get_child_count()):
        child = node.get_child_at_index(index)
        if child is not None:
            list_items(child, found)
    return found


desktop = Atspi.get_desktop(0)
for index in range(desktop.get_child_count()):
    application = desktop.get_child_at_index(index)
    items = list_items(application, []) if application is not None else []
    if items:
        item = items[0]
        print("text:", repr(Atspi.Text.get_text(item, 0, -1)))
        print("first:", Atspi.Text.get_attribute_run(item, 0, False))
        print("second:", Atspi.Text.get_attribute_run(item, 0, False))
        break
