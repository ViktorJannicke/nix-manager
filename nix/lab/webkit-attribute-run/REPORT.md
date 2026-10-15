# Upstream report: AT-SPI GetAttributeRun aborts the web process (CrashOnOverflow in textAttributesWithUTF8Offset)

Ready to file at https://bugs.webkit.org (component: Accessibility, platform: Linux, GTK and WPE). Nothing has been
filed yet. Run the pages and the probe with `nix build .#webkit-attribute-run` from this repository.

---

**Title:** [ATSPI] GetAttributeRun on a list item aborts the WebProcess: UTF16OffsetToUTF8 indexes past the offset
mapping

**Version:** WebKitGTK 2.54.1. Both flavours abort:
- webkit2gtk-4.1 (GTK 3);
- webkitgtk-6.0 (GTK 4).

The code is unchanged on WebKit main (`Source/WebCore/accessibility/atspi/AccessibilityObjectTextAtspi.cpp`).

**Environment:** NixOS (nixpkgs 26.11.20261006), GNOME 50 on Wayland, at-spi2-core 2.60.6, Orca 50.2. Reproduced with
each flavour's MiniBrowser, and in an application that embeds webkit2gtk-4.1.

**Impact:** a screen reader user crashes such a page by pressing Tab. Orca asks for the text attributes of the line it
reads, and the WebProcess aborts. The view then stays blank or frozen until it is reloaded.

## Steps to reproduce

1. Serve this page over HTTP (`pages/crash.html`):

   ```html
   <!doctype html>
   <html lang="en">
   <head><meta charset="utf-8"><title>WebKitGTK AT-SPI GetAttributeRun crash</title>
   <style>ul { list-style: none } button { display: flex }</style></head>
   <body>
   <ul>
     <li><button> <span>One</span> </button></li>
     <li><button> <span>Two</span> </button></li>
   </ul>
   </body>
   </html>
   ```

2. Open it in MiniBrowser with the accessibility bus on:
   - `libexec/webkit2gtk-4.1/MiniBrowser http://127.0.0.1:8123/crash.html`; or
   - `libexec/webkitgtk-6.0/MiniBrowser http://127.0.0.1:8123/crash.html`.

3. Call `org.a11y.atspi.Text.GetAttributeRun(0, false)` on the first list item, as `probe.py` (PyGObject, Atspi 2.0)
   does for every offset of every text object:

   ```python
   item = ...  # the first accessible with role "list item"; its text is "￼"
   Atspi.Text.get_attribute_run(item, 0, False)
   Atspi.Text.get_attribute_run(item, 0, False)
   ```

   Pressing Tab onto the first button with Orca running does the same.

## Result

The first call answers. A later call on the same item aborts the WebProcess with SIGABRT. In MiniBrowser the page goes
blank and the UI process logs "WebProcess CRASHED". The client gets `The application no longer exists`.

**webkit2gtk-4.1**, gdb attached to the WebProcess:

```
#0  __pthread_kill_implementation () from libc.so.6
#1  raise () from libc.so.6
#2  abort () from libc.so.6
#3  WTF::CrashOnOverflow::crash() () from libwebkit2gtk-4.1.so.0
#4  WTF::CrashOnOverflow::overflowed() () from libwebkit2gtk-4.1.so.0
#5  WebCore::AccessibilityObjectAtspi::textAttributesWithUTF8Offset(std::optional<int>, bool) const () from libwebkit2gtk-4.1.so.0
#6  WebCore::AccessibilityObjectAtspi::$_14::__invoke(_GDBusConnection*, char const*, char const*, char const*, char const*, _GVariant*, _GDBusMethodInvocation*, void*) () from libwebkit2gtk-4.1.so.0
#7  call_in_idle_cb () from libgio-2.0.so.0
#8  g_idle_dispatch () from libglib-2.0.so.0
...
#13 WebKit::WebProcessMain(int, char**) () from libwebkit2gtk-4.1.so.0
```

**webkitgtk-6.0**, gdb attached to the WebProcess. To read its symbols, the WebProcess ran with
`WEBKIT_DISABLE_SANDBOX_THIS_IS_DANGEROUS=1`; it aborts the same way with the sandbox.

```
#3  WTF::CrashOnOverflow::crash() () from libwebkitgtk-6.0.so.4
#4  WTF::CrashOnOverflow::overflowed() () from libwebkitgtk-6.0.so.4
#5  WebCore::AccessibilityObjectAtspi::textAttributesWithUTF8Offset(std::optional<int>, bool) const () from libwebkitgtk-6.0.so.4
#6  WebCore::AccessibilityObjectAtspi::$_14::__invoke(...) () from libwebkitgtk-6.0.so.4
#7  call_in_idle_cb () from libgio-2.0.so.0
```

## Expected

A run within the list item's text, such as `(attributes, 0, 1)`, and no abort.

## Analysis

1. **The mapping has two entries.** The list item's text is one U+FFFC for its button. That is a 16-bit string, so
   `offsetMapping()` builds a table of two entries, `[0, 1]`.

2. **The run's end escapes the item.** `textAttributes()` finds the run by walking renderers forward from the
   offset's renderer. The walk is bounded by
   `lastValidPosition = lastPositionInOrAfterNode(m_coreObject->node()->lastDescendant())`.
   - Here the item's last descendant has no renderer: the collapsed whitespace after the flex item's span. A comment
     node does the same, such as the anchors that Svelte and other frameworks leave.
   - So the walk does not stop at the item's last rendered text. It continues with `nextInPreOrder()` into the next
     list item's text, whose attributes are the same.
   - `indexForVisiblePosition()` of that end position is past the item's one character.

3. **The lookup is unchecked.** `UTF16OffsetToUTF8()` returns `mapping[offset]` without a bounds check, so the
   `Vector` index aborts with `CrashOnOverflow`.

## Which markup aborts

`pages/` in this directory. Each page was probed at every offset of every text object, on both flavours:

| Page | Markup | Aborts |
|---|---|---|
| `crash.html` | `<li><button> <span>One</span> </button></li>`, twice | yes |
| `roles.html` | the same with `<div role="list">` and `<div role="listitem">` | yes |
| `nav.html` | `<li><button><svg/><span>Changes</span><span>2</span><!----></button></li>`: no whitespace, a trailing comment | yes |
| `nav-tight.html` | the same, the form Nix Manager's navigation had | yes |
| `no-whitespace.html` | `<li><button><span>One</span></button></li>`: the last descendant is rendered | no |
| `nav-workaround.html` | `nav.html` with `li { display: contents }` and `aria-setsize`/`aria-posinset` on each item | no |

Found by hand in the same session, and not part of `default.nix`:
- **Also abort:**
  - a `display: inline-flex`, `grid` or `block` button;
  - a flex row inside a block button.
- **Do not abort:**
  - a single list item: no following content with the same attributes;
  - a button whose only child is its text.

## Suggested fix

Either of these, ideally both:

- **Clamp the run before mapping it.** In `textAttributesWithUTF8Offset()`, clamp `startOffset` and `endOffset` to
  the object's text length before calling `UTF16OffsetToUTF8()`. Or bounds-check `UTF16OffsetToUTF8()` itself, as
  `UTF8OffsetToUTF16()` already is.
- **Keep the walk inside the object.** In `textAttributes()`, limit the backward and forward renderer walks to
  renderers inside `m_coreObject->node()`, for example with `isDescendantOf`, instead of comparing DOM positions with
  the first and last descendant. That descendant can be a node without a renderer.

## Workaround for pages

- **What works:** make the list items `display: contents`.
  - An item then has no renderer of its own, and `textAttributes()` returns before computing a run.
  - Orca then no longer counts the items ("list with N items"), probably because they have no box.
  - `aria-setsize` and `aria-posinset` on each item bring the count back: Orca says "List with 8 items" again on both
    flavours.
- **Also works:** removing every whitespace and comment node after the last rendered child of each button. This is
  fragile with frameworks that insert anchors.
