import { expect, test, type Page } from "@playwright/test";

async function openPreparation(page: Page, operator: "a" | "b") {
  await page.goto("/");
  const credentials = [`preparation-sse-${operator}-e2e`, `preparation-sse-${operator}-e2e-secret`];
  await page.getByLabel("Identificador de acceso").fill(credentials[0]);
  await page.getByLabel("Secreto").fill(credentials[1]);

  // Passive network observation ensures the stream has opened before B acts.
  // No intercepted responses, injected events, or test-triggered business reads.
  const streamOpened = page.waitForResponse((response) =>
    new URL(response.url()).pathname === "/api/notifications/stream" && response.status() === 200,
  );
  // Observe rejection immediately if an earlier UI assertion fails and closes the context.
  void streamOpened.catch(() => {});
  await page.getByRole("button", { name: "Ingresar" }).click();
  await expect(page.getByRole("region", { name: "Identity actual" }))
    .toContainText(`Preparador SSE ${operator.toUpperCase()} E2E`);
  const preparation = page.getByRole("region", { name: "Preparación", exact: true });
  await expect(preparation.getByText("Cocina SSE E2E", { exact: true })).toBeVisible();
  const row = preparation.getByRole("row").filter({ hasText: "Papas SSE E2E" });
  await expect(row).toContainText("Pendiente1");
  await expect(row).toContainText("En preparación0");
  await expect(row).toContainText("Listo0");
  await streamOpened;
  await expect(preparation.getByRole("button", { name: "Actualizar preparación" }))
    .toBeEnabled();
  return { preparation, row };
}

test("Preparation actualiza al observador por SSE tras el Start de otra sesión", async ({ browser }) => {
  const contextA = await browser.newContext();
  const contextB = await browser.newContext();
  try {
    const pageA = await contextA.newPage();
    const pageB = await contextB.newPage();
    const observer = await openPreparation(pageA, "a");
    const actor = await openPreparation(pageB, "b");

    // A stays on its already-open destination. Only B performs one UI command.
    const quantity = actor.preparation.getByLabel(
      "Cantidad a iniciar de Papas SSE E2E, incorporación 1, Mesa SSE E2E, sin instrucción",
    );
    await expect(quantity).toHaveValue("1");
    await actor.preparation.getByRole("button", {
      name: "Iniciar Papas SSE E2E, incorporación 1, Mesa SSE E2E, sin instrucción",
      exact: true,
    }).click();

    await expect(actor.row).toContainText("Pendiente0");
    await expect(actor.row).toContainText("En preparación1");
    await expect(actor.row).toContainText("Listo0");

    // No reload, refresh, destination re-selection, or API call on A.
    await expect(observer.row).toContainText("Pendiente0", { timeout: 10_000 });
    await expect(observer.row).toContainText("En preparación1");
    await expect(observer.row).toContainText("Listo0");
    await expect(observer.row).toContainText("Total1");
  } finally {
    await contextA.close();
    await contextB.close();
  }
});
