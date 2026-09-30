import { expect, test, type Locator, type Page } from "@playwright/test";
import { selectInitialContext } from "./helpers/select-initial-context.js";
import { selectPreparationDestination } from "./helpers/select-preparation-destination.js";

const productName = "Papas E2E autorizadas";
const orderContext = "Contexto base E2E";

async function login(
  page: Page,
  identifier: string,
  secret: string,
  name: string,
) {
  await page.goto("/");
  await page.getByLabel("Usuario de acceso").fill(identifier);
  await page.getByLabel("Contraseña").fill(secret);
  await page.getByRole("button", { name: "Ingresar" }).click();
  await expect(
    page
      .getByRole("region", { name: "Usuario actual" })
      .getByText(name, { exact: true }),
  ).toBeVisible();
}

async function logout(page: Page) {
  await page
    .getByRole("region", { name: "Usuario actual" })
    .getByRole("button", { name: "Cerrar sesión" })
    .click();
  await expect(page.getByRole("heading", { name: "Ingresar" })).toBeVisible();
}

async function lookup(page: Page, reference: string) {
  await page
    .getByRole("textbox", { name: "Referencia del pedido", exact: true })
    .fill(reference);
  await page.getByRole("button", { name: "Buscar pedido" }).click();
  await expect(
    page.getByRole("button", { name: "Buscar pedido" }),
  ).toBeEnabled();
}

async function quantity(surface: Locator, label: string, value: string) {
  await expect(
    surface.getByText(label, { exact: true }).locator("..").locator("dd"),
  ).toHaveText(value);
}

test("Complete Cancellation termina obligación Pending e InPreparation y descarta PendingComposition", async ({
  page,
}) => {
  await login(
    page,
    "complete-cancellation-e2e",
    "complete-cancellation-e2e-secret",
    "Cancelación completa E2E",
  );

  // One confirmed Content is enough to demonstrate two cancellation origins.
  const composition = page.getByRole("region", { name: "Preparar pedido" });
  const add = composition.getByRole("button", {
    name: `Agregar ${productName} a Preparar pedido`,
  });
  await add.click();
  await add.click();
  await selectInitialContext(composition, orderContext);
  const confirmed = page.waitForResponse(
    (response) =>
      response.url().endsWith("/api/order-operations/first-confirmations") &&
      response.request().method() === "POST",
  );
  await composition.getByRole("button", { name: "Crear Pedido" }).click();
  const confirmationResponse = await confirmed;
  expect(confirmationResponse.ok()).toBeTruthy();
  const { operationalReference, firstIncorporation } =
    await confirmationResponse.json();
  const incorporationId: string = firstIncorporation.id;
  await expect(
    page
      .getByRole("region", { name: "Pedido activo" })
      .getByRole("article", { name: "Incorporación 1" }),
  ).toBeVisible();
  await logout(page);

  // This actor alone has Preparation and the Cocina enablement.
  await login(
    page,
    "preparador-e2e",
    "preparation-e2e-secret",
    "Preparador E2E",
  );
  const preparation = page.getByRole("region", { name: "Preparación" });
  await selectPreparationDestination(preparation, "Cocina E2E");
  const work = preparation
    .getByRole("row")
    .filter({ hasText: productName })
    .filter({ hasText: operationalReference });
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
  await login(
    page,
    "complete-cancellation-e2e",
    "complete-cancellation-e2e-secret",
    "Cancelación completa E2E",
  );
  await lookup(page, operationalReference);
  const pendingLoaded = page.waitForResponse(
    (response) =>
      response
        .url()
        .endsWith(`/api/orders/${operationalReference}/pending-composition`) &&
      response.request().method() === "GET" &&
      response.ok(),
  );
  await page
    .getByRole("button", { name: "Agregar productos a este Pedido" })
    .click();
  expect(await (await pendingLoaded).json()).toMatchObject({
    orderId: operationalReference,
    pendingComposition: null,
  });
  const pendingComposition = page.getByRole("region", {
    name: "Agregar productos al pedido",
  });
  await expect(
    pendingComposition.getByText(operationalReference, { exact: true }),
  ).toBeVisible();
  await pendingComposition
    .getByRole("button", {
      name: `Agregar ${productName} a Agregar productos al pedido`,
    })
    .click();
  await expect(
    pendingComposition.getByText(
      "Productos pendientes de confirmar en este pedido",
      {
        exact: true,
      },
    ),
  ).toBeVisible();

  // Supported narrow read exposes Q/R/C/F and current Work without Preparation authority.
  await page
    .getByRole("button", { name: "Intervención en preparación" })
    .click();
  const intervention = page.getByRole("region", {
    name: "Intervención operacional",
  });
  await intervention
    .getByLabel("Identificador de Incorporación")
    .fill(incorporationId);
  await intervention.getByLabel("Número de contenido").fill("1");
  await intervention
    .getByRole("button", { name: "Consultar para intervenir" })
    .click();
  const obligation = intervention.getByRole("definition");
  await expect(obligation).not.toHaveCount(0);
  await quantity(intervention, "Cantidad confirmada", "2");
  await quantity(intervention, "Quitada por corrección", "0");
  await quantity(intervention, "Cantidad cancelada", "0");
  await quantity(intervention, "Cantidad requerida", "2");
  await quantity(intervention, "Pendiente", "1");
  await quantity(intervention, "En preparación", "1");
  await quantity(intervention, "Lista", "0");
  await quantity(intervention, "Total requerido", "2");
  await quantity(intervention, "Entregada", "0");

  await page.getByRole("button", { name: "Pedidos", exact: true }).click();
  await lookup(page, operationalReference);
  const ending = page.getByRole("region", { name: "Liquidación y Cierre" });
  for (const label of [
    "Liquidado",
    "Operación congelada por liquidación",
    "Cerrado",
  ])
    await quantity(ending, label, "No");
  const cancellation = page.getByRole("region", {
    name: "Cancelación completa excepcional",
  });
  await expect(cancellation).toContainText("Intervención operacional");
  await expect(
    cancellation.getByRole("button", {
      name: "Cancelar pedido completo",
      exact: true,
    }),
  ).toBeEnabled();
  await cancellation
    .getByRole("button", { name: "Cancelar pedido completo", exact: true })
    .click();
  const consequences = cancellation.getByRole("region", {
    name: "Confirmar cancelación completa",
  });
  await expect(consequences).toContainText("Obligación restante informada: 2.");
  await expect(consequences).toContainText(
    "Hay trabajo real ya iniciado o listo",
  );
  await expect(consequences).toContainText(
    "el trabajo realizado permanecerá registrado en la Historia",
  );
  await expect(consequences).toContainText(
    "La Composición pendiente será descartada",
  );
  await expect(consequences).toContainText(
    "El pedido terminará excepcionalmente",
  );
  await expect(consequences).toContainText(
    "No se creará Liquidación ni Cierre",
  );
  await expect(
    consequences
      .getByRole("table", { name: "Consecuencias informadas" })
      .locator("tbody tr"),
  ).toHaveCount(1);
  await expect(consequences.locator("tbody tr td")).toHaveText([
    "1",
    "1",
    "0",
    "0",
  ]);

  // Only the terminal command is submitted: no Discard or Preparation Correction batch.
  const commands: string[] = [];
  const recordCommand = (request: import("@playwright/test").Request) => {
    if (request.method() === "POST")
      commands.push(new URL(request.url()).pathname);
  };
  page.on("request", recordCommand);
  const cancelled = page.waitForResponse(
    (response) =>
      response
        .url()
        .endsWith(
          `/api/orders/${operationalReference}/complete-cancellation`,
        ) && response.request().method() === "POST",
  );
  await consequences
    .getByRole("button", {
      name: "Confirmar cancelación completa",
      exact: true,
    })
    .click();
  const cancelledResponse = await cancelled;
  expect(cancelledResponse.ok()).toBeTruthy();
  expect(await cancelledResponse.json()).toMatchObject({
    orderId: operationalReference,
    isCompletelyCancelled: true,
    pendingCompositionDiscarded: true,
    consequences: [
      {
        incorporationId,
        contentOrdinal: 1,
        directOrPendingQuantity: 1,
        inPreparationQuantity: 1,
        readyQuantity: 0,
        resultingFulfillmentQuantity: 0,
      },
    ],
  });
  await expect(
    page
      .getByRole("status")
      .filter({ hasText: "Pedido completamente cancelado:" }),
  ).toContainText("La Composición pendiente fue descartada");
  await expect(
    pendingComposition.getByText(
      "Productos pendientes de confirmar en este pedido",
      {
        exact: true,
      },
    ),
  ).toHaveCount(0);
  await expect(pendingComposition).toHaveCount(0);
  await expect(
    page.getByRole("button", { name: "Agregar productos a este Pedido" }),
  ).toHaveCount(0);
  await expect(cancellation).toHaveCount(0);
  await expect(ending).toHaveCount(0);
  expect(commands).toEqual([
    `/api/orders/${operationalReference}/complete-cancellation`,
  ]);
  page.off("request", recordCommand);

  await page
    .getByRole("button", { name: "Intervención en preparación" })
    .click();
  await intervention
    .getByRole("button", { name: "Actualizar contenido", exact: true })
    .click();
  await quantity(intervention, "Cantidad confirmada", "2");
  await quantity(intervention, "Quitada por corrección", "0");
  await quantity(intervention, "Cantidad cancelada", "2");
  for (const label of [
    "Cantidad requerida",
    "Pendiente",
    "En preparación",
    "Lista",
    "Total requerido",
    "Entregada",
  ])
    await quantity(intervention, label, "0");
  await expect(
    intervention.getByRole("button", { name: /Cancelar cantidad ya/ }),
  ).toHaveCount(0);

  // The active read is no longer authorized; terminal facts have a separate History read.
  const activeRead = await page.request.get(
    `/api/order-operations/orders/${operationalReference}`,
  );
  expect(activeRead.status()).toBe(404);
  const historyRead = await page.request.get(
    `/api/order-operations/order-history/${operationalReference}`,
  );
  expect(historyRead.status()).toBe(200);
  expect(await historyRead.json()).toMatchObject({
    termination: { type: "CompleteCancellation" },
    completeCancellation: { pendingCompositionDiscarded: true },
  });
  await page.reload();
  await expect(
    page.getByRole("region", { name: "Usuario actual" }),
  ).toBeVisible();
  await page
    .getByLabel("Referencia del pedido finalizado")
    .fill(operationalReference);
  await page.getByRole("button", { name: "Ver historial" }).click();
  await expect(
    page.getByRole("region", { name: "Historial del pedido" }),
  ).toContainText(productName);
  await expect(
    page.getByRole("button", {
      name: /^(Agregar productos a este Pedido|Liquidar|Cerrar pedido|Cancelar pedido completo)$/,
    }),
  ).toHaveCount(0);
});
