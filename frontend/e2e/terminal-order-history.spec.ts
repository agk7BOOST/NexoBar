import { expect, test, type Page } from "@playwright/test";

const oabc = {
  login: "delivery-e2e",
  secret: "delivery-e2e-secret",
  name: "Delivery E2E",
};
const general = {
  login: "general-configuration-admin-a-e2e",
  secret: "general-configuration-admin-a-e2e-secret",
  name: "Administradora general A E2E",
};
const cancellation = {
  login: "complete-cancellation-e2e",
  secret: "complete-cancellation-e2e-secret",
  name: "Cancelación completa E2E",
};
const productName = "Bebida E2E directa";

async function login(page: Page, actor: typeof oabc | typeof general) {
  await page.goto("/");
  await page.getByLabel("Identificador de acceso").fill(actor.login);
  await page.getByLabel("Secreto").fill(actor.secret);
  await page.getByRole("button", { name: "Ingresar" }).click();
  await expect(
    page
      .getByRole("region", { name: "Usuario actual" })
      .getByText(actor.name, { exact: true }),
  ).toBeVisible();
}

async function logout(page: Page) {
  await page
    .getByRole("region", { name: "Usuario actual" })
    .getByRole("button", { name: "Cambiar persona / salir" })
    .click();
  await expect(page.getByRole("heading", { name: "Ingresar" })).toBeVisible();
}

test("MVP-FC-TOH-CLOSE OABC consults closed Order History by exact reference", async ({
  page,
}) => {
  await login(page, general);
  await expect(page.getByLabel("Referencia operacional exacta")).toHaveCount(0);
  await expect(page.getByRole("button", { name: "Ver historial" })).toHaveCount(
    0,
  );
  await logout(page);

  await login(page, oabc);
  const composition = page.getByRole("region", { name: "Composición inicial" });
  await composition
    .getByRole("button", {
      name: `Agregar ${productName} a Composición inicial`,
    })
    .click();
  const context = composition.getByLabel("Contexto para Primera Confirmacion");
  await expect(
    context.getByRole("option", { name: "Contexto base E2E", exact: true }),
  ).toHaveCount(1);
  await context.selectOption({ label: "Contexto base E2E" });
  const confirmation = page.waitForResponse(
    (response) =>
      new URL(response.url()).pathname ===
        "/api/order-operations/first-confirmations" &&
      response.request().method() === "POST",
  );
  await composition.getByRole("button", { name: "Crear Pedido" }).click();
  const confirmedResponse = await confirmation;
  expect(confirmedResponse.status()).toBe(201);
  const confirmed = (await confirmedResponse.json()) as {
    operationalReference: string;
    context: string;
    firstIncorporation: {
      items: Array<{ productOperationalNameSnapshot: string | null }>;
    };
  };
  const reference = confirmed.operationalReference;
  expect(confirmed.context).toBe("Contexto base E2E");
  expect(
    confirmed.firstIncorporation.items[0]?.productOperationalNameSnapshot,
  ).toBe(productName);

  await page
    .getByRole("button", { name: "Abrir entrega de este Pedido" })
    .click();
  const delivery = page.getByRole("region", {
    name: `Entrega del Pedido ${reference}`,
  });
  const item = delivery.getByRole("article", { name: new RegExp(productName) });
  await item.getByRole("button", { name: /^Entregar / }).click();
  await expect(item).toContainText("Entregado1");
  await expect(item).toHaveAttribute("aria-busy", "false");

  const ending = page.getByRole("region", { name: "Liquidación y Cierre" });
  await expect(
    ending.getByText("Importe funcional actual").locator("..").locator("dd"),
  ).toHaveText("5");
  await ending.getByLabel("Medio de pago declarado").fill("Efectivo E2E");
  const activeAfterLiquidation = page.waitForResponse(
    (response) =>
      new URL(response.url()).pathname ===
        `/api/order-operations/orders/${reference}` &&
      response.request().method() === "GET",
  );
  const liquidation = page.waitForResponse(
    (response) =>
      new URL(response.url()).pathname.endsWith(
        `/api/order-operations/orders/${reference}/liquidate-simple`,
      ) && response.request().method() === "POST",
  );
  await ending.getByRole("button", { name: "Liquidar", exact: true }).click();
  expect((await liquidation).ok()).toBeTruthy();
  expect((await activeAfterLiquidation).status()).toBe(200);
  await expect(
    ending.getByRole("button", { name: "Cerrar Pedido" }),
  ).toBeEnabled();
  const closure = page.waitForResponse(
    (response) =>
      new URL(response.url()).pathname === `/api/orders/${reference}/close` &&
      response.request().method() === "POST",
  );
  await ending.getByRole("button", { name: "Cerrar Pedido" }).click();
  expect((await closure).ok()).toBeTruthy();
  const closed = page.getByRole("status").filter({
    hasText: `Pedido cerrado: ${reference}`,
  });
  await expect(closed.getByRole("time")).toHaveAttribute("datetime", /\S+/);
  await expect(ending).toHaveCount(0);

  const historyLookup = page.getByLabel("Referencia operacional exacta");
  await historyLookup.fill(reference);
  const historyRequest = page.waitForResponse(
    (response) =>
      new URL(response.url()).pathname ===
        `/api/order-operations/order-history/${reference}` &&
      response.request().method() === "GET",
  );
  await page.getByRole("button", { name: "Ver historial" }).click();
  const historyResponse = await historyRequest;
  expect(historyResponse.ok()).toBeTruthy();
  const history = (await historyResponse.json()) as {
    operationalReference: string;
    termination: { type: string };
    finalContextOperationalName: string;
    incorporations: Array<{
      contents: Array<{ productOperationalNameSnapshot: string | null }>;
    }>;
    liquidation: unknown;
    closure: unknown;
  };
  expect(history).toMatchObject({
    operationalReference: reference,
    termination: { type: "Closure" },
    finalContextOperationalName: confirmed.context,
    incorporations: [
      { contents: [{ productOperationalNameSnapshot: productName }] },
    ],
  });
  const result = page.getByRole("region", { name: "Historial del pedido" });
  await expect(result.getByText("Solo lectura", { exact: true })).toBeVisible();
  await expect(result.getByText(reference, { exact: true })).toBeVisible();
  await expect(
    result.getByText("Cierre", { exact: true }).first(),
  ).toBeVisible();
  await expect(
    result.getByText(confirmed.context, { exact: true }),
  ).toBeVisible();
  await expect(result.getByText(productName, { exact: true })).toBeVisible();
  await expect(
    result.getByRole("heading", { name: "Liquidación" }),
  ).toBeVisible();
  await expect(result.getByRole("heading", { name: "Cierre" })).toBeVisible();
  expect(history.liquidation).not.toBeNull();
  expect(history.closure).not.toBeNull();
  await expect(result.getByRole("button")).toHaveCount(0);
  await expect(result.getByRole("textbox")).toHaveCount(0);
  await expect(result.getByRole("combobox")).toHaveCount(0);
  await expect(result.getByRole("checkbox")).toHaveCount(0);
});

test("MVP-FC-TOH-CLOSE Complete Cancellation History stays distinct from Closure", async ({
  page,
}) => {
  await login(page, cancellation);
  const composition = page.getByRole("region", { name: "Composición inicial" });
  await composition
    .getByRole("button", {
      name: `Agregar ${productName} a Composición inicial`,
    })
    .click();
  await composition
    .getByLabel("Contexto para Primera Confirmacion")
    .selectOption({ label: "Contexto base E2E" });
  const confirmation = page.waitForResponse(
    (response) =>
      new URL(response.url()).pathname ===
        "/api/order-operations/first-confirmations" &&
      response.request().method() === "POST",
  );
  await composition.getByRole("button", { name: "Crear Pedido" }).click();
  const confirmed = await confirmation;
  expect(confirmed.status()).toBe(201);
  const { operationalReference: reference } = (await confirmed.json()) as {
    operationalReference: string;
  };
  const cancellationPanel = page.getByRole("region", {
    name: "Cancelación completa excepcional",
  });
  await cancellationPanel
    .getByRole("button", { name: "Cancelar pedido completo", exact: true })
    .click();
  const confirmationPanel = cancellationPanel.getByRole("region", {
    name: "Confirmar cancelación completa",
  });
  const cancelled = page.waitForResponse(
    (response) =>
      new URL(response.url()).pathname ===
        `/api/orders/${reference}/complete-cancellation` &&
      response.request().method() === "POST",
  );
  await confirmationPanel
    .getByRole("button", {
      name: "Confirmar cancelación completa",
      exact: true,
    })
    .click();
  expect((await cancelled).ok()).toBeTruthy();

  const historyInput = page.getByLabel("Referencia operacional exacta");
  await historyInput.fill(reference);
  const responsePromise = page.waitForResponse(
    (response) =>
      new URL(response.url()).pathname ===
        `/api/order-operations/order-history/${reference}` &&
      response.request().method() === "GET",
  );
  await page.getByRole("button", { name: "Ver historial" }).click();
  const response = await responsePromise;
  expect(response.ok()).toBeTruthy();
  const history = (await response.json()) as {
    termination: { type: string };
    liquidation: unknown;
    closure: unknown;
  };
  expect(history.termination.type).toBe("CompleteCancellation");
  expect(history.liquidation).toBeNull();
  expect(history.closure).toBeNull();
  const result = page.getByRole("region", { name: "Historial del pedido" });
  await expect(
    result.getByText("Cancelación completa", { exact: true }).first(),
  ).toBeVisible();
  await expect(
    result.getByRole("heading", { name: "Liquidación" }),
  ).toHaveCount(0);
  await expect(result.getByRole("heading", { name: "Cierre" })).toHaveCount(0);
  await expect(result.getByRole("button")).toHaveCount(0);
  await expect(result.getByRole("textbox")).toHaveCount(0);
  await expect(result.getByRole("combobox")).toHaveCount(0);
});
