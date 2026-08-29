import { randomUUID } from "node:crypto";
import { expect, test, type Locator, type Page } from "@playwright/test";

function definitionValue(container: Locator, label: string): Locator {
  return container
    .getByText(label, { exact: true })
    .locator("..")
    .locator("dd");
}

async function createConfirmedOrder(page: Page): Promise<{
  productName: string;
  operationalReference: string;
}> {
  const productName = `E2E-${Date.now()}-${randomUUID().slice(0, 8)}`;

  await page.goto("/");
  await page.getByLabel("Nombre operacional").fill(productName);
  await page.getByLabel("Precio").fill("10.50");
  await page.getByRole("button", { name: "Crear producto" }).click();

  const products = page.getByRole("region", { name: "Productos vigentes" });
  await expect(products.getByText(productName, { exact: true })).toBeVisible();

  const addButton = page.getByRole("button", {
    name: `Agregar ${productName} a la composición`,
  });
  await addButton.click();
  await addButton.click();

  const composition = page.getByRole("region", { name: "Composición" });
  await expect(
    composition.getByRole("cell", {
      name: `Cantidad de ${productName}`,
      exact: true,
    }),
  ).toHaveText("2");
  await page.getByLabel("Contexto").fill("Mesa 7");
  await page.getByRole("button", { name: "Confirmar Composición" }).click();

  const createdOrder = page.getByRole("region", {
    name: "Pedido recién creado",
  });
  await expect(createdOrder).toBeVisible();
  await expect(definitionValue(createdOrder, "Contexto confirmado")).toHaveText(
    "Mesa 7",
  );
  await expect(
    definitionValue(createdOrder, "Primera Incorporación"),
  ).toHaveText(/\S+/);

  const operationalReference = await definitionValue(
    createdOrder,
    "Referencia operacional",
  ).innerText();
  expect(operationalReference).not.toBe("");

  const createdItem = createdOrder.getByRole("row").filter({
    has: page.getByRole("cell", { name: productName, exact: true }),
  });
  await expect(createdItem).toContainText(productName);
  await expect(createdItem.locator("td").nth(1)).toHaveText("2");
  await expect(createdItem.locator("td").nth(2)).toHaveText("10.50");

  return { productName, operationalReference };
}

async function lookupOrder(page: Page, operationalReference: string) {
  await page.getByLabel("Referencia operacional").fill(operationalReference);
  await page.getByRole("button", { name: "Buscar Pedido" }).click();
  return page.getByRole("region", { name: "Pedido consultado" });
}

test("crea y confirma un Pedido y luego lo consulta por su Referencia", async ({
  page,
}) => {
  const { productName, operationalReference } =
    await createConfirmedOrder(page);
  const consultedOrder = await lookupOrder(page, operationalReference);

  await expect(consultedOrder).toBeVisible();
  await expect(
    definitionValue(consultedOrder, "Referencia operacional"),
  ).toHaveText(operationalReference);
  await expect(definitionValue(consultedOrder, "Contexto actual")).toHaveText(
    "Mesa 7",
  );

  const incorporation = consultedOrder.getByRole("article", {
    name: "Incorporación 1",
  });
  await expect(definitionValue(incorporation, "Ordinal")).toHaveText("1");
  await expect(incorporation.getByRole("time")).toHaveText(/\S+/);
  await expect(incorporation.getByRole("time")).toHaveAttribute(
    "datetime",
    /\S+/,
  );

  const consultedItem = incorporation.getByRole("row").filter({
    has: page.getByRole("cell", { name: productName, exact: true }),
  });
  await expect(consultedItem).toContainText(productName);
  await expect(consultedItem.locator("td").nth(1)).toHaveText("2");
  await expect(consultedItem.locator("td").nth(2)).toHaveText("10.50");
});

test("informa un Pedido inexistente sin conservar el resultado previo", async ({
  page,
}) => {
  const { operationalReference } = await createConfirmedOrder(page);
  const priorResult = await lookupOrder(page, operationalReference);
  await expect(priorResult).toBeVisible();

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
  await expect(referenceInput).toHaveValue(missingReference);
});
