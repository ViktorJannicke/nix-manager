import { resolve } from "node:path";
import tailwindcss from "@tailwindcss/vite";
import { svelte } from "@sveltejs/vite-plugin-svelte";
import { defineConfig } from "vite";

export default defineConfig({
  root: "ui",
  plugins: [
    tailwindcss(),
    svelte({ configFile: resolve(import.meta.dirname, "svelte.config.js") }),
  ],
  resolve: {
    alias: {
      $lib: resolve(import.meta.dirname, "ui/lib"),
    },
  },
  build: {
    outDir: "../ui-dist",
    emptyOutDir: true,
    assetsInlineLimit: 0,
    rollupOptions: {
      output: {
        entryFileNames: "assets/app.js",
        chunkFileNames: "assets/[name]-[hash].js",
        assetFileNames: (asset) =>
          asset.names?.some((name) => name.endsWith(".css"))
            ? "assets/app.css"
            : "assets/[name]-[hash][extname]",
      },
    },
  },
});
