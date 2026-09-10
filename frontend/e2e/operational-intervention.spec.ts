import { expect, test, type Page } from "@playwright/test";

const productName = "Papas E2E intervención";
const orderContext = "Mesa intervención E2E";

async function login(page: Page, identifier: string, secret: string, identityName: string) {
  await page.goto("/");
  await page.getByLabel("Identificador de acceso").fill(identifier);
  await page.getByLabel("Secreto").fill(secret);
  await page.getByRole("button", { name: "Ingresar" }).click();
  await expect(
    page.getByRole("region", { name: "Identity actual" }).getByText(identityName, { exact: true }),
  ).toBeVisible();
}

async function logout(page: Page) {
  await page.getByRole("region", { name: "Identity actual" })
    .getByRole("button", { name: "Cambiar persona / salir" }).click();
  await expect(page.getByRole("heading", { name: "Ingresar" })).toBeVisible();
}

async function readOperationalReference(page: Page): Promise<string> {
  const preparation = page.getByRole("region", { name: "Preparación" });
  const row = preparation.getByRole("row").filter({ hasText: productName });
  return (await row.locator(".technical-reference").innerText()).trim();
}

test("OperationalIntervention cancela Ready real y liquida la obligación reducida", async ({ page }) => {
  await login(page, "preparador-e2e", "preparation-e2e-secret", "Preparador E2E");
  const preparation = page.getByRole("region", { name: "Preparación" });
  const preparationRow = preparation.getByRole("row").filter({ hasText: productName });
  const start = preparation.getByLabel(
    "Cantidad a iniciar de " + productName + ", incorporación 1, " + orderContext + ", Sin sal",
  );
  await expect(start).toHaveValue("2");
  await start.fill("2");
  await preparation.getByRole("button", {
    name: "Iniciar " + productName + ", incorporación 1, " + orderContext + ", Sin sal",
  }).click();
  await expect(preparationRow).toContainText("Pendiente0");
  await expect(preparationRow).toContainText("En preparación2");

  const operationalReference = await readOperationalReference(page);
  await logout(page);

  await login(page, "preparadora-b-e2e", "preparation-b-e2e-secret", "Preparadora E2E B");
  const secondPreparation = page.getByRole("region", { name: "Preparación" });
  const secondRow = secondPreparation.getByRole("row").filter({ hasText: productName });
  await expect(secondRow).toContainText("Pendiente0");
  await expect(secondRow).toContainText("En preparación2");
  await secondPreparation.getByRole("button", {
    name: "Marcar listo " + productName + ", incorporación 1, " + orderContext + ", Sin sal",
  }).click();
  await expect(secondRow).toContainText("Pendiente0");
  await expect(secondRow).toContainText("En preparación0");
  await expect(secondRow).toContainText("Listo2");
  await logout(page);

  await login(page, "delivery-e2e", "delivery-e2e-secret", "Delivery E2E");
  await page.getByLabel("Referencia operacional").fill(operationalReference);
  await page.getByRole("button", { name: "Buscar Pedido" }).click();
  const order = page.getByRole("region", { name: "Pedido consultado" });
  const incorporation = order.getByRole("article", { name: "Incorporación 1" });
  const incorporationId = (
    await incorporation.getByRole("definition").filter({ hasText: /^[0-9a-f-]{36}$/i }).innerText()
  ).trim();
  await expect(incorporationId).toMatch(/^[0-9a-f-]{36}$/i);
  await page.getByRole("button", { name: "Abrir entrega de este Pedido" }).click();
  const delivery = page.getByRole("region", {
    name: "Entrega del Pedido " + operationalReference,
  });
  const prepared = delivery.getByRole("article", {
    name: productName + ", Sin sal, incorporación 1",
  });
  await expect(prepared).toContainText("Ready2");
  await expect(prepared).toContainText("Delivered0");
  const deliveryQuantity = prepared.getByLabel(
    "Cantidad a entregar — " + productName + " — Sin sal — incorporación 1",
  );
  await deliveryQuantity.fill("1");
  await prepared.getByRole("button", {
    name: "Entregar " + productName + ", Sin sal, incorporación 1",
  }).click();
  await expect(prepared).toContainText("Ready2");
  await expect(prepared).toContainText("Delivered1");
  await expect(prepared).toContainText("Deliverable1");
  await logout(page);

  await login(page, "intervention-e2e", "intervention-e2e-secret", "Intervención E2E");
  const intervention = page.getByRole("region", { name: "Intervención operacional" });
  await intervention.getByLabel("Identificador de Incorporación").fill(incorporationId);
  await intervention.getByLabel("Ordinal de Content").fill("1");
  await intervention.getByRole("button", { name: "Consultar para intervenir" }).click();
  await expect(intervention.getByText(productName, { exact: true })).toBeVisible();
  await expect(intervention).toContainText("En preparación (I)0");
  await expect(intervention).toContainText("Lista (Y)2");
  await expect(intervention).toContainText("Entregada (D)1");
  await expect(intervention).toContainText("Máximo: 1");
  await expect(intervention.getByRole("button", { name: "Cancelar cantidad ya lista" })).toBeVisible();
  await expect(intervention.getByRole("button", { name: /Corregir|Undo|Deshacer/i })).toHaveCount(0);
  await expect(intervention).not.toContainText(/desperdicio|descarte|recuperación|inventario/i);
  const readyAmount = intervention.getByLabel("Cantidad ya lista a cancelar");
  await expect(readyAmount).toHaveAttribute("max", "1");
  await readyAmount.fill("1");
  await expect(
    intervention.getByRole("table", { name: "Vista previa: Cancelar cantidad ya lista" }),
  ).toContainText("Total vigente (T)");
  await intervention.getByRole("button", { name: "Cancelar cantidad ya lista" }).click();
  await expect(intervention).toContainText("Intervención registrada. Consultando la obligación vigente.");
  await expect(intervention).toContainText("Cancelada (C)1");
  await expect(intervention).toContainText("Obligación de cumplimiento (F)1");
  await expect(intervention).toContainText("Total vigente (T)1");
  await expect(intervention).toContainText("Lista (Y)1");
  await expect(intervention).toContainText("Entregada (D)1");
  await expect(intervention).toContainText("No hay cantidad elegible para intervenir.");
  await logout(page);

  await login(page, "delivery-e2e", "delivery-e2e-secret", "Delivery E2E");
  await page.getByLabel("Referencia operacional").fill(operationalReference);
  await page.getByRole("button", { name: "Buscar Pedido" }).click();
  await page.getByRole("button", { name: "Abrir entrega de este Pedido" }).click();
  const finalDelivery = page.getByRole("region", {
    name: "Entrega del Pedido " + operationalReference,
  });
  const finalPrepared = finalDelivery.getByRole("article", {
    name: productName + ", Sin sal, incorporación 1",
  });
  await expect(finalPrepared).toContainText("Ready1");
  await expect(finalPrepared).toContainText("Delivered1");
  await expect(finalPrepared).toContainText("Deliverable0");
  const ending = page.getByRole("region", { name: "Liquidación y Cierre" });
  await expect(
    ending.getByText("Importe funcional actual").locator("..").locator("dd"),
  ).toHaveText(/^7(?:\.0+)?$/);
  await expect(ending.getByRole("button", { name: "Liquidar", exact: true })).toBeEnabled();
  await ending.getByLabel("Medio de pago declarado").fill("Efectivo");
  await ending.getByRole("button", { name: "Liquidar", exact: true }).click();
  await expect(ending.getByText(/Pedido congelado/)).toBeVisible();
  await expect(
    ending.getByText("Importe liquidado").locator("..").locator("dd"),
  ).toHaveText(/^7(?:\.0+)?$/);
});
