import { expect, test, type Locator, type Page } from "@playwright/test";

const productName = "Papas E2E autorizadas";
const orderContext = "Mesa cancelación completa E2E";

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

async function lookup(page: Page, reference: string) {
  await page.getByLabel("Referencia operacional").fill(reference);
  await page.getByRole("button", { name: "Buscar Pedido" }).click();
  await expect(page.getByRole("button", { name: "Buscar Pedido" })).toBeEnabled();
}

async function quantity(surface: Locator, label: string, value: string) {
  await expect(surface.getByText(label, { exact: true }).locator("..").locator("dd")).toHaveText(value);
}

test("Complete Cancellation termina obligación Pending e InPreparation y descarta PendingComposition", async ({ page }) => {
  await login(page, "complete-cancellation-e2e", "complete-cancellation-e2e-secret", "Cancelación completa E2E");

  // One confirmed Content is enough to demonstrate two cancellation origins.
  const composition = page.getByRole("region", { name: "Composición inicial" });
  const add = composition.getByRole("button", { name: `Agregar ${productName} a Composición inicial` });
  await add.click();
  await add.click();
  await composition.getByLabel("Contexto").fill(orderContext);
  const confirmed = page.waitForResponse(response => response.url().endsWith("/api/order-operations/first-confirmations") && response.request().method() === "POST");
  await composition.getByRole("button", { name: "Confirmar Primera Composición" }).click();
  const confirmationResponse = await confirmed;
  expect(confirmationResponse.ok()).toBeTruthy();
  const { operationalReference, firstIncorporation } = await confirmationResponse.json();
  const incorporationId: string = firstIncorporation.id;
  await expect(page.getByRole("region", { name: "Pedido activo" }).getByRole("article", { name: "Incorporación 1" })).toBeVisible();
  await logout(page);

  // This actor alone has Preparation and the Cocina enablement.
  await login(page, "preparador-e2e", "preparation-e2e-secret", "Preparador E2E");
  const preparation = page.getByRole("region", { name: "Preparación" });
  const work = preparation.getByRole("row").filter({ hasText: productName }).filter({ hasText: orderContext });
  await expect(work).toContainText("Pendiente2");
  await expect(work).toContainText("En preparación0");
  await work.getByRole("spinbutton", { name: /^Cantidad a iniciar/ }).fill("1");
  await work.getByRole("button", { name: /^Iniciar / }).click();
  await expect(work).toContainText("Pendiente1");
  await expect(work).toContainText("En preparación1");
  await expect(work).toContainText("Listo0");
  await logout(page);

  // The cancellation actor has OrderOperationsAndBasicClosure + OperationalIntervention,
  // deliberately neither Preparation nor PreparationEnablement (DatabaseSetup fixture).
  await login(page, "complete-cancellation-e2e", "complete-cancellation-e2e-secret", "Cancelación completa E2E");
  await lookup(page, operationalReference);
  await page.getByRole("button", { name: "Continuar este Pedido" }).click();
  const pendingComposition = page.getByRole("region", { name: "Nueva Composición" });
  await pendingComposition.getByRole("button", { name: `Agregar ${productName} a Nueva Composición` }).click();
  await expect(pendingComposition.getByText("Composición pendiente autoritativa activa", { exact: true })).toBeVisible();

  // Supported narrow read exposes Q/R/C/F and current Work without Preparation authority.
  const intervention = page.getByRole("region", { name: "Intervención operacional" });
  await intervention.getByLabel("Identificador de Incorporación").fill(incorporationId);
  await intervention.getByLabel("Ordinal de Content").fill("1");
  await intervention.getByRole("button", { name: "Consultar para intervenir" }).click();
  const obligation = intervention.getByRole("definition");
  await expect(obligation).not.toHaveCount(0);
  await quantity(intervention, "Confirmada (Q)", "2");
  await quantity(intervention, "Retirada por corrección de contenido (R)", "0");
  await quantity(intervention, "Cancelada (C)", "0");
  await quantity(intervention, "Obligación de cumplimiento (F)", "2");
  await quantity(intervention, "Pendiente (P)", "1");
  await quantity(intervention, "En preparación (I)", "1");
  await quantity(intervention, "Lista (Y)", "0");
  await quantity(intervention, "Total vigente (T)", "2");
  await quantity(intervention, "Entregada (D)", "0");

  await lookup(page, operationalReference);
  const ending = page.getByRole("region", { name: "Liquidación y Cierre" });
  for (const label of ["Liquidado", "Congelado", "Cerrado"]) await quantity(ending, label, "No");
  const cancellation = page.getByRole("region", { name: "Cancelación completa excepcional" });
  await expect(cancellation).toContainText("OperationalIntervention");
  await expect(cancellation.getByRole("button", { name: "Cancelar pedido completo", exact: true })).toBeEnabled();
  await cancellation.getByRole("button", { name: "Cancelar pedido completo", exact: true }).click();
  const consequences = cancellation.getByRole("region", { name: "Confirmar cancelación completa" });
  await expect(consequences).toContainText("Obligación restante informada: 2.");
  await expect(consequences).toContainText("Hay trabajo real ya iniciado o listo");
  await expect(consequences).toContainText("el trabajo realizado permanecerá registrado en la Historia");
  await expect(consequences).toContainText("La Composición pendiente será descartada");
  await expect(consequences).toContainText("El pedido terminará excepcionalmente");
  await expect(consequences).toContainText("No se creará Liquidación ni Cierre");
  await expect(consequences.getByRole("table", { name: "Consecuencias informadas" }).locator("tbody tr")).toHaveCount(1);
  await expect(consequences.locator("tbody tr td")).toHaveText(["1", "1", "0", "0"]);

  // Only the terminal command is submitted: no Discard or Preparation Correction batch.
  const commands: string[] = [];
  const recordCommand = (request: import("@playwright/test").Request) => {
    if (request.method() === "POST") commands.push(new URL(request.url()).pathname);
  };
  page.on("request", recordCommand);
  const cancelled = page.waitForResponse(response => response.url().endsWith(`/api/orders/${operationalReference}/complete-cancellation`) && response.request().method() === "POST");
  const pendingRefreshed = page.waitForResponse(response => response.url().endsWith(`/api/orders/${operationalReference}/pending-composition`) && response.request().method() === "GET" && response.ok());
  await consequences.getByRole("button", { name: "Confirmar cancelación completa", exact: true }).click();
  const cancelledResponse = await cancelled;
  expect(cancelledResponse.ok()).toBeTruthy();
  expect(await cancelledResponse.json()).toMatchObject({
    orderId: operationalReference,
    isCompletelyCancelled: true,
    pendingCompositionDiscarded: true,
    consequences: [{ incorporationId, contentOrdinal: 1, directOrPendingQuantity: 1, inPreparationQuantity: 1, readyQuantity: 0, resultingFulfillmentQuantity: 0 }],
  });
  await expect(cancellation.getByText("Pedido completamente cancelado", { exact: true })).toBeVisible();
  await expect(cancellation).toContainText("La Composición pendiente fue descartada");
  expect(await (await pendingRefreshed).json()).toMatchObject({ orderId: operationalReference, pendingComposition: null });
  await expect(pendingComposition.getByText("Composición pendiente autoritativa activa", { exact: true })).toHaveCount(0);
  await expect(pendingComposition.getByRole("button", { name: `Agregar ${productName} a Nueva Composición` })).toBeDisabled();
  await expect(page.getByRole("button", { name: "Continuar este Pedido" })).toHaveCount(0);
  await expect(cancellation.getByRole("button", { name: "Cancelar pedido completo", exact: true })).toHaveCount(0);
  await expect(ending.getByRole("button", { name: /Liquidar|Cerrar Pedido/ })).toHaveCount(0);
  for (const label of ["Liquidado", "Congelado", "Cerrado"]) await quantity(ending, label, "No");
  expect(commands).toEqual([`/api/orders/${operationalReference}/complete-cancellation`]);
  page.off("request", recordCommand);

  await intervention.getByRole("button", { name: "Actualizar contenido", exact: true }).click();
  await quantity(intervention, "Confirmada (Q)", "2");
  await quantity(intervention, "Retirada por corrección de contenido (R)", "0");
  await quantity(intervention, "Cancelada (C)", "2");
  for (const label of ["Obligación de cumplimiento (F)", "Pendiente (P)", "En preparación (I)", "Lista (Y)", "Total vigente (T)", "Entregada (D)"]) await quantity(intervention, label, "0");
  await expect(intervention.getByRole("button", { name: /Cancelar cantidad ya/ })).toHaveCount(0);

  // A fresh browser read retains the terminal fact and confirmed historical Content.
  await page.reload();
  await expect(page.getByRole("region", { name: "Identity actual" })).toBeVisible();
  await lookup(page, operationalReference);
  await expect(cancellation.getByText("Pedido completamente cancelado", { exact: true })).toBeVisible();
  const historical = page.getByRole("region", { name: "Pedido consultado" }).getByRole("article", { name: "Incorporación 1" });
  await expect(historical.getByRole("row").filter({ hasText: productName }).getByRole("cell")).toHaveText([productName, "2", /^7(?:\.0+)?$/, "Sin instrucción"]);
  for (const label of ["Liquidado", "Congelado", "Cerrado"]) await quantity(ending, label, "No");
  await expect(page.getByRole("button", { name: /^(Continuar este Pedido|Liquidar|Cerrar Pedido|Cancelar pedido completo)$/ })).toHaveCount(0);
});
