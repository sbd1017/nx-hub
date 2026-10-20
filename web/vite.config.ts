import { defineConfig } from "vite";
import react from "@vitejs/plugin-react";

// The production build is served by the NX Hub server from its wwwroot.
// During development, `npm run dev` proxies API and WebSocket calls to a local server on :8080.
export default defineConfig({
  plugins: [react()],
  // Relative asset URLs: the same build works at / and behind a proxy sub-path (e.g. /nxhub/).
  base: "./",
  build: {
    outDir: "../src/NxHub.Server/wwwroot",
    emptyOutDir: true,
    target: "es2022",
  },
  server: {
    port: 5173,
    proxy: {
      "/api": { target: "http://localhost:8080", ws: true },
      "/openapi": "http://localhost:8080",
    },
  },
});
