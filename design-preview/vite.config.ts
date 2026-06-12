import { defineConfig } from "vite";
import react from "@vitejs/plugin-react";

// Standalone preview SPA. base:'/' so client-routed deep links load assets.
export default defineConfig({
  plugins: [react()],
  base: "/",
  server: { port: 5180, host: true },
  preview: { port: 5180, host: true },
});
