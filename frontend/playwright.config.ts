import { env } from "node:process";
import { defineConfig, devices } from "@playwright/test";

const connectionString = env.NEXOBAR_E2E_CONNECTION_STRING;

if (!connectionString) {
  throw new Error(
    "NEXOBAR_E2E_CONNECTION_STRING is required. Run Playwright through npm run test:e2e.",
  );
}

export default defineConfig({
  testDir: "./e2e",
  fullyParallel: false,
  workers: 1,
  retries: 0,
  reporter: "list",
  use: {
    baseURL: "http://127.0.0.1:5173",
    trace: "retain-on-failure",
  },
  projects: [
    {
      name: "chromium",
      use: { ...devices["Desktop Chrome"] },
    },
  ],
  webServer: [
    {
      command:
        "dotnet run --project ../backend/src/NexoBar.Host/NexoBar.Host.csproj --no-launch-profile",
      url: "http://127.0.0.1:5028/openapi/v1.json",
      reuseExistingServer: false,
      timeout: 120_000,
      env: {
        ASPNETCORE_ENVIRONMENT: "Testing",
        ASPNETCORE_URLS: "http://127.0.0.1:5028",
        ConnectionStrings__Catalog: connectionString,
        ConnectionStrings__OrderOperations: connectionString,
      },
    },
    {
      command: "npm run dev -- --host 127.0.0.1 --port 5173 --strictPort",
      url: "http://127.0.0.1:5173",
      reuseExistingServer: false,
      timeout: 120_000,
    },
  ],
});
