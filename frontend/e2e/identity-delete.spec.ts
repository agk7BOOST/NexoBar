import { randomUUID } from "node:crypto";
import { expect, test, type Page } from "@playwright/test";

async function login(page: Page, identifier: string, secret: string) {
  await page.goto("/");
  await page.getByLabel("Usuario de acceso").fill(identifier);
  await page.getByLabel("Contraseña").fill(secret);
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
      await general.getByLabel("Nombre", { exact: true }).fill(name);
      await general.getByRole("button", { name: "Crear identidad" }).click();
      await expect(general.getByText(/Se creó la identidad/)).toBeVisible();
      await general.getByRole("button", { name: "Actualizar" }).click();
      return general.getByRole("region", {
        name: `Administrar identidad ${name}`,
      });
    }

    const eligible = await createIdentity(eligibleName);
    await expect(eligible).toBeVisible();
    await eligible
      .getByRole("button", { name: `Eliminar definitivamente ${eligibleName}` })
      .click();
    await expect(
      general.getByRole("dialog", {
        name: `Eliminar definitivamente “${eligibleName}”`,
      }),
    ).toContainText("operaciones cuya atribución deba conservarse");
    await general
      .getByRole("button", { name: "Eliminar definitivamente", exact: true })
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
      .getByRole("button", { name: `Configurar acceso de ${actorName}` })
      .click();
    const credential = general.getByRole("form", {
      name: `Configurar acceso de ${actorName}`,
    });
    await credential.getByLabel("Usuario de acceso").fill(actorLogin);
    await credential.getByLabel("Nueva contraseña").fill(actorSecret);
    await credential.getByRole("button", { name: "Guardar acceso" }).click();
    await expect(
      target.getByText("configurado", { exact: true }),
    ).toBeVisible();
    await target.getByRole("button", { name: `Activar ${actorName}` }).click();
    await expect(target.getByText("Activa", { exact: true })).toBeVisible();

    await login(catalog, "price-catalog-e2e", "price-catalog-e2e-secret");
    await catalog.getByLabel("Nombre", { exact: true }).fill(productName);
    await catalog.getByLabel("Precio", { exact: true }).fill("5");
    await catalog.getByRole("button", { name: "Crear producto" }).click();
    await expect(
      catalog.getByRole("row").filter({
        has: catalog.getByRole("cell", { name: productName, exact: true }),
      }),
    ).toBeVisible();

    await login(actor, actorLogin, actorSecret);
    const composition = actor.getByRole("region", {
      name: "Preparar pedido",
    });
    await composition
      .getByRole("button", {
        name: `Agregar ${productName} a Preparar pedido`,
      })
      .click();
    await composition
      .getByLabel("Contexto del pedido")
      .selectOption({ label: "Contexto base E2E" });
    const confirmation = actor.waitForResponse(
      (response) =>
        new URL(response.url()).pathname ===
          "/api/order-operations/first-confirmations" &&
        response.request().method() === "POST",
    );
    await composition.getByRole("button", { name: "Crear Pedido" }).click();
    expect((await confirmation).ok()).toBeTruthy();

    await target
      .getByRole("button", { name: `Eliminar definitivamente ${actorName}` })
      .click();
    await general
      .getByRole("button", { name: "Eliminar definitivamente", exact: true })
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
