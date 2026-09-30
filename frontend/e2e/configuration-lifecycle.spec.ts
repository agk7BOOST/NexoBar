import { randomUUID } from "node:crypto";
import { expect, test, type Page } from "@playwright/test";

async function login(page: Page, account: "general" | "catalog" | "order") {
  const accounts = {
    general: [
      "general-configuration-admin-a-e2e",
      "general-configuration-admin-a-e2e-secret",
      "Administradora general A E2E",
    ],
    catalog: [
      "price-catalog-e2e",
      "price-catalog-e2e-secret",
      "Catálogo precios E2E",
    ],
    order: ["delivery-e2e", "delivery-e2e-secret", "Delivery E2E"],
  };
  const [identifier, secret, name] = accounts[account];
  await page.goto("/");
  await page.getByLabel("Identificador de acceso").fill(identifier);
  await page.getByLabel("Secreto", { exact: true }).fill(secret);
  const response = page.waitForResponse(
    (r) =>
      new URL(r.url()).pathname === "/api/identity-sessions" &&
      r.request().method() === "POST",
  );
  await page.getByRole("button", { name: "Ingresar", exact: true }).click();
  expect((await response).ok()).toBeTruthy();
  await expect(
    page
      .getByRole("region", { name: "Usuario actual" })
      .getByText(name, { exact: true }),
  ).toBeVisible();
}

test("Context lifecycle excludes retired Contexts from new Orders and restores reactivated Contexts", async ({
  browser,
}) => {
  const administration = await browser.newContext(),
    operation = await browser.newContext();
  try {
    const g = await administration.newPage(),
      o = await operation.newPage();
    await login(g, "general");
    const name = `Context lifecycle ${randomUUID().slice(0, 8)}`;
    await g.getByLabel("Nombre operacional del Contexto").fill(name);
    await g
      .getByRole("button", { name: "Crear Contexto", exact: true })
      .click();
    const row = g
      .getByRole("list", { name: "Contextos configurados" })
      .getByRole("listitem")
      .filter({ has: g.getByText(name, { exact: true }) });
    await expect(row.getByText("Activo", { exact: true })).toBeVisible();
    await login(o, "order");
    await expect(
      o
        .getByLabel("Contexto para Primera Confirmacion")
        .getByRole("option", { name, exact: true }),
    ).toHaveCount(1);
    await row.getByRole("button", { name: "Retirar", exact: true }).click();
    await expect(
      row.getByText(/Los pedidos que ya lo usan no se modificarán/),
    ).toBeVisible();
    await row
      .getByRole("button", { name: "Confirmar retiro", exact: true })
      .click();
    await expect(row.getByText("Retirado", { exact: true })).toBeVisible();
    await o.reload();
    await expect(
      o.getByLabel("Contexto para Primera Confirmacion"),
    ).toBeVisible();
    await expect(
      o
        .getByLabel("Contexto para Primera Confirmacion")
        .getByRole("option", { name, exact: true }),
    ).toHaveCount(0);
    await row.getByRole("button", { name: "Reactivar", exact: true }).click();
    await expect(row.getByText("Activo", { exact: true })).toBeVisible();
    await o.reload();
    await expect(
      o
        .getByLabel("Contexto para Primera Confirmacion")
        .getByRole("option", { name, exact: true }),
    ).toHaveCount(1);
  } finally {
    await administration.close();
    await operation.close();
  }
});

test("Destination lifecycle blocks active Product dependencies and removes retired destinations from new configuration", async ({
  browser,
}) => {
  const administration = await browser.newContext(),
    catalogContext = await browser.newContext();
  try {
    const g = await administration.newPage(),
      c = await catalogContext.newPage();
    await login(g, "general");
    const destination = `Destination lifecycle ${randomUUID().slice(0, 8)}`;
    const product = `Product lifecycle ${randomUUID().slice(0, 8)}`;
    await g
      .getByLabel("Nombre operacional del destino de preparación")
      .fill(destination);
    await g
      .getByRole("button", {
        name: "Crear destino de preparación",
        exact: true,
      })
      .click();
    const row = g
      .getByRole("list", { name: "Listado de destinos de preparación" })
      .getByRole("listitem")
      .filter({ has: g.getByText(destination, { exact: true }) });
    await expect(row.getByText("Activo", { exact: true })).toBeVisible();
    await login(c, "catalog");
    const create = c.getByRole("region", {
      name: "Crear producto",
      exact: true,
    });
    await create
      .getByLabel("Nombre operacional", { exact: true })
      .fill(product);
    await create.getByLabel("Precio", { exact: true }).fill("5");
    await create
      .getByRole("button", { name: "Crear producto", exact: true })
      .click();
    const configure = c.getByRole("button", {
      name: `Configurar preparación de ${product}`,
      exact: true,
    });
    await configure.click();
    let editor = c.getByRole("form", {
      name: `Configurar preparación de ${product}`,
      exact: true,
    });
    await editor.getByLabel("Requiere preparación", { exact: true }).check();
    await editor
      .getByLabel("Destino de preparación")
      .selectOption({ label: destination });
    await editor
      .getByRole("button", {
        name: "Confirmar configuración de preparación",
        exact: true,
      })
      .click();
    await expect(editor).toHaveCount(0);
    await row.getByRole("button", { name: "Retirar", exact: true }).click();
    await row
      .getByRole("button", { name: "Confirmar retiro", exact: true })
      .click();
    await expect(
      row.getByText(/No se puede retirar porque todavía hay productos activos/),
    ).toBeVisible();
    await expect(row.getByText("Activo", { exact: true })).toBeVisible();
    await configure.click();
    editor = c.getByRole("form", {
      name: `Configurar preparación de ${product}`,
      exact: true,
    });
    await editor.getByLabel("Requiere preparación", { exact: true }).uncheck();
    await editor
      .getByRole("button", {
        name: "Confirmar configuración de preparación",
        exact: true,
      })
      .click();
    await expect(editor).toHaveCount(0);
    await row
      .getByRole("button", { name: "Confirmar retiro", exact: true })
      .click();
    await expect(row.getByText("Retirado", { exact: true })).toBeVisible();
    await c.reload();
    await c
      .getByRole("button", {
        name: `Configurar preparación de ${product}`,
        exact: true,
      })
      .click();
    editor = c.getByRole("form", {
      name: `Configurar preparación de ${product}`,
      exact: true,
    });
    await editor.getByLabel("Requiere preparación", { exact: true }).check();
    await expect(
      editor
        .getByLabel("Destino de preparación")
        .getByRole("option", { name: destination, exact: true }),
    ).toHaveCount(0);
  } finally {
    await administration.close();
    await catalogContext.close();
  }
});
