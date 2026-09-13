import { expect, test, type Page } from "@playwright/test";

const productName = "Papas SSE E2E";

async function signIn(page: Page, login: string, secret: string, name: string) {
  await page.goto("/");
  await page.getByLabel("Identificador de acceso").fill(login);
  await page.getByLabel("Secreto").fill(secret);
  await page.getByRole("button", { name: "Ingresar" }).click();
  await expect(page.getByRole("region", { name: "Identity actual" }))
    .toContainText(name);
}

test("Delivery abierto recibe por SSE la cantidad marcada lista por Preparation", async ({
  browser,
}) => {
  const deliveryContext = await browser.newContext();
  const preparationContext = await browser.newContext();
  try {
    const deliveryPage = await deliveryContext.newPage();
    const preparationPage = await preparationContext.newPage();

    await signIn(
      preparationPage,
      "preparation-sse-b-e2e",
      "preparation-sse-b-e2e-secret",
      "Preparador SSE B E2E",
    );
    const preparation = preparationPage.getByRole("region").filter({
      has: preparationPage.getByRole("heading", { name: /Prepar/ }),
    });
    const work = preparation.getByRole("row").filter({ hasText: productName });
    await expect(work).toContainText("Pendiente0");
    await expect(work).toContainText(/En prepar.*1/);
    await expect(work).toContainText("Listo0");
    const operationalReference = (
      await work.locator(".technical-reference").innerText()
    ).trim();
    expect(operationalReference).not.toBe("");

    await signIn(
      deliveryPage,
      "delivery-e2e",
      "delivery-e2e-secret",
      "Delivery E2E",
    );
    await deliveryPage
      .getByLabel("Referencia operacional")
      .fill(operationalReference);
    await deliveryPage.getByRole("button", { name: "Buscar Pedido" }).click();
    const order = deliveryPage.getByRole("region", { name: "Pedido consultado" });
    await expect(order).toBeVisible();

    const activeOrderStream = deliveryPage.waitForResponse((response) => {
      const url = new URL(response.url());
      return (
        url.pathname === "/api/notifications/stream" &&
        response.status() === 200 &&
        url.searchParams.getAll("scope").some((scope) =>
          scope.startsWith("order.active:"),
        )
      );
    });
    await order.getByRole("button", { name: "Continuar este Pedido" }).click();
    await expect(deliveryPage.getByRole("region", { name: "Pedido activo" }))
      .toBeVisible();
    await activeOrderStream;

    await deliveryPage
      .getByRole("button", { name: "Abrir entrega de este Pedido" })
      .click();
    const delivery = deliveryPage.getByRole("region", {
      name: `Entrega del Pedido ${operationalReference}`,
    });
    const content = delivery.getByRole("article", { name: new RegExp(productName) });
    await expect(content).toContainText("Ready0");
    await expect(content).toContainText("Delivered0");
    await expect(content).toContainText("Deliverable0");

    const readyQuantity = preparation.getByLabel(
      /Cantidad a marcar lista de Papas SSE E2E/,
    );
    await expect(readyQuantity).toHaveValue("1");
    await preparation.getByRole("button", {
      name: /Marcar listo Papas SSE E2E/,
    }).click();
    await expect(work).toContainText("Pendiente0");
    await expect(work).toContainText(/En prepar.*0/);
    await expect(work).toContainText("Listo1");

    // A remains on the already-open Delivery view: no reload, refresh, or re-selection.
    await expect(content).toContainText("Ready1", { timeout: 10_000 });
    await expect(content).toContainText("Deliverable1");
    const quantity = content.getByLabel(/^Cantidad a entregar.*Papas SSE E2E/);
    await expect(quantity).toHaveValue("1");
    await content.getByRole("button", { name: /Entregar Papas SSE E2E/ }).click();
    await expect(content).toContainText("Delivered1");
    await expect(content).toContainText("Deliverable0");
  } finally {
    await deliveryContext.close();
    await preparationContext.close();
  }
});
