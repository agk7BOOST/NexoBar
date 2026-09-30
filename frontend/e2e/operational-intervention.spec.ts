import { expect, test, type Page } from "@playwright/test";
import { selectPreparationDestination } from "./helpers/select-preparation-destination.js";

const productName = "Papas E2E intervención";
const orderContext = "Contexto base E2E";

async function login(
  page: Page,
  identifier: string,
  secret: string,
  identityName: string,
) {
  await page.goto("/");
  await page.getByLabel("Usuario de acceso").fill(identifier);
  await page.getByLabel("Contraseña").fill(secret);
  await page.getByRole("button", { name: "Ingresar" }).click();
  await expect(
    page
      .getByRole("region", { name: "Usuario actual" })
      .getByText(identityName, { exact: true }),
  ).toBeVisible();
}

async function logout(page: Page) {
  await page
    .getByRole("region", { name: "Usuario actual" })
    .getByRole("button", { name: "Cerrar sesión" })
    .click();
  await expect(page.getByRole("heading", { name: "Ingresar" })).toBeVisible();
}

async function readOperationalReference(page: Page): Promise<string> {
  const preparation = page.getByRole("region", { name: "Preparación" });
  const row = preparation.getByRole("row").filter({ hasText: productName });
  return (await row.locator(".technical-reference").innerText()).trim();
}

test("OperationalIntervention cancela Ready real y liquida la obligación reducida", async ({
  page,
}) => {
  await login(
    page,
    "preparador-e2e",
    "preparation-e2e-secret",
    "Preparador E2E",
  );
  const preparation = page.getByRole("region", { name: "Preparación" });
  await selectPreparationDestination(preparation, "Cocina E2E");
  const preparationRow = preparation
    .getByRole("row")
    .filter({ hasText: productName });
  const start = preparation.getByLabel(
    "Cantidad a iniciar de " +
      productName +
      ", incorporación 1, " +
      orderContext +
      ", Sin sal",
  );
  await expect(start).toHaveValue("2");
  await start.fill("2");
  await preparation
    .getByRole("button", {
      name:
        "Iniciar " +
        productName +
        ", incorporación 1, " +
        orderContext +
        ", Sin sal",
    })
    .click();
  await expect(preparationRow).toContainText("Pendiente0");
  await expect(preparationRow).toContainText("En preparación2");

  const operationalReference = await readOperationalReference(page);
  await logout(page);

  await login(
    page,
    "preparadora-b-e2e",
    "preparation-b-e2e-secret",
    "Preparadora E2E B",
  );
  const secondPreparation = page.getByRole("region", { name: "Preparación" });
  await selectPreparationDestination(secondPreparation, "Cocina E2E");
  const secondRow = secondPreparation
    .getByRole("row")
    .filter({ hasText: productName });
  await expect(secondRow).toContainText("Pendiente0");
  await expect(secondRow).toContainText("En preparación2");
  await secondPreparation
    .getByRole("button", {
      name:
        "Marcar listo " +
        productName +
        ", incorporación 1, " +
        orderContext +
        ", Sin sal",
    })
    .click();
  await expect(secondRow).toContainText("Pendiente0");
  await expect(secondRow).toContainText("En preparación0");
  await expect(secondRow).toContainText("Listo2");
  await logout(page);

  await login(page, "delivery-e2e", "delivery-e2e-secret", "Delivery E2E");
  await page
    .getByRole("textbox", { name: "Referencia del pedido", exact: true })
    .fill(operationalReference);
  await page.getByRole("button", { name: "Buscar pedido" }).click();
  const order = page.getByRole("region", { name: "Pedido consultado" });
  const incorporation = order.getByRole("article", { name: "Incorporación 1" });
  await incorporation
    .getByText("Detalle de la confirmación", { exact: true })
    .click();
  const incorporationId = (
    await incorporation
      .getByRole("definition")
      .filter({ hasText: /^[0-9a-f-]{36}$/i })
      .innerText()
  ).trim();
  await expect(incorporationId).toMatch(/^[0-9a-f-]{36}$/i);
  await page
    .getByRole("button", { name: "Abrir entrega de este pedido" })
    .click();
  const delivery = page.getByRole("region", {
    name: "Entrega del Pedido " + operationalReference,
  });
  const prepared = delivery.getByRole("article", {
    name: productName + ", Sin sal, incorporación 1",
  });
  await expect(prepared).toContainText("Listo2");
  await expect(prepared).toContainText("Entregado0");
  const deliveryQuantity = prepared.getByLabel(
    "Cantidad a entregar — " + productName + " — Sin sal — incorporación 1",
  );
  await deliveryQuantity.fill("1");
  await prepared
    .getByRole("button", {
      name: "Entregar " + productName + ", Sin sal, incorporación 1",
    })
    .click();
  await expect(prepared).toContainText("Listo2");
  await expect(prepared).toContainText("Entregado1");
  await expect(prepared).toContainText("Disponible para entregar1");
  await logout(page);

  await login(
    page,
    "intervention-e2e",
    "intervention-e2e-secret",
    "Intervención E2E",
  );
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
  await expect(
    intervention.getByText(productName, { exact: true }),
  ).toBeVisible();
  await expect(intervention).toContainText("En preparación0");
  await expect(intervention).toContainText("Lista2");
  await expect(intervention).toContainText("Entregada1");
  await expect(intervention).toContainText("Máximo: 1");
  await expect(
    intervention.getByRole("button", { name: "Cancelar cantidad ya lista" }),
  ).toBeVisible();
  await expect(
    intervention.getByRole("button", { name: /Corregir|Undo|Deshacer/i }),
  ).toHaveCount(0);
  await expect(intervention).not.toContainText(
    /desperdicio|descarte|recuperación|inventario/i,
  );
  const readyAmount = intervention.getByLabel("Cantidad ya lista a cancelar");
  await expect(readyAmount).toHaveAttribute("max", "1");
  await readyAmount.fill("1");
  await expect(
    intervention.getByRole("table", {
      name: "Vista previa: Cancelar cantidad ya lista",
    }),
  ).toContainText("Total requerido");
  await intervention
    .getByRole("button", { name: "Cancelar cantidad ya lista" })
    .click();
  await expect(intervention).toContainText(
    "Intervención registrada. Consultando la obligación vigente.",
  );
  await expect(intervention).toContainText("Cantidad cancelada1");
  await expect(intervention).toContainText("Cantidad requerida1");
  await expect(intervention).toContainText("Total requerido1");
  await expect(intervention).toContainText("Lista1");
  await expect(intervention).toContainText("Entregada1");
  await expect(intervention).toContainText(
    "No hay cantidad elegible para intervenir.",
  );
  await logout(page);

  await login(page, "delivery-e2e", "delivery-e2e-secret", "Delivery E2E");
  await page
    .getByRole("textbox", { name: "Referencia del pedido", exact: true })
    .fill(operationalReference);
  await page.getByRole("button", { name: "Buscar pedido" }).click();
  await page
    .getByRole("button", { name: "Abrir entrega de este pedido" })
    .click();
  const finalDelivery = page.getByRole("region", {
    name: "Entrega del Pedido " + operationalReference,
  });
  const finalPrepared = finalDelivery.getByRole("article", {
    name: productName + ", Sin sal, incorporación 1",
  });
  await expect(finalPrepared).toContainText("Listo1");
  await expect(finalPrepared).toContainText("Entregado1");
  await expect(finalPrepared).toContainText("Disponible para entregar0");
  const ending = page.getByRole("region", { name: "Liquidación y Cierre" });
  await expect(
    ending.getByText("Importe de lo entregado").locator("..").locator("dd"),
  ).toHaveText(/^7(?:\.0+)?$/);
  await expect(
    ending.getByRole("button", { name: "Liquidar", exact: true }),
  ).toBeEnabled();
  await ending.getByLabel("Medio de pago").fill("Efectivo");
  await ending.getByRole("button", { name: "Liquidar", exact: true }).click();
  await expect(ending.getByText(/Pedido congelado/)).toBeVisible();
  await expect(
    ending.getByText("Importe liquidado").locator("..").locator("dd"),
  ).toHaveText(/^7(?:\.0+)?$/);
});
