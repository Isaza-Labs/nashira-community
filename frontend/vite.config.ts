import { execSync } from "node:child_process";
import { readFileSync } from "node:fs";
import { sveltekit } from "@sveltejs/kit/vite";
import tailwindcss from "@tailwindcss/vite";
import { defineConfig } from "vite";

// The dev server proxies the backend so the browser talks same-origin (no CORS in
// dev). The backend dev URL is http://localhost:5280 (nashira_backend
// launchSettings); override with VITE_API_TARGET for split setups.
const apiTarget = process.env.VITE_API_TARGET || "http://localhost:5280";

// What build the docs describe, stamped at build time so a reader can tell whether a
// page matches the code they are running. The Docker build context excludes .git, so
// the commit comes from NASHIRA_BUILD_COMMIT when set, then from git, then "unknown";
// NASHIRA_BUILD_DATE likewise overrides the build clock for reproducible images.
function gitCommit(): string {
  try {
    return execSync("git rev-parse --short HEAD", { stdio: ["ignore", "pipe", "ignore"] })
      .toString()
      .trim();
  } catch {
    return "unknown";
  }
}

const pkg = JSON.parse(readFileSync(new URL("./package.json", import.meta.url), "utf-8"));
const buildInfo = {
  version: String(pkg.version ?? "0.0.0"),
  commit: process.env.NASHIRA_BUILD_COMMIT || gitCommit(),
  date: process.env.NASHIRA_BUILD_DATE || new Date().toISOString().slice(0, 10),
};

export default defineConfig({
  plugins: [tailwindcss(), sveltekit()],
  define: {
    __NASHIRA_BUILD__: JSON.stringify(buildInfo),
  },
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
