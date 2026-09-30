import { expect, test, type Page } from "@playwright/test";

const itemName = "Insumo corrección Inventario E2E";

async function signIn(page: Page) {
  await page.goto("/");
  await page
    .getByLabel("Usuario de acceso")
    .fill("inventory-operation-sse-a-e2e");
  await page
    .getByLabel("Contraseña")
    .fill("inventory-operation-sse-a-e2e-secret");
  await page.getByRole("button", { name: "Ingresar" }).click();
  await expect(
    page.getByRole("region", { name: "Usuario actual" }),
  ).toContainText("Operador Inventario SSE A E2E");
}

test("MVP-FC-INV-MC corrects the same root successively and preserves its history", async ({
  page,
}) => {
  await signIn(page);
  const item = page.getByRole("article", { name: itemName });
  await expect(item).toContainText("10 unidades");
  await item.getByRole("button", { name: "Entrada" }).click();
  await item.getByLabel(`Cantidad de entrada para ${itemName}`).fill("10");
  const entryResponse = page.waitForResponse(
    (r) =>
      new URL(r.url()).pathname.endsWith("/entries") &&
      r.request().method() === "POST",
  );
  await item.getByRole("button", { name: "Registrar entrada" }).click();
  expect((await entryResponse).ok()).toBeTruthy();
  await expect(item).toContainText("20 unidades");

  await item
    .getByRole("button", { name: `Ver movimientos de ${itemName}` })
    .click();
  const history = page.getByRole("region", { name: "Movimientos" });
  const original = history
    .getByRole("heading", { name: "Entrada" })
    .last()
    .locator("xpath=ancestor::li[contains(@class, 'inventory-movement')]");
  await expect(original).toContainText("10");
  await history
    .getByRole("button", { name: "Corregir movimiento" })
    .last()
    .click();
  await history.getByLabel("Cantidad corregida").fill("6");
  const correction1 = page.waitForResponse(
    (r) =>
      new URL(r.url()).pathname.endsWith("/corrections") &&
      r.request().method() === "POST",
  );
  await history.getByRole("button", { name: "Guardar corrección" }).click();
  expect((await correction1).ok()).toBeTruthy();
  await expect(item).toContainText("16 unidades");
  await expect(history).toContainText("Entrada 10 → Entrada 6");

  await history
    .getByRole("button", { name: "Corregir movimiento" })
    .last()
    .click();
  await history
    .getByLabel("Tipo de movimiento corregido")
    .selectOption("Waste");
  await history.getByLabel("Cantidad corregida").fill("2");
  const correction2 = page.waitForResponse(
    (r) =>
      new URL(r.url()).pathname.endsWith("/corrections") &&
      r.request().method() === "POST",
  );
  await history.getByRole("button", { name: "Guardar corrección" }).click();
  expect((await correction2).ok()).toBeTruthy();
  await expect(item).toContainText("8 unidades");
  await expect(history).toContainText("Entrada 6 → Merma 2");
  await expect(history).toContainText("Significado efectivo actual: Merma 2");
  await expect(original).toContainText("Entrada");
  await expect(
    history.getByText("Este movimiento queda sin efecto"),
  ).toHaveCount(0);
});
