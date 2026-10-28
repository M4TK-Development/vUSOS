/// <reference types="vitest" />
import { defineConfig } from "vite";
import react from "@vitejs/plugin-react";

export default defineConfig({
  // Relative base so dist works from file:// and capacitor://. For web hosting with deep-link reloads
  // (e.g. /kursy/5) build with VITE_BASE=/ .
  base: process.env.VITE_BASE ?? "./",
  plugins: [react()],
  server: {
    port: 5173,
    proxy: {
      "/api": {
        target: "http://localhost:3001",
        changeOrigin: true,
        // cookies are passed through; keep them host-relative so they bind to :5173
        cookieDomainRewrite: "",
      },
    },
  },
  build: { sourcemap: true },
  test: {
    environment: "jsdom",
    globals: true,
    setupFiles: ["./src/test/setup.ts"],
    css: false,
  },
});
