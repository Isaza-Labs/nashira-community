import { defineConfig } from "@playwright/test";

import base from "./playwright.config";

// The README captures (e2e/readme-shots.capture.ts). Kept out of the default suite:
// they assert nothing about the product and exist only to produce images.
// Run: npx playwright test -c playwright.readme.config.ts
export default defineConfig({ ...base, testMatch: "readme-shots.capture.ts" });
