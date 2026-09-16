import react from "@vitejs/plugin-react";
import tailwindcss from "@tailwindcss/vite";
import { defineConfig } from "vitest/config";

export default defineConfig({
  plugins: [react(), tailwindcss()],
  server: {
    port: 5176,
    proxy: {
      "/api": "http://localhost:5066",
      "/health": "http://localhost:5066",
    },
  },
  test: {
    environment: "jsdom",
    setupFiles: "./src/testSetup.ts",
  },
});
