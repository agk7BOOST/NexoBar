import { randomUUID } from "node:crypto";
import { expect, test, type Locator, type Page } from "@playwright/test";

function rowForProduct(
  container: Locator,
  page: Page,
  productName: string,
): Locator {
  return container.getByRole("row").filter({
    has: page.getByRole("cell", { name: productName, exact: true }),
  });
}

function compositionRowForProduct(
  composition: Locator,
  page: Page,
  productName: string,
): Locator {
  return composition.getByRole("row").filter({
    has: page.getByRole("cell", {
      name: `Cantidad de ${productName}`,
      exact: true,
    }),
  });
}

async function readActiveOperationalReference(
  composition: Locator,
): Promise<string> {
  const label = "Referencia del Pedido activo";
  const action = "Iniciar nuevo Pedido";
  const activeOrderSummary = composition.getByRole("status").filter({
    hasText: label,
  });

  await expect(activeOrderSummary).toBeVisible();

  const summaryLines = (await activeOrderSummary.innerText())
    .split("\n")
    .map((line) => line.trim())
    .filter((line) => line.length > 0);
  const labelIndex = summaryLines.indexOf(label);
  const actionIndex = summaryLines.indexOf(action);
  const operationalReference = summaryLines
    .slice(labelIndex + 1, actionIndex === -1 ? undefined : actionIndex)
    .join("\n")
    .trim();

  expect(labelIndex).toBeGreaterThanOrEqual(0);
  expect(operationalReference).not.toBe("");

  return operationalReference;
}

async function createConfirmedOrder(page: Page): Promise<{
  productName: string;
  operationalReference: string;
}> {
  const productName = `E2E-${Date.now()}-${randomUUID().slice(0, 8)}`;

  await page.goto("/");
  await page.getByLabel("Nombre operacional").fill(productName);
  await page.getByLabel("Precio", { exact: true }).fill("10");
  await page.getByRole("button", { name: "Crear producto" }).click();

  const products = page.getByRole("region", { name: "Productos vigentes" });
  const productRow = rowForProduct(products, page, productName);
  await expect(productRow).toBeVisible();
  await expect(
    productRow.getByRole("cell", { name: "10", exact: true }),
  ).toBeVisible();

  const initialComposition = page.getByRole("region", {
    name: "Composición inicial",
  });
  const addButton = initialComposition.getByRole("button", {
    name: `Agregar ${productName} a Composición inicial`,
  });
  await addButton.click();
  await addButton.click();

  const compositionRow = compositionRowForProduct(
    initialComposition,
    page,
    productName,
  );
  await expect(
    compositionRow.getByRole("cell", {
      name: `Cantidad de ${productName}`,
      exact: true,
    }),
  ).toHaveText("2");
  await initialComposition.getByLabel("Contexto").fill("Mesa 7");
  await initialComposition
    .getByRole("button", { name: "Confirmar Primera Composición" })
    .click();

  const subsequentComposition = page.getByRole("region", {
    name: "Nueva Composición",
  });
  await expect(subsequentComposition).toBeVisible();

  const activeOrder = page.getByRole("region", { name: "Pedido activo" });
  await expect(activeOrder).toBeVisible();

  const incorporation1 = activeOrder.getByRole("article", {
    name: "Incorporación 1",
  });
  await expect(incorporation1).toBeVisible();

  const operationalReference = await readActiveOperationalReference(
    subsequentComposition,
  );
  await expect(
    activeOrder.getByText(operationalReference, { exact: true }),
  ).toBeVisible();
  await expect(activeOrder.getByText("Mesa 7", { exact: true })).toBeVisible();

  const firstItem = rowForProduct(incorporation1, page, productName);
  await expect(firstItem).toBeVisible();
  await expect(
    firstItem.getByRole("cell", { name: "2", exact: true }),
  ).toBeVisible();
  await expect(
    firstItem.getByRole("cell", { name: "10", exact: true }),
  ).toBeVisible();
  await expect(incorporation1.getByRole("time")).toHaveAttribute(
    "datetime",
    /\S+/,
  );

  return { productName, operationalReference };
}

async function lookupActiveOrder(page: Page, operationalReference: string) {
  await page.getByLabel("Referencia operacional").fill(operationalReference);
  await page.getByRole("button", { name: "Buscar Pedido" }).click();
  return page.getByRole("region", { name: "Pedido activo" });
}

test("conserva el Precio aplicado histórico entre Incorporaciones del mismo Pedido", async ({
  page,
}) => {
  const { productName, operationalReference } =
    await createConfirmedOrder(page);

  const products = page.getByRole("region", { name: "Productos vigentes" });
  const productRow = rowForProduct(products, page, productName);
  await productRow
    .getByRole("button", { name: `Cambiar precio de ${productName}` })
    .click();

  const priceChangeForm = page.getByRole("form", {
    name: `Cambiar precio de ${productName}`,
  });
  await priceChangeForm.getByLabel("Nuevo precio").fill("12");
  await priceChangeForm
    .getByRole("button", { name: "Confirmar cambio de Precio" })
    .click();

  await expect(
    productRow.getByRole("cell", { name: "12", exact: true }),
  ).toBeVisible();

  const subsequentComposition = page.getByRole("region", {
    name: "Nueva Composición",
  });
  await expect(subsequentComposition).toBeVisible();
  await expect(
    subsequentComposition.getByText(operationalReference, { exact: true }),
  ).toBeVisible();

  const availableProducts = subsequentComposition.getByRole("region", {
    name: "Productos para la Composición",
  });
  const availableProductRow = rowForProduct(
    availableProducts,
    page,
    productName,
  );
  await expect(
    availableProductRow.getByRole("cell", { name: "12", exact: true }),
  ).toBeVisible();
  await subsequentComposition
    .getByRole("button", {
      name: `Agregar ${productName} a Nueva Composición`,
    })
    .click();

  const subsequentCompositionRow = compositionRowForProduct(
    subsequentComposition,
    page,
    productName,
  );
  await expect(
    subsequentCompositionRow.getByRole("cell", {
      name: `Cantidad de ${productName}`,
      exact: true,
    }),
  ).toHaveText("1");
  await expect(
    subsequentCompositionRow.getByRole("cell", {
      name: "12",
      exact: true,
    }),
  ).toBeVisible();

  await subsequentComposition
    .getByRole("button", { name: "Confirmar nueva Incorporación" })
    .click();

  const activeOrder = page.getByRole("region", { name: "Pedido activo" });
  const incorporation2 = activeOrder.getByRole("article", {
    name: "Incorporación 2",
  });
  await expect(incorporation2).toBeVisible();

  const incorporation1 = activeOrder.getByRole("article", {
    name: "Incorporación 1",
  });
  const firstItem = rowForProduct(incorporation1, page, productName);
  await expect(firstItem).toBeVisible();
  await expect(
    firstItem.getByRole("cell", { name: "2", exact: true }),
  ).toBeVisible();
  await expect(
    firstItem.getByRole("cell", { name: "10", exact: true }),
  ).toBeVisible();

  const secondItem = rowForProduct(incorporation2, page, productName);
  await expect(secondItem).toBeVisible();
  await expect(
    secondItem.getByRole("cell", { name: "1", exact: true }),
  ).toBeVisible();
  await expect(
    secondItem.getByRole("cell", { name: "12", exact: true }),
  ).toBeVisible();
  await expect(
    activeOrder.getByText(operationalReference, { exact: true }),
  ).toBeVisible();
  await expect(
    subsequentComposition.getByText(operationalReference, { exact: true }),
  ).toBeVisible();
});

test("informa un Pedido inexistente sin conservar el resultado previo", async ({
  page,
}) => {
  const { operationalReference } = await createConfirmedOrder(page);
  const priorResult = await lookupActiveOrder(page, operationalReference);
  await expect(priorResult).toBeVisible();
  await expect(
    priorResult.getByText(operationalReference, { exact: true }),
  ).toBeVisible();

  const missingReference = "00000000-0000-0000-0000-000000000001";
  const referenceInput = page.getByLabel("Referencia operacional");
  await referenceInput.fill(missingReference);
  await page.getByRole("button", { name: "Buscar Pedido" }).click();

  await expect(
    page.getByText("No se encontró un Pedido con esa Referencia operacional.", {
      exact: true,
    }),
  ).toBeVisible();
  await expect(priorResult).toHaveCount(0);
  await expect(
    page.getByRole("region", { name: "Pedido consultado" }),
  ).toHaveCount(0);
  await expect(referenceInput).toHaveValue(missingReference);

  const subsequentComposition = page.getByRole("region", {
    name: "Nueva Composición",
  });
  await expect(
    subsequentComposition.getByText(operationalReference, { exact: true }),
  ).toBeVisible();
  await expect(
    subsequentComposition.getByText(missingReference, { exact: true }),
  ).toHaveCount(0);
});

test("dos preparadores progresan cantidades parciales del mismo Work", async ({
  page,
}) => {
  await page.goto("/");
  await page.getByLabel("Identificador de acceso").fill("preparador-e2e");
  await page.getByLabel("Secreto").fill("preparation-e2e-secret");
  await page.getByRole("button", { name: "Ingresar" }).click();

  const identity = page.getByRole("region", { name: "Identity actual" });
  await expect(
    identity.getByText("Preparador E2E", { exact: true }),
  ).toBeVisible();
  const preparation = page.getByRole("region", { name: "Preparación" });
  await expect(
    preparation.getByText("Cocina E2E", { exact: true }),
  ).toBeVisible();
  const preparationRow = preparation
    .getByRole("row")
    .filter({ hasText: "Papas E2E autorizadas" });
  await expect(preparationRow).toBeVisible();
  await expect(preparation.getByText("Sin sal", { exact: true })).toBeVisible();
  await expect(
    preparation.getByText("Trago E2E no autorizado", { exact: true }),
  ).toHaveCount(0);

  const startQuantity = preparation.getByLabel(
    "Cantidad a iniciar de Papas E2E autorizadas, incorporación 1, Mesa seguridad E2E, Sin sal",
  );
  await expect(startQuantity).toHaveValue("2");
  await startQuantity.fill("1");
  await preparation
    .getByRole("button", {
      name: "Iniciar Papas E2E autorizadas, incorporación 1, Mesa seguridad E2E, Sin sal",
    })
    .click();
  await expect(preparationRow).toContainText("Pendiente1");
  await expect(preparationRow).toContainText("En preparación1");
  await expect(preparationRow).toContainText("Listo0");

  await identity
    .getByRole("button", { name: "Cambiar persona / salir" })
    .click();
  await expect(page.getByRole("heading", { name: "Ingresar" })).toBeVisible();
  await expect(page.getByText("Preparador E2E", { exact: true })).toHaveCount(
    0,
  );

  await page.getByLabel("Identificador de acceso").fill("preparadora-b-e2e");
  await page.getByLabel("Secreto").fill("preparation-b-e2e-secret");
  await page.getByRole("button", { name: "Ingresar" }).click();

  const secondIdentity = page.getByRole("region", { name: "Identity actual" });
  await expect(
    secondIdentity.getByText("Preparadora E2E B", { exact: true }),
  ).toBeVisible();
  const secondPreparation = page.getByRole("region", { name: "Preparación" });
  const secondPreparationRow = secondPreparation
    .getByRole("row")
    .filter({ hasText: "Papas E2E autorizadas" });
  await expect(secondPreparationRow).toContainText("Pendiente1");
  await expect(secondPreparationRow).toContainText("En preparación1");
  const readyQuantity = secondPreparation.getByLabel(
    "Cantidad a marcar lista de Papas E2E autorizadas, incorporación 1, Mesa seguridad E2E, Sin sal",
  );
  await expect(readyQuantity).toHaveValue("1");
  await secondPreparation
    .getByRole("button", {
      name: "Marcar listo Papas E2E autorizadas, incorporación 1, Mesa seguridad E2E, Sin sal",
    })
    .click();

  await expect(secondPreparationRow).toContainText("Pendiente1");
  await expect(secondPreparationRow).toContainText("En preparación0");
  await expect(secondPreparationRow).toContainText("Listo1");
  await expect(secondPreparation.getByText(/Entreg/i)).toHaveCount(0);
});
