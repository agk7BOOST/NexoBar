import { expect, test, type Page } from "@playwright/test";
import { randomUUID } from "node:crypto";
import { selectInitialContext } from "./helpers/select-initial-context.js";

type Actor = {
  identifier: string;
  secret: string;
  operationalName: string;
};

const catalogActor: Actor = {
  identifier: "price-catalog-e2e",
  secret: "price-catalog-e2e-secret",
  operationalName: "Catálogo precios E2E",
};

const orderActor: Actor = {
  identifier: "delivery-e2e",
  secret: "delivery-e2e-secret",
  operationalName: "Delivery E2E",
};

async function login(page: Page, actor: Actor) {
  await page.goto("/");
  await page.getByLabel("Identificador de acceso").fill(actor.identifier);
  await page.getByLabel("Secreto").fill(actor.secret);
  await page.getByRole("button", { name: "Ingresar" }).click();
  await expect(
    page
      .getByRole("region", { name: "Usuario actual" })
      .getByText(actor.operationalName, { exact: true }),
  ).toBeVisible();
}

async function logout(page: Page) {
  await page
    .getByRole("region", { name: "Usuario actual" })
    .getByRole("button", { name: "Cambiar persona / salir" })
    .click();
  await expect(page.getByRole("heading", { name: "Ingresar" })).toBeVisible();
}

test("MVP-FC-CAT-I3 Catalog structure and Product lifecycle", async ({
  page,
}) => {
  const suffix = `${Date.now()}-${randomUUID().slice(0, 8)}`;
  const groupName = `Grupo I3 ${suffix}`;
  const productName = `Producto I3 ${suffix}`;
  const renamedProductName = `Producto I3 renombrado ${suffix}`;
  const catalog = page.getByRole("region", { name: "Productos", exact: true });

  await login(page, catalogActor);

  await page.getByLabel("Nombre operacional del Grupo").fill(groupName);
  await page.getByRole("button", { name: "Crear Grupo" }).click();
  await expect(
    page.getByRole("list", { name: "Grupos del Catálogo" }),
  ).toContainText(groupName);

  await page
    .getByLabel("Nombre operacional", { exact: true })
    .fill(productName);
  await page.getByLabel("Precio", { exact: true }).fill("10");
  const administrativeProductsResponsePromise = page.waitForResponse(
    (response) =>
      response.url().endsWith("/api/catalog/products") &&
      response.request().method() === "GET" &&
      response.ok(),
  );
  await page.getByRole("button", { name: "Crear producto" }).click();
  const administrativeProductsResponse =
    await administrativeProductsResponsePromise;
  const administrativeProducts =
    (await administrativeProductsResponse.json()) as {
      id: string;
      operationalName: string;
    }[];
  const administrativeProduct = administrativeProducts.find(
    (candidate) => candidate.operationalName === productName,
  );
  expect(administrativeProduct).toBeDefined();

  let product = catalog.getByRole("row").filter({
    has: page.getByRole("cell", { name: productName, exact: true }),
  });
  await expect(product).toContainText("Activo");
  await expect(product).toContainText("Disponible");
  await expect(product).toContainText("Sin Grupo");

  await product
    .getByRole("button", { name: `Configurar Grupo de ${productName}` })
    .click();
  await page
    .getByLabel(`Grupo del Producto de ${productName}`)
    .selectOption({ label: groupName });
  await page.getByRole("button", { name: "Confirmar Grupo" }).click();
  await expect(product).toContainText(groupName);

  await product
    .getByRole("button", { name: `Renombrar ${productName}` })
    .click();
  await page.getByLabel("Nuevo nombre operacional").fill(renamedProductName);
  await page.getByRole("button", { name: "Confirmar renombre" }).click();
  product = catalog.getByRole("row").filter({
    has: page.getByRole("cell", { name: renamedProductName, exact: true }),
  });
  await expect(product).toContainText(renamedProductName);
  await expect(product).toContainText(groupName);

  await logout(page);
  await login(page, orderActor);
  const composition = page.getByRole("region", { name: "Composición inicial" });
  await expect(
    composition.getByText(renamedProductName, { exact: true }),
  ).toBeVisible();
  await composition
    .getByRole("button", {
      name: `Agregar ${renamedProductName} a Composición inicial`,
    })
    .click();
  await selectInitialContext(composition);
  const confirmationResponsePromise = page.waitForResponse(
    (response) =>
      response.url().endsWith("/api/order-operations/first-confirmations") &&
      response.request().method() === "POST",
  );
  await composition
    .getByRole("button", { name: "Confirmar Primera Composición" })
    .click();
  const confirmationResponse = await confirmationResponsePromise;
  expect(confirmationResponse.ok()).toBeTruthy();
  const confirmation = (await confirmationResponse.json()) as {
    operationalReference: string;
    contextId: string;
    firstIncorporation: { id: string; items: { productId: string }[] };
  };
  expect(confirmation.firstIncorporation.items).toHaveLength(1);
  const productId = confirmation.firstIncorporation.items[0]?.productId;
  expect(productId).toBeTruthy();
  expect(productId).toBe(administrativeProduct!.id);

  await logout(page);
  await login(page, catalogActor);
  product = catalog.getByRole("row").filter({
    has: page.getByRole("cell", { name: renamedProductName, exact: true }),
  });
  await product
    .getByRole("button", { name: `Retirar ${renamedProductName}` })
    .click();
  await expect(product).toContainText("Retirado");
  await expect(product).toContainText("Disponible");
  await expect(product).toContainText(groupName);
  await expect(product).toContainText("10");
  await expect(product).toContainText("No requiere preparación");

  await logout(page);
  await login(page, orderActor);
  const retiredComposition = page.getByRole("region", {
    name: "Composición inicial",
  });
  await expect(
    retiredComposition.getByText(renamedProductName, { exact: true }),
  ).toHaveCount(0);

  const antiforgeryResponse = await page.request.get(
    "/api/security/antiforgery",
  );
  expect(antiforgeryResponse.ok()).toBeTruthy();
  const { requestToken } = (await antiforgeryResponse.json()) as {
    requestToken: string;
  };
  const staleConfirmation = await page.request.post(
    "/api/order-operations/first-confirmations",
    {
      headers: {
        "Idempotency-Key": randomUUID(),
        "X-NexoBar-CSRF": requestToken,
      },
      data: {
        contextId: confirmation.contextId,
        items: [{ productId, quantity: 1, instruction: null }],
      },
    },
  );
  expect(staleConfirmation.status()).toBe(409);
  expect((await staleConfirmation.json()).code).toBe(
    "order_operations.confirmation.product_not_current",
  );
  const preservedOrder = await page.request.get(
    `/api/order-operations/orders/${encodeURIComponent(confirmation.operationalReference)}`,
  );
  expect(preservedOrder.ok()).toBeTruthy();
  expect((await preservedOrder.json()).incorporations).toHaveLength(1);

  await logout(page);
  await login(page, catalogActor);
  product = catalog.getByRole("row").filter({
    has: page.getByRole("cell", { name: renamedProductName, exact: true }),
  });
  await product
    .getByRole("button", { name: `Reactivar ${renamedProductName}` })
    .click();
  await expect(product).toContainText("Activo");
  await expect(product).toContainText("Disponible");
  await expect(product).toContainText(groupName);
  await expect(product).toContainText("10");
  await expect(product).toContainText("No requiere preparación");

  await logout(page);
  await login(page, orderActor);
  await expect(
    page
      .getByRole("region", { name: "Composición inicial" })
      .getByText(renamedProductName, { exact: true }),
  ).toBeVisible();
});
