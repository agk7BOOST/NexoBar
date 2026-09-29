import { randomUUID } from "node:crypto";
import { expect, test, type Page } from "@playwright/test";

async function login(
  page: Page,
  identifier: string,
  secret: string,
  operationalName: string,
) {
  await page.goto("/");
  await page.getByLabel("Identificador de acceso").fill(identifier);
  await page.getByLabel("Secreto").fill(secret);
  await page.getByRole("button", { name: "Ingresar" }).click();
  await expect(
    page
      .getByRole("region", { name: "Usuario actual" })
      .getByText(operationalName, { exact: true }),
  ).toBeVisible();
}

async function logout(page: Page) {
  await page
    .getByRole("region", { name: "Usuario actual" })
    .getByRole("button", { name: "Cambiar persona / salir" })
    .click();
  await expect(page.getByRole("heading", { name: "Ingresar" })).toBeVisible();
}

async function createProduct(page: Page, name: string) {
  await page.getByLabel("Nombre operacional", { exact: true }).fill(name);
  await page.getByLabel("Precio", { exact: true }).fill("5");
  await page.getByRole("button", { name: "Crear producto" }).click();
  await expect(
    page
      .getByRole("row")
      .filter({ has: page.getByRole("cell", { name, exact: true }) }),
  ).toBeVisible();
}

test("eligible Product is deleted and its operational name can be reused", async ({
  page,
}) => {
  const name = `Producto Delete ${randomUUID()}`;
  await login(
    page,
    "price-catalog-e2e",
    "price-catalog-e2e-secret",
    "Catálogo precios E2E",
  );
  await createProduct(page, name);
  const row = page
    .getByRole("row")
    .filter({ has: page.getByRole("cell", { name, exact: true }) });
  await row
    .getByRole("button", { name: `Eliminar definitivamente ${name}` })
    .click();
  await expect(
    page.getByRole("alertdialog", {
      name: "Confirmar eliminación definitiva de Producto",
    }),
  ).toContainText("nunca participó en un Pedido confirmado");
  await page
    .getByRole("button", { name: "Confirmar eliminación definitiva" })
    .click();
  await expect(row).toHaveCount(0);
  await createProduct(page, name);
  await expect(row).toHaveCount(1);
});

test("confirmed Product rejects Delete and Retire remains separate", async ({
  page,
}) => {
  const name = `Producto confirmado Delete ${randomUUID()}`;
  await login(
    page,
    "price-catalog-e2e",
    "price-catalog-e2e-secret",
    "Catálogo precios E2E",
  );
  await createProduct(page, name);
  await logout(page);
  await login(page, "delivery-e2e", "delivery-e2e-secret", "Delivery E2E");
  const composition = page.getByRole("region", { name: "Composición inicial" });
  await composition
    .getByRole("button", { name: `Agregar ${name} a Composición inicial` })
    .click();
  await composition
    .getByLabel("Contexto para Primera Confirmacion")
    .selectOption({ label: "Contexto base E2E" });
  const response = page.waitForResponse(
    (candidate) =>
      candidate.url().endsWith("/api/order-operations/first-confirmations") &&
      candidate.request().method() === "POST",
  );
  await composition.getByRole("button", { name: "Crear Pedido" }).click();
  expect((await response).ok()).toBeTruthy();
  await logout(page);
  await login(
    page,
    "price-catalog-e2e",
    "price-catalog-e2e-secret",
    "Catálogo precios E2E",
  );
  const row = page
    .getByRole("row")
    .filter({ has: page.getByRole("cell", { name, exact: true }) });
  await row
    .getByRole("button", { name: `Eliminar definitivamente ${name}` })
    .click();
  await page
    .getByRole("button", { name: "Confirmar eliminación definitiva" })
    .click();
  await expect(
    page.getByText(
      /no puede eliminarse porque participó en un Pedido confirmado/,
    ),
  ).toBeVisible();
  await expect(row).toBeVisible();
  await expect(
    row.getByRole("button", { name: `Retirar ${name}` }),
  ).toBeVisible();
});
