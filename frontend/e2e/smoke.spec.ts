import { expect, test } from "@playwright/test";

// Smoke-level e2e against the built static SPA. No backend required — these
// exercise the app shell, the client-side auth guard, and login rendering.

test("root redirects to login when unauthenticated", async ({ page }) => {
  await page.goto("/");
  await expect(page).toHaveURL(/\/login/);
  await expect(page.getByRole("heading", { name: "Nashira" })).toBeVisible();
});

test("protected routes redirect to login", async ({ page }) => {
  await page.goto("/workflows");
  await expect(page).toHaveURL(/\/login/);
  await page.goto("/admin");
  await expect(page).toHaveURL(/\/login/);
});

test("login form renders its fields", async ({ page }) => {
  await page.goto("/login");
  await expect(page.getByLabel("Username")).toBeVisible();
  await expect(page.getByLabel("Password")).toBeVisible();
  await expect(page.getByRole("button", { name: "Sign in" })).toBeVisible();
});
