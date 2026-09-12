import { randomUUID } from "node:crypto";
import { expect, test, type Locator, type Page } from "@playwright/test";

async function login(page: Page, identifier: string, secret: string, name: string) {
  await page.goto("/");
  await page.getByLabel("Identificador de acceso").fill(identifier);
  await page.getByLabel("Secreto").fill(secret);
  await page.getByRole("button", { name: "Ingresar" }).click();
  await expect(page.getByRole("region", { name: "Identity actual" }).getByText(name, { exact: true })).toBeVisible();
}

async function logout(page: Page) {
  await page.getByRole("region", { name: "Identity actual" }).getByRole("button", { name: "Cambiar persona / salir" }).click();
  await expect(page.getByRole("heading", { name: "Ingresar" })).toBeVisible();
}

function value(surface: Locator, label: string) {
  return surface.getByText(label, { exact: true }).locator("xpath=following-sibling::dd[1]");
}

test("Applied Price Correction adopta Catálogo explícitamente y Liquida al precio efectivo", async ({ page }) => {
  const productName = `Precio aplicado E2E ${randomUUID().slice(0, 8)}`;
  // DatabaseSetup grants this actor only CatalogConfiguration.
  await login(page, "price-catalog-e2e", "price-catalog-e2e-secret", "Catálogo precios E2E");
  await page.getByLabel("Nombre operacional").fill(productName);
  await page.getByLabel("Precio", { exact: true }).fill("10");
  await page.getByRole("button", { name: "Crear producto" }).click();
  const product = page.getByRole("region", { name: "Productos vigentes" }).getByRole("row").filter({
    has: page.getByRole("cell", { name: productName, exact: true }),
  });
  await expect(product.getByRole("cell", { name: "10", exact: true })).toBeVisible();
  await logout(page);

  // This existing actor has only OrderOperationsAndBasicClosure, no CatalogConfiguration.
  await login(page, "delivery-e2e", "delivery-e2e-secret", "Delivery E2E");
  const composition = page.getByRole("region", { name: "Composición inicial" });
  await composition.getByRole("button", { name: `Agregar ${productName} a Composición inicial` }).click();
  await composition.getByLabel("Contexto").fill("Mesa precio aplicado E2E");
  const confirmed = page.waitForResponse(response => response.url().endsWith("/api/order-operations/first-confirmations") && response.request().method() === "POST");
  await composition.getByRole("button", { name: "Confirmar Primera Composición" }).click();
  const confirmationResponse = await confirmed;
  expect(confirmationResponse.ok()).toBeTruthy();
  const { operationalReference, firstIncorporation } = await confirmationResponse.json();
  const incorporationId: string = firstIncorporation.id;
  const contentOrdinal = 1;
  const target = { orderId: operationalReference, incorporationId, contentOrdinal };
  const contentPath = `/api/order-operations/orders/${operationalReference}/incorporations/${incorporationId}/contents/${contentOrdinal}`;

  const historical = page.getByRole("article", { name: "Incorporación 1", exact: true });
  async function originalContent() {
    await expect(historical.locator("tbody tr")).toHaveCount(1);
    await expect(historical.locator("tbody tr").getByRole("cell")).toHaveText([productName, "1", /^10(?:\.0+)?$/, "Sin instrucción"]);
  }
  await originalContent();
  await expect(page.getByText("Composición pendiente autoritativa activa", { exact: true })).toHaveCount(0);
  await page.getByRole("button", { name: "Abrir entrega de este Pedido" }).click();
  const delivery = page.getByRole("region", { name: `Entrega del Pedido ${operationalReference}` });
  const deliveredContent = delivery.getByRole("article", { name: `${productName}, sin instrucción, incorporación 1`, exact: true });
  await expect(delivery.getByRole("article")).toHaveCount(1);
  await expect(deliveredContent).toContainText("Preparación no requerida");
  await expect(deliveredContent.getByLabel(/^Cantidad a entregar —/)).toHaveValue("1");
  await deliveredContent.getByRole("button", { name: /^Entregar / }).click();
  await expect(deliveredContent).toContainText("Delivered1");
  const ending = page.getByRole("region", { name: "Liquidación y Cierre" });
  const amount = value(ending, "Importe funcional actual");
  await expect(amount).toHaveText(/^10(?:\.0+)?$/);
  for (const label of ["Liquidado", "Congelado", "Cerrado"]) await expect(value(ending, label)).toHaveText("No");
  await logout(page);

  await login(page, "price-catalog-e2e", "price-catalog-e2e-secret", "Catálogo precios E2E");
  await product.getByRole("button", { name: `Cambiar precio de ${productName}` }).click();
  const priceChange = page.getByRole("form", { name: `Cambiar precio de ${productName}` });
  await priceChange.getByLabel("Nuevo precio").fill("8");
  await priceChange.getByRole("button", { name: "Confirmar cambio de Precio" }).click();
  await expect(product.getByRole("cell", { name: "8", exact: true })).toBeVisible();
  await logout(page);

  await login(page, "delivery-e2e", "delivery-e2e-secret", "Delivery E2E");
  await page.getByLabel("Referencia operacional").fill(operationalReference);
  await page.getByRole("button", { name: "Buscar Pedido" }).click();
  await originalContent();
  await expect(amount).toHaveText(/^10(?:\.0+)?$/);

  const prices = page.getByRole("region", { name: "Corrección de precio aplicado" });
  // The UI names the incorporation/content ordinal; the real request proves the exact ID pair.
  const exactContent = prices.getByRole("article", { name: "Incorporación 1, contenido 1", exact: true });
  const evaluated = page.waitForResponse(response => response.url().endsWith(`${contentPath}/applied-price-correction`) && response.request().method() === "GET");
  await prices.getByRole("button", { name: "Consultar precios aplicados" }).click();
  const evaluation = await evaluated;
  expect(evaluation.ok()).toBeTruthy();
  expect(await evaluation.json()).toMatchObject({ ...target, appliedPrice: expect.stringMatching(/^10(?:\.0+)?$/), effectiveAppliedPrice: expect.stringMatching(/^10(?:\.0+)?$/), currentCatalogPrice: expect.stringMatching(/^8(?:\.0+)?$/) });
  await expect(prices.getByRole("article")).toHaveCount(1);
  await expect(value(exactContent, "Precio confirmado original")).toHaveText(/^10(?:\.0+)?$/);
  await expect(value(exactContent, "Precio aplicado efectivo actual")).toHaveText(/^10(?:\.0+)?$/);
  await expect(value(exactContent, "Precio vigente en catálogo")).toHaveText(/^8(?:\.0+)?$/);
  await expect(amount).toHaveText(/^10(?:\.0+)?$/);
  await exactContent.getByRole("button", { name: "Corregir precio aplicado", exact: true }).click();
  const correction = exactContent.getByRole("region", { name: "Confirmar corrección de precio" });
  await expect(correction).toBeVisible();
  await expect(prices.locator("input, textarea, [contenteditable=true]")).toHaveCount(0);

  const corrected = page.waitForResponse(response => response.url().endsWith(`${contentPath}/apply-current-catalog-price`) && response.request().method() === "POST");
  const refreshed = page.waitForResponse(response => response.url().endsWith(`${contentPath}/applied-price-correction`) && response.request().method() === "GET");
  await correction.getByRole("button", { name: /^Aplicar precio 8(?:\.0+)?$/ }).click();
  const correctionResponse = await corrected;
  expect(correctionResponse.ok()).toBeTruthy();
  expect(correctionResponse.request().postDataJSON()).toEqual({});
  expect(await correctionResponse.json()).toMatchObject({ ...target, previousEffectiveAppliedPrice: expect.stringMatching(/^10(?:\.0+)?$/), resultingEffectiveAppliedPrice: expect.stringMatching(/^8(?:\.0+)?$/) });
  const refreshResponse = await refreshed;
  expect(refreshResponse.ok()).toBeTruthy();
  expect(await refreshResponse.json()).toMatchObject({ ...target, appliedPrice: expect.stringMatching(/^10(?:\.0+)?$/), effectiveAppliedPrice: expect.stringMatching(/^8(?:\.0+)?$/), currentCatalogPrice: expect.stringMatching(/^8(?:\.0+)?$/) });
  await expect(prices.getByRole("button", { name: "Consultar precios aplicados" })).toBeEnabled();
  await originalContent();
  await expect(value(exactContent, "Precio confirmado original")).toHaveText(/^10(?:\.0+)?$/);
  await expect(value(exactContent, "Precio aplicado efectivo actual")).toHaveText(/^8(?:\.0+)?$/);
  await expect(value(exactContent, "Precio vigente en catálogo")).toHaveText(/^8(?:\.0+)?$/);
  await expect(exactContent.getByText("Entregado: 1", { exact: true })).toBeVisible();
  await expect(amount).toHaveText(/^8(?:\.0+)?$/);

  await ending.getByLabel("Medio de pago declarado").fill("Efectivo");
  await ending.getByRole("button", { name: "Liquidar", exact: true }).click();
  await expect(value(ending, "Liquidado")).toHaveText("Sí");
  await expect(value(ending, "Importe liquidado")).toHaveText(/^8(?:\.0+)?$/);
  await expect(value(ending, "Congelado")).toHaveText("Sí");
  await expect(ending.getByText(/Pedido congelado/)).toBeVisible();
});
