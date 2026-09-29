import { randomUUID } from "node:crypto";
import { expect, test, type Page } from "@playwright/test";

async function login(page: Page, identifier: string, secret: string) {
  await page.goto("/");
  await page.getByLabel("Identificador de acceso").fill(identifier);
  await page.getByLabel("Secreto").fill(secret);
  await page.getByRole("button", { name: "Ingresar" }).click();
  await expect(
    page.getByRole("region", { name: "Usuario actual" }),
  ).toBeVisible();
}

test("eligible Identity is removed and functional Order History preserves another Identity", async ({
  browser,
}) => {
  const unique = randomUUID().slice(0, 8);
  const eligibleName = `Identity elegible ${unique}`;
  const actorName = `Identity con Historia ${unique}`;
  const actorLogin = `identity-delete-${unique}`;
  const actorSecret = `secret-${unique}`;
  const productName = `Producto Identity Delete ${unique}`;
  const adminContext = await browser.newContext();
  const catalogContext = await browser.newContext();
  const actorContext = await browser.newContext();
  try {
    const admin = await adminContext.newPage();
    const catalog = await catalogContext.newPage();
    const actor = await actorContext.newPage();
    await login(
      admin,
      "general-configuration-admin-a-e2e",
      "general-configuration-admin-a-e2e-secret",
    );
    const general = admin
      .getByRole("heading", { name: "Configuración general" })
      .locator("..");
    await expect(general).toBeVisible();

    async function createIdentity(name: string) {
      await general
        .getByLabel("Nombre operacional", { exact: true })
        .fill(name);
      await general.getByRole("button", { name: "Crear identidad" }).click();
      await expect(
        general.getByText("Identidad creada correctamente."),
      ).toBeVisible();
      await general.getByRole("button", { name: "Actualizar" }).click();
      return general
        .getByRole("row")
        .filter({ has: admin.getByRole("cell", { name, exact: true }) });
    }

    const eligible = await createIdentity(eligibleName);
    await expect(eligible).toBeVisible();
    await eligible
      .getByRole("button", { name: `Eliminar definitivamente ${eligibleName}` })
      .click();
    await expect(
      general.getByRole("region", {
        name: `Confirmar eliminación de ${eligibleName}`,
      }),
    ).toContainText("Historia funcional relevante");
    await general
      .getByRole("button", { name: "Confirmar eliminación definitiva" })
      .click();
    await expect(eligible).toHaveCount(0);

    const target = await createIdentity(actorName);
    await expect(target).toBeVisible();
    await target
      .getByRole("button", {
        name: `Asignar Pedidos y cierre básico a ${actorName}`,
      })
      .click();
    await expect(
      target.getByRole("list", { name: `Responsabilidades de ${actorName}` }),
    ).toContainText("Pedidos y cierre básico: Asignada");
    await target
      .getByRole("button", { name: `Configurar credencial de ${actorName}` })
      .click();
    const credential = general.getByRole("form", {
      name: "Configurar credencial local",
    });
    await credential.getByLabel("Identificador de acceso").fill(actorLogin);
    await credential.getByLabel("Nueva clave secreta").fill(actorSecret);
    await credential
      .getByRole("button", { name: "Guardar credencial" })
      .click();
    await expect(
      target.getByRole("cell", { name: "Configurada", exact: true }),
    ).toBeVisible();
    await target.getByRole("button", { name: `Activar ${actorName}` }).click();
    await expect(
      target.getByRole("cell", { name: "Activa", exact: true }),
    ).toBeVisible();

    await login(catalog, "price-catalog-e2e", "price-catalog-e2e-secret");
    await catalog
      .getByLabel("Nombre operacional", { exact: true })
      .fill(productName);
    await catalog.getByLabel("Precio", { exact: true }).fill("5");
    await catalog.getByRole("button", { name: "Crear producto" }).click();
    await expect(
      catalog.getByRole("row").filter({
        has: catalog.getByRole("cell", { name: productName, exact: true }),
      }),
    ).toBeVisible();

    await login(actor, actorLogin, actorSecret);
    const composition = actor.getByRole("region", {
      name: "Composición inicial",
    });
    await composition
      .getByRole("button", {
        name: `Agregar ${productName} a Composición inicial`,
      })
      .click();
    await composition
      .getByLabel("Contexto para Primera Confirmacion")
      .selectOption({ label: "Contexto base E2E" });
    const confirmation = actor.waitForResponse(
      (response) =>
        new URL(response.url()).pathname ===
          "/api/order-operations/first-confirmations" &&
        response.request().method() === "POST",
    );
    await composition
      .getByRole("button", { name: "Confirmar Primera Composición" })
      .click();
    expect((await confirmation).ok()).toBeTruthy();

    await target
      .getByRole("button", { name: `Eliminar definitivamente ${actorName}` })
      .click();
    await general
      .getByRole("button", { name: "Confirmar eliminación definitiva" })
      .click();
    await expect(
      general.getByText(
        /debe conservarse porque tiene operaciones registradas a su nombre/,
      ),
    ).toBeVisible();
    await expect(target).toBeVisible();
    await expect(
      target.getByRole("button", { name: `Desactivar ${actorName}` }),
    ).toBeVisible();
  } finally {
    await adminContext.close();
    await catalogContext.close();
    await actorContext.close();
  }
});
