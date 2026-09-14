import { expect, test, type Page } from "@playwright/test";

const itemName = "Insumo SSE Inventario E2E";

async function signIn(page: Page, login: string, secret: string, name: string) {
  await page.goto("/");
  await page.getByLabel("Identificador de acceso").fill(login);
  await page.getByLabel("Secreto").fill(secret);
  await page.getByRole("button", { name: "Ingresar" }).click();
  await expect(page.getByRole("region", { name: "Identity actual" }))
    .toContainText(name);
}

test("Inventario abierto se actualiza por SSE tras una entrada de otro operador", async ({
  browser,
}) => {
  const operatorAContext = await browser.newContext();
  const operatorBContext = await browser.newContext();
  try {
    const operatorAPage = await operatorAContext.newPage();
    const operatorBPage = await operatorBContext.newPage();

    const operationStream = operatorAPage.waitForResponse((response) => {
      const url = new URL(response.url());
      return (
        url.pathname === "/api/notifications/stream" &&
        response.status() === 200 &&
        url.searchParams.getAll("scope").includes("inventory.operation")
      );
    });
    await signIn(
      operatorAPage,
      "inventory-operation-sse-a-e2e",
      "inventory-operation-sse-a-e2e-secret",
      "Operador Inventario SSE A E2E",
    );
    const operatorAItem = operatorAPage.getByRole("article", { name: itemName });
    await expect(operatorAItem).toContainText("Existencia registrada");
    await expect(operatorAItem).toContainText("10 unidades");
    await operationStream;

    await signIn(
      operatorBPage,
      "inventory-operation-sse-b-e2e",
      "inventory-operation-sse-b-e2e-secret",
      "Operador Inventario SSE B E2E",
    );
    const operatorBItem = operatorBPage.getByRole("article", { name: itemName });
    await expect(operatorBItem).toContainText("10 unidades");

    const operatorARefresh = operatorAPage.waitForResponse((response) =>
      response.frame() === operatorAPage.mainFrame() &&
      new URL(response.url()).pathname === "/api/inventory/operations/items" &&
      response.status() === 200,
    );
    await operatorBItem
      .getByLabel(`Cantidad de entrada para ${itemName}`)
      .fill("5");
    await operatorBItem
      .getByRole("button", { name: "Registrar entrada" })
      .click();
    await expect(operatorBItem.getByRole("status")).toContainText(
      "Entrada registrada. Saldo autoritativo resultante: 15 unidades.",
    );

    await operatorARefresh;
    await expect(operatorAItem).toContainText("15 unidades", { timeout: 10_000 });
  } finally {
    await operatorAContext.close();
    await operatorBContext.close();
  }
});
