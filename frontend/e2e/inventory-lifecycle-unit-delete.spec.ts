import { expect, test, type Locator, type Page } from "@playwright/test";
import { randomUUID } from "node:crypto";

const configurator = {
  identifier: "inventory-config-e2e",
  secret: "inventory-config-e2e-secret",
  name: "Configurador Inventario E2E",
};
const operator = {
  identifier: "inventory-operation-e2e",
  secret: "inventory-operation-e2e-secret",
  name: "Operador Inventario E2E",
};

async function login(page: Page, actor: typeof configurator) {
  await page.goto("/");
  await page.getByLabel("Identificador de acceso").fill(actor.identifier);
  await page.getByLabel("Secreto").fill(actor.secret);
  await page.getByRole("button", { name: "Ingresar" }).click();
  await expect(
    page.getByRole("region", { name: "Usuario actual" }).getByText(actor.name, {
      exact: true,
    }),
  ).toBeVisible();
}

async function logout(page: Page) {
  await page
    .getByRole("region", { name: "Usuario actual" })
    .getByRole("button", { name: "Cambiar persona / salir" })
    .click();
  await expect(page.getByRole("heading", { name: "Ingresar" })).toBeVisible();
}

async function refreshConfiguration(page: Page) {
  const read = page.waitForResponse(
    (response) =>
      new URL(response.url()).pathname ===
        "/api/inventory/configuration/items" &&
      response.request().method() === "GET" &&
      response.ok(),
  );
  await page.getByRole("button", { name: "Actualizar configuración" }).click();
  return read;
}

async function refreshOperation(page: Page) {
  const read = page.waitForResponse(
    (response) =>
      new URL(response.url()).pathname === "/api/inventory/operations/items" &&
      response.request().method() === "GET" &&
      response.ok(),
  );
  await page.getByRole("button", { name: "Actualizar estado" }).click();
  await read;
}

function configurationItem(config: Locator, name: string): Locator {
  return config
    .getByRole("list", { name: "Elementos configurados" })
    .getByRole("listitem")
    .filter({ hasText: name });
}

async function openConfigurationItem(item: Locator): Promise<void> {
  const detail = item.locator("details.inventory-configuration-item");
  if ((await detail.getAttribute("open")) === null) {
    await detail.locator(":scope > summary").click();
  }
}

test("MVP-FC-INV-LU-I3 lifecycle and Unit across physical existence", async ({
  page,
}) => {
  const suffix = `${Date.now()}-${randomUUID().slice(0, 8)}`;
  const name = `Elemento LU I3 ${suffix}`;

  await login(page, configurator);
  const config = page.getByRole("region", {
    name: "Configuración de Inventario",
  });
  await config.getByLabel("Nombre operacional").fill(name);
  await config.locator("#inventory-operational-unit").fill("U1");
  const createdPromise = page.waitForResponse(
    (response) =>
      new URL(response.url()).pathname === "/api/inventory/items" &&
      response.request().method() === "POST" &&
      response.ok(),
  );
  await config.getByRole("button", { name: "Crear elemento" }).click();
  const created = await createdPromise;
  const createdItem = (await created.json()) as { itemId: string };
  expect(createdItem.itemId).toBeTruthy();
  let row = configurationItem(config, name);
  await expect(row).toContainText("Activo");
  await expect(row).toContainText("U1");
  await expect(row).toContainText("Requiere conteo y reconciliación");
  await expect(
    page.getByRole("region", { name: "Estado actual de Inventario" }),
  ).toHaveCount(0);

  await openConfigurationItem(row);
  await row.getByText(`Corregir unidad de ${name}`).click();
  await row.getByLabel(`Unidad observada actualmente de ${name}`).fill("U1");
  await row.getByLabel(`Nueva Unidad de ${name}`).fill("U2");
  const unitCorrection = page.waitForResponse(
    (response) =>
      new URL(response.url()).pathname.endsWith("/unit-corrections") &&
      response.request().method() === "POST",
  );
  await row.getByRole("button", { name: "Corregir Unidad" }).click();
  expect((await unitCorrection).ok()).toBeTruthy();
  await refreshConfiguration(page);
  await expect(row).toContainText("U2");
  await expect(row).toContainText("Requiere conteo y reconciliación");

  await logout(page);
  await login(page, operator);
  await expect(
    page.getByRole("region", { name: "Configuración de Inventario" }),
  ).toHaveCount(0);
  const operation = page.getByRole("region", {
    name: "Estado actual de Inventario",
  });
  let item = operation.getByRole("article", { name });
  await expect(item).toContainText("Existencia física no establecida");
  await expect(item).toContainText("U2");
  await expect(
    item.getByRole("button", { name: "Registrar entrada" }),
  ).toHaveCount(0);
  await expect(
    item.getByRole("button", { name: "Registrar salida manual" }),
  ).toHaveCount(0);
  await expect(
    item.getByRole("button", { name: "Registrar merma" }),
  ).toHaveCount(0);

  await item.getByRole("button", { name: "Conteo" }).click();
  await item.getByLabel(`Cantidad observada para ${name}`).fill("8");
  await item.getByRole("button", { name: "Registrar conteo" }).click();
  await expect(item).toContainText("Conteo registrado: 8 U2");
  await item.getByRole("button", { name: "Reconciliar conteo" }).click();
  await expect(item).toContainText("Existencia registrada8 U2");
  await expect(item.getByRole("button", { name: "Entrada" })).toBeVisible();
  await item.getByRole("button", { name: "Entrada" }).click();
  await item.getByLabel(`Cantidad de entrada para ${name}`).fill("2");
  await item.getByRole("button", { name: "Registrar entrada" }).click();
  await expect(item).toContainText("Existencia registrada10 U2");
  await item
    .getByRole("button", { name: `Ver movimientos de ${name}` })
    .click();
  const history = operation.getByRole("region", { name: "Movimientos" });
  await expect(history.getByRole("heading", { name: "Entrada" })).toBeVisible();
  await expect(history).toContainText("Saldo resultante10 U2");
  await expect(history).toContainText("Existencia establecida mediante conteo");

  await logout(page);
  await login(page, configurator);
  await refreshConfiguration(page);
  row = configurationItem(config, name);
  await expect(row).toContainText("U2");
  await openConfigurationItem(row);
  await row.getByText("Por qué no puede cambiarse la unidad").click();
  await expect(row).toContainText("La unidad ya no puede cambiarse");
  await expect(
    row.getByRole("button", { name: "Corregir Unidad" }),
  ).toHaveCount(0);
  await expect(row).toContainText("retiralo, creá un elemento nuevo");
  await expect(row).toContainText(
    "establecé su existencia mediante conteo y reconciliación",
  );

  await openConfigurationItem(row);
  await row.getByRole("button", { name: `Retirar ${name}` }).click();
  await expect(row).toContainText("Retirado");
  const retiredConfigurationRead = await refreshConfiguration(page);
  const retiredConfiguration = (await retiredConfigurationRead.json()) as {
    itemId: string;
    operationalName: string;
  }[];
  expect(
    retiredConfiguration.find((candidate) => candidate.operationalName === name)
      ?.itemId,
  ).toBe(createdItem.itemId);
  row = configurationItem(config, name);
  await expect(row).toContainText("Retirado");
  await expect(row).toContainText("Reactivar");
  await expect(row).not.toContainText("10 U2");
  await openConfigurationItem(row);
  await expect(
    row.getByRole("button", { name: `Reactivar ${name}` }),
  ).toBeVisible();

  await logout(page);
  await login(page, operator);
  await refreshOperation(page);
  await expect(operation.getByRole("article", { name })).toHaveCount(0);

  await logout(page);
  await login(page, configurator);
  await refreshConfiguration(page);
  row = configurationItem(config, name);
  await openConfigurationItem(row);
  await row.getByRole("button", { name: `Reactivar ${name}` }).click();
  await expect(row).toContainText("Activo");
  await refreshConfiguration(page);
  row = configurationItem(config, name);
  await expect(row).toContainText("Activo");
  await expect(row).toContainText("Requiere conteo y reconciliación");
  await expect(row).toContainText("U2");

  await logout(page);
  await login(page, operator);
  await refreshOperation(page);
  item = operation.getByRole("article", { name });
  await expect(item).toContainText("Existencia física no establecida");
  await expect(item).toContainText("U2");
  await expect(
    item.getByRole("button", { name: "Registrar entrada" }),
  ).toHaveCount(0);
  await item.getByRole("button", { name: "Conteo" }).click();
  await item.getByLabel(`Cantidad observada para ${name}`).fill("11");
  await item.getByRole("button", { name: "Registrar conteo" }).click();
  await item.getByRole("button", { name: "Reconciliar conteo" }).click();
  await expect(item).toContainText("Existencia registrada11 U2");
  await item.getByRole("button", { name: "Entrada" }).click();
  await item.getByLabel(`Cantidad de entrada para ${name}`).fill("1");
  await item.getByRole("button", { name: "Registrar entrada" }).click();
  await expect(item).toContainText("Existencia registrada12 U2");
  await item
    .getByRole("button", { name: `Ver movimientos de ${name}` })
    .click();
  const restoredHistory = operation.getByRole("region", {
    name: "Movimientos",
  });
  await expect(
    restoredHistory.getByRole("heading", { name: "Entrada" }).first(),
  ).toBeVisible();
  await expect(restoredHistory).toContainText("12 U2");
});

test("MVP-FC-INV-LU-I3 eligible definitive Delete through Configuration", async ({
  page,
}) => {
  const suffix = `${Date.now()}-${randomUUID().slice(0, 8)}`;
  const name = `Elemento Delete I3 ${suffix}`;
  await login(page, configurator);
  const config = page.getByRole("region", {
    name: "Configuración de Inventario",
  });
  await config.getByLabel("Nombre operacional").fill(name);
  await config.locator("#inventory-operational-unit").fill("ud");
  await config.getByRole("button", { name: "Crear elemento" }).click();
  const row = configurationItem(config, name);
  await expect(row).toContainText("Activo");
  await expect(row).toContainText("Requiere conteo y reconciliación");
  await openConfigurationItem(row);
  await expect(
    row.getByRole("button", { name: `Retirar ${name}` }),
  ).toBeVisible();
  await expect(
    row.getByRole("button", { name: `Eliminar definitivamente ${name}` }),
  ).toBeVisible();
  await expect(
    row.getByRole("button", { name: `Retirar ${name}` }),
  ).not.toHaveAttribute("aria-label", `Eliminar definitivamente ${name}`);
  await row
    .getByRole("button", { name: `Eliminar definitivamente ${name}` })
    .click();
  const confirmation = page.getByRole("alertdialog", {
    name: "Confirmar eliminación definitiva",
  });
  await expect(confirmation).toContainText("No se puede deshacer");
  await expect(
    confirmation.getByRole("button", {
      name: "Confirmar eliminación definitiva",
    }),
  ).toBeVisible();
  const deletion = page.waitForResponse(
    (response) =>
      new URL(response.url()).pathname.endsWith("/delete") &&
      response.request().method() === "POST",
  );
  await confirmation
    .getByRole("button", { name: "Confirmar eliminación definitiva" })
    .click();
  expect((await deletion).ok()).toBeTruthy();
  await refreshConfiguration(page);
  await expect(configurationItem(config, name)).toHaveCount(0);
  await refreshConfiguration(page);
  await expect(configurationItem(config, name)).toHaveCount(0);

  await logout(page);
  await login(page, operator);
  await refreshOperation(page);
  await expect(
    page
      .getByRole("region", { name: "Estado actual de Inventario" })
      .getByRole("article", { name }),
  ).toHaveCount(0);
});
