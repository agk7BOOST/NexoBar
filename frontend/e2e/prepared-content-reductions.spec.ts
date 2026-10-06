import { randomUUID } from "node:crypto";
import { expect, test, type Page } from "@playwright/test";
import { selectInitialContext } from "./helpers/select-initial-context.js";
import { selectPreparationDestination } from "./helpers/select-preparation-destination.js";

async function login(page: Page, preparation = false) {
  await page.goto("/");
  await page
    .getByLabel("Usuario de acceso")
    .fill(preparation ? "preparador-e2e" : "delivery-e2e");
  await page
    .getByLabel("Contraseña")
    .fill(preparation ? "preparation-e2e-secret" : "delivery-e2e-secret");
  await page.getByRole("button", { name: "Ingresar" }).click();
  await expect(
    page
      .getByRole("region", { name: "Usuario actual" })
      .getByText(preparation ? "Preparador E2E" : "Delivery E2E", {
        exact: true,
      }),
  ).toBeVisible();
}

test("OABC sin Preparation corrige y cancela Pending preparado, entrega y cierra", async ({
  page,
  browser,
}, testInfo) => {
  test.setTimeout(60_000);
  const preparationReads: string[] = [];
  page.on("request", (request) => {
    const path = new URL(request.url()).pathname;
    if (
      path === "/api/identity-sessions/current/preparation-destinations" ||
      path.startsWith("/api/order-operations/preparation/")
    )
      preparationReads.push(request.url());
  });
  await login(page);
  expect(
    (
      await page.request.get(
        "/api/identity-sessions/current/preparation-destinations",
      )
    ).status(),
  ).toBe(403);
  const product = "Papas E2E autorizadas";
  const instruction = `Prueba de cantidades pendientes ${randomUUID().slice(0, 8)}`;
  const composition = page.getByRole("region", { name: "Preparar pedido" });
  for (let quantity = 0; quantity < 4; quantity += 1) {
    await composition
      .getByRole("button", { name: `Agregar ${product} a Preparar pedido` })
      .click();
  }
  await composition
    .getByLabel(`Instrucción para ${product}, línea 1`, { exact: true })
    .fill(instruction);
  await selectInitialContext(composition, "Contexto base E2E");
  const confirmationRequest = page.waitForResponse(
    (response) =>
      response.url().endsWith("/api/order-operations/first-confirmations") &&
      response.request().method() === "POST",
  );
  await composition.getByRole("button", { name: "Crear Pedido" }).click();
  const confirmation = await confirmationRequest;
  expect(confirmation.status()).toBe(201);
  const reference: string = (await confirmation.json()).operationalReference;
  await page
    .getByRole("button", { name: "Abrir entrega de este pedido" })
    .click();
  const delivery = page.getByRole("region", {
    name: `Entrega del Pedido ${reference}`,
  });
  const content = delivery.getByRole("article", {
    name: `${product}, ${instruction}, incorporación 1`,
  });
  await expect(
    content.getByRole("button", {
      name: "Corregir cantidad confirmada",
      exact: true,
    }),
  ).toBeEnabled();
  await expect(
    content.getByRole("button", {
      name: "Cancelar cantidad pendiente",
      exact: true,
    }),
  ).toBeEnabled();
  await expect(content).not.toContainText("No se pudo consultar");
  await content
    .getByText("Ver cantidades confirmadas y ajustes", { exact: true })
    .click();
  await content
    .getByRole("button", { name: "Corregir cantidad confirmada", exact: true })
    .click();
  await content.getByLabel(/^Cantidad confirmada a corregir/).fill("1");
  await expect(
    content.getByLabel(/^Cantidad confirmada a corregir/),
  ).toHaveAttribute("max", "4");
  await delivery.screenshot({
    path: testInfo.outputPath("correccion-pendiente.png"),
  });
  await content
    .getByRole("button", {
      name: "Confirmar corrección de cantidad confirmada",
    })
    .click();
  await expect(content).toContainText("R · Retirada por corrección1");
  await content
    .getByRole("button", { name: "Cancelar cantidad pendiente", exact: true })
    .click();
  await content.getByLabel(/^Cantidad a cancelar/).fill("1");
  await expect(content.getByLabel(/^Cantidad a cancelar/)).toHaveAttribute(
    "max",
    "3",
  );
  await delivery.screenshot({
    path: testInfo.outputPath("cancelacion-pendiente.png"),
  });
  await content
    .getByRole("button", {
      name: "Confirmar cancelación de cantidad pendiente",
    })
    .click();
  await expect(content).toContainText("Q · Cantidad confirmada original4");
  await expect(content).toContainText("C · Cantidad cancelada1");
  await expect(content).toContainText("F · Obligación vigente2");

  const kitchenContext = await browser.newContext();
  try {
    const kitchen = await kitchenContext.newPage();
    await login(kitchen, true);
    const preparation = kitchen.getByRole("region", { name: "Preparación" });
    await selectPreparationDestination(preparation, "Cocina E2E");
    const work = preparation.getByRole("row").filter({
      has: kitchen.getByRole("cell", { name: instruction, exact: true }),
    });
    await expect(work).toContainText("Pendiente2");
    await work.getByLabel(/^Cantidad a iniciar/).fill("1");
    await work.getByRole("button", { name: /^Iniciar / }).click();
    await expect(work).toContainText("En preparación1");
    await work.getByRole("button", { name: /^Marcar listo / }).click();
    await expect(work).toContainText("Listo1");
    await page
      .getByRole("region", { name: "Entrega", exact: true })
      .getByRole("button", { name: "Actualizar", exact: true })
      .click();
    await expect(content).toContainText("Máximo cancelable actualmente: 1");
    await content
      .getByRole("button", { name: "Cancelar cantidad pendiente", exact: true })
      .click();
    await content.getByLabel(/^Cantidad a cancelar/).fill("1");
    await content
      .getByRole("button", {
        name: "Confirmar cancelación de cantidad pendiente",
      })
      .click();
    await expect(content).toContainText("C · Cantidad cancelada2");
    await expect(content).toContainText("F · Obligación vigente1");
    await expect(
      content.getByRole("button", {
        name: "Corregir cantidad confirmada",
        exact: true,
      }),
    ).toHaveCount(0);
    await expect(
      content.getByRole("button", {
        name: "Cancelar cantidad pendiente",
        exact: true,
      }),
    ).toHaveCount(0);
    await expect(work).toContainText("Pendiente0");
    await expect(work).toContainText("Listo1");
    await content.getByRole("button", { name: /^Entregar / }).click();
    await expect(content).toContainText("Entregado1");
    const ending = page.getByRole("region", { name: "Liquidación y Cierre" });
    await expect(
      ending.getByText("Importe de lo entregado").locator("..").locator("dd"),
    ).toHaveText("7");
    await ending.getByLabel("Medio de pago").fill("Efectivo E2E");
    await ending.getByRole("button", { name: "Liquidar", exact: true }).click();
    await expect(
      ending.getByRole("button", { name: "Cerrar pedido" }),
    ).toBeEnabled();
    await ending.getByRole("button", { name: "Cerrar pedido" }).click();
    await expect(
      page
        .getByRole("status")
        .filter({ hasText: `Pedido cerrado: ${reference}` }),
    ).toBeVisible();
    expect(preparationReads).toEqual([]);
  } finally {
    await kitchenContext.close();
  }
});
