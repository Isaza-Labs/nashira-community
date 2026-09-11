import { defineConfig } from "@playwright/test";

// Smoke-level e2e. Builds the app and serves it with the Node adapter (the same
// `node build` the frontend container runs); specs assert the app shell mounts.
// Backend-dependent flows are exercised in later slices.
export default defineConfig({
  testDir: "e2e",
  webServer: {
    command: "npm run build && node build",
    port: 4173,
    env: { PORT: "4173", HOST: "127.0.0.1" },
    reuseExistingServer: !process.env.CI,
    timeout: 120000,
  },
  use: { baseURL: "http://localhost:4173" },
});
