import { dlopen, FFIType, ptr } from "bun:ffi";
import { ManagerError } from "./domain";
import { mkdtempSync, rmSync } from "node:fs";
import { tmpdir } from "node:os";
import { join } from "node:path";

// A deliberately small binding to webui.me. No runtime downloads, callbacks,
// filesystem serving, or WebUI JavaScript RPC are needed: the app uses its own
// authenticated loopback API and WebUI supplies the browser app window.
export function createWindow() {
  let library;
  try {
    library = dlopen(process.env.NIX_MANAGER_WEBUI_LIBRARY || "libwebui-2.so", {
      webui_new_window: { args: [], returns: FFIType.u64 },
      webui_set_config: {
        args: [FFIType.i32, FFIType.bool],
        returns: FFIType.void,
      },
      webui_set_size: {
        args: [FFIType.u64, FFIType.u32, FFIType.u32],
        returns: FFIType.void,
      },
      webui_set_root_folder: {
        args: [FFIType.u64, FFIType.ptr],
        returns: FFIType.bool,
      },
      webui_show_browser: {
        args: [FFIType.u64, FFIType.ptr, FFIType.u64],
        returns: FFIType.bool,
      },
      webui_exit: { args: [], returns: FFIType.void },
    });
  } catch (error) {
    throw new ManagerError({
      message: `Cannot load WebUI: ${error}. Use the Nix package or launch with --browser.`,
    });
  }
  const api = library.symbols;
  api.webui_set_config(0, false); // Let Bun serve the page while the browser opens.
  const window = api.webui_new_window();
  const root = mkdtempSync(join(tmpdir(), "nix-manager-window-"));
  const folder = Buffer.from(root + "\0");
  api.webui_set_root_folder(window, ptr(folder));
  api.webui_set_size(window, 1100, 680);
  return {
    show(url: string) {
      const encoded = Buffer.from(url + "\0");
      if (!api.webui_show_browser(window, ptr(encoded), 1))
        throw new ManagerError({
          message: "WebUI could not open an installed browser. Try --browser.",
        });
    },
    close() {
      api.webui_exit();
      rmSync(root, {
        recursive: true,
        force: true,
      }); /* WebUI owns native threads until process exit. */
    },
  };
}
