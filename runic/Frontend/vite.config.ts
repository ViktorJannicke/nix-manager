import { fileURLToPath } from "node:url";
import { defineConfig } from "vite";
import { svelte } from "@sveltejs/vite-plugin-svelte";
import tailwindcss from "@tailwindcss/vite";
import { runic } from "@runic-artifex/vite-plugin-runic";
import { runicTranslations } from "@runic-artifex/vite-plugin-runic-translations";

// The translations compiler is the project-local .NET tool (`dotnet tool restore`). The Nix build has no
// network to restore it and passes the tool's assembly instead (runic/package.nix).
const translationsTool = process.env.NIX_MANAGER_TRANSLATIONS_TOOL;

export default defineConfig({
  // Relative asset URLs keep the published www folder relocatable, and Runic Desktop serves each window below
  // its own path. The page names no host, so one build runs on Runic Desktop and CS-WebUI.
  base: "./",
  resolve: { alias: { $lib: fileURLToPath(new URL("./src/lib", import.meta.url)) } },
  plugins: [
    runicTranslations(translationsTool ? { command: "dotnet", commandArguments: [translationsTool], persistentCompiler: false } : {}),
    tailwindcss(),
    svelte(),
    runic(),
  ],
  build: {
    rolldownOptions: { input: ["index.html"] },
    outDir: "dist",
    emptyOutDir: true,
    target: "es2022",
    // One local page, loaded from disk: the stored catalogue (both languages) is most of it.
    chunkSizeWarningLimit: 1500,
  },
});
