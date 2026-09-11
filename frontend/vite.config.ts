import { sveltekit } from "@sveltejs/kit/vite";
import tailwindcss from "@tailwindcss/vite";
import { defineConfig } from "vite";

// The dev server proxies the backend so the browser talks same-origin (no CORS in
// dev). The backend dev URL is http://localhost:5280 (nashira_backend
// launchSettings); override with VITE_API_TARGET for split setups.
const apiTarget = process.env.VITE_API_TARGET || "http://localhost:5280";

export default defineConfig({
  plugins: [tailwindcss(), sveltekit()],
  server: {
    port: 5173,
    host: process.env.VITE_HOST || "localhost",
    proxy: {
      // `ws`/long timeout keep the SSE chat stream (/api/ai/chat) alive.
      "/api": { target: apiTarget, changeOrigin: true, ws: true, timeout: 300000 },
      "/health": { target: apiTarget, changeOrigin: true },
      "/openapi": { target: apiTarget, changeOrigin: true },
      "/scalar": { target: apiTarget, changeOrigin: true },
    },
  },
});
