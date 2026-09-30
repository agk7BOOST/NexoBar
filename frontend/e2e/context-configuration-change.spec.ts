import { randomUUID } from "node:crypto";
import { expect, test, type Page, type Response } from "@playwright/test";
import { selectPreparationDestination } from "./helpers/select-preparation-destination.js";

type Actor = {
  login: string;
  secret: string;
  name: string;
  responsibilities: string[];
};
const actorG: Actor = {
  login: "general-configuration-admin-a-e2e",
  secret: "general-configuration-admin-a-e2e-secret",
  name: "Administradora general A E2E",
  responsibilities: ["GeneralConfiguration"],
};
const actorO: Actor = {
  login: "delivery-e2e",
  secret: "delivery-e2e-secret",
  name: "Delivery E2E",
  responsibilities: ["OrderOperationsAndBasicClosure"],
};
const actorP: Actor = {
  login: "preparador-e2e",
  secret: "preparation-e2e-secret",
  name: "Preparador E2E",
  responsibilities: ["Preparation"],
};
const preparedProduct = "Papas E2E autorizadas";
const directProduct = "Bebida E2E directa";

async function login(page: Page, actor: Actor) {
  await page.goto("/");
  await page.getByLabel("Identificador de acceso").fill(actor.login);
  await page.getByLabel("Secreto").fill(actor.secret);
  const loginResponse = page.waitForResponse(
    (r) =>
      new URL(r.url()).pathname === "/api/identity-sessions" &&
      r.request().method() === "POST",
  );
  await page.getByRole("button", { name: "Ingresar" }).click();
  await expect(
    page
      .getByRole("region", { name: "Usuario actual" })
      .getByText(actor.name, { exact: true }),
  ).toBeVisible();
  const response = await loginResponse;
  expect(
    [
      ...((await response.json()) as { responsibilities: string[] })
        .responsibilities,
    ].sort(),
  ).toEqual([...actor.responsibilities].sort());
}

async function logout(page: Page) {
  await page.getByRole("button", { name: "Cambiar persona / salir" }).click();
  await expect(page.getByRole("heading", { name: "Ingresar" })).toBeVisible();
}

async function waitPost(page: Page, path: RegExp): Promise<Response> {
  return page.waitForResponse(
    (r) =>
      r.request().method() === "POST" && path.test(new URL(r.url()).pathname),
  );
}

test("MVP-FC-CTX-I3 Context configuration, same-Order change, Preparation freshness and Freeze frontier", async ({
  browser,
}) => {
  const gContext = await browser.newContext();
  const oContext = await browser.newContext();
  const pContext = await browser.newContext();
  const contextA = `CTX A E2E ${randomUUID().slice(0, 8)}`;
  const contextB = `CTX B E2E ${randomUUID().slice(0, 8)}`;

  try {
    const g = await gContext.newPage();
    await login(g, actorG);
    const admin = g.getByRole("region", { name: "Configuración general" });
    for (const name of [contextA, contextB]) {
      await admin.getByLabel("Nombre operacional del Contexto").fill(name);
      const createResponse = waitPost(
        g,
        /\/api\/operational-configuration\/contexts$/,
      );
      await admin.getByRole("button", { name: "Crear Contexto" }).click();
      const response = await createResponse;
      expect(response.status()).toBe(201);
      const created = (await response.json()) as {
        id: string;
        operationalName: string;
      };
      expect(created.operationalName).toBe(name);
      await expect(
        admin.getByRole("list", { name: "Contextos configurados" }),
      ).toContainText(name);
      await expect(
        admin.getByText("Contexto creado correctamente."),
      ).toBeVisible();
      await expect(admin.getByText(created.id, { exact: true })).toHaveCount(0);
    }
    await expect(
      admin.getByRole("list", { name: "Contextos configurados" }),
    ).toContainText(contextA);
    await expect(
      admin.getByRole("list", { name: "Contextos configurados" }),
    ).toContainText(contextB);
    for (const name of [contextA, contextB]) {
      const controls = admin.getByRole("group", {
        name: `Administrar ${name}`,
      });
      await expect(controls.getByText("Activo", { exact: true })).toBeVisible();
      for (const action of [
        "Cambiar nombre",
        "Retirar",
        "Eliminar definitivamente",
      ]) {
        await expect(
          controls.getByRole("button", { name: action, exact: true }),
        ).toBeEnabled();
      }
    }
    await expect(
      g.getByLabel("Contexto para Primera Confirmacion"),
    ).toHaveCount(0);
    await expect(
      g.getByRole("button", { name: "Cambiar contexto" }),
    ).toHaveCount(0);
    await logout(g);

    const o = await oContext.newPage();
    await login(o, actorO);
    const composition = o.getByRole("region", { name: "Composición inicial" });
    const contextSelector = composition.getByLabel(
      "Contexto para Primera Confirmacion",
    );
    await expect(
      contextSelector.getByRole("option", { name: contextA, exact: true }),
    ).toBeAttached();
    await expect(
      contextSelector.getByRole("option", { name: contextB, exact: true }),
    ).toBeAttached();
    await expect(
      composition.getByRole("textbox", { name: /contexto/i }),
    ).toHaveCount(0);
    await contextSelector.selectOption({ label: contextA });
    await composition
      .getByRole("button", {
        name: `Agregar ${preparedProduct} a Composición inicial`,
      })
      .click();
    const firstRequest = o.waitForRequest(
      (r) =>
        r.method() === "POST" &&
        new URL(r.url()).pathname ===
          "/api/order-operations/first-confirmations",
    );
    const firstResponse = waitPost(
      o,
      /\/api\/order-operations\/first-confirmations$/,
    );
    await composition.getByRole("button", { name: "Crear Pedido" }).click();
    const request = await firstRequest;
    const firstIntent = request.postDataJSON() as {
      items: Array<{ productId: string; quantity: number }>;
      contextId: string;
      context?: string;
    };
    expect(firstIntent.items).toHaveLength(1);
    expect(firstIntent.items[0]).toMatchObject({ quantity: 1 });
    expect(firstIntent).toHaveProperty("contextId");
    expect(firstIntent).not.toHaveProperty("context");
    const confirmation = await firstResponse;
    expect(confirmation.status()).toBe(201);
    const confirmed = (await confirmation.json()) as {
      operationalReference: string;
      context: string;
      contextId: string;
      firstIncorporation: {
        id: string;
        items: Array<{ appliedPrice: string }>;
      };
    };
    const originalReference = confirmed.operationalReference;
    const appliedPrice = confirmed.firstIncorporation.items[0]!.appliedPrice;
    expect(originalReference).not.toBe("");
    await expect(
      o
        .getByRole("region", { name: "Pedido activo" })
        .getByText(originalReference, { exact: true }),
    ).toBeVisible();
    await expect(o.getByLabel("Contexto actual del Pedido")).toHaveText(
      contextA,
    );
    const firstContentRow = o
      .getByRole("article", { name: "Incorporación 1" })
      .getByRole("row")
      .nth(1);
    await expect(firstContentRow.getByRole("cell").nth(0)).toContainText(
      preparedProduct,
    );
    await expect(firstContentRow.getByRole("cell").nth(1)).toHaveText("1");
    await expect(firstContentRow.getByRole("cell").nth(2)).toHaveText(
      appliedPrice,
    );
    await expect(firstContentRow.getByRole("cell").nth(3)).toHaveText(
      "Sin instrucción",
    );
    await expect(o.getByText(confirmed.contextId, { exact: true })).toHaveCount(
      0,
    );
    await expect(o.getByRole("heading", { name: "Contextos" })).toHaveCount(0);

    const p = await pContext.newPage();
    await login(p, actorP);
    const preparation = p.getByRole("region", {
      name: "Preparación",
      exact: true,
    });
    await selectPreparationDestination(preparation, "Cocina E2E");
    const work = preparation
      .getByRole("row")
      .filter({ hasText: preparedProduct })
      .filter({ hasText: originalReference });
    await expect(work).toContainText(contextA);
    await expect(work).toContainText("Pendiente1");
    await expect(work).toContainText("En preparación0");
    await expect(work).toContainText("Listo0");
    await expect(p.getByRole("heading", { name: "Contextos" })).toHaveCount(0);
    await expect(
      p.getByRole("button", { name: "Cambiar contexto" }),
    ).toHaveCount(0);

    const change = waitPost(
      o,
      /\/api\/order-operations\/orders\/[^/]+\/context-changes$/,
    );
    const targetSelector = o.getByLabel("Contexto destino");
    await expect(
      targetSelector.getByRole("option", { name: contextA, exact: true }),
    ).toHaveCount(0);
    await expect(
      targetSelector.getByRole("option", { name: contextB, exact: true }),
    ).toBeAttached();
    await targetSelector.selectOption({ label: contextB });
    await o.getByRole("button", { name: "Cambiar contexto" }).click();
    const changed = await change;
    expect(changed.ok()).toBeTruthy();
    const changeBody = changed.request().postDataJSON() as {
      expectedCurrentContextId: string;
      newContextId: string;
    };
    expect(changeBody.expectedCurrentContextId).toBe(confirmed.contextId);
    expect(changeBody.newContextId).not.toBe(confirmed.contextId);
    await expect(o.getByLabel("Contexto actual del Pedido")).toHaveText(
      contextB,
    );
    await expect(
      o
        .getByRole("region", { name: "Pedido activo" })
        .getByText(originalReference, { exact: true }),
    ).toBeVisible();
    const originalContent = o.getByRole("article", { name: "Incorporación 1" });
    await expect(originalContent.getByRole("row")).toHaveCount(2);
    const unchangedContentRow = originalContent.getByRole("row").nth(1);
    await expect(unchangedContentRow.getByRole("cell").nth(0)).toContainText(
      preparedProduct,
    );
    await expect(unchangedContentRow.getByRole("cell").nth(1)).toHaveText("1");
    await expect(unchangedContentRow.getByRole("cell").nth(2)).toHaveText(
      appliedPrice,
    );
    await expect(unchangedContentRow.getByRole("cell").nth(3)).toHaveText(
      "Sin instrucción",
    );

    // P's already-open Preparation view must converge through its existing freshness subscription.
    await expect(work).toContainText(contextB, { timeout: 15000 });
    await expect(work).toContainText("Pendiente1");
    await expect(work).toContainText("En preparación0");
    await expect(work).toContainText("Listo0");
    await expect(
      preparation.getByRole("row").filter({ hasText: originalReference }),
    ).toHaveCount(1);

    // Complete this Work and Delivery only after recording unchanged A→B freshness, to enable valid Freeze.
    await work.getByRole("button", { name: /^Iniciar / }).click();
    await expect(work).toContainText("En preparación1");
    await work.getByRole("button", { name: /^Marcar listo / }).click();
    await expect(work).toContainText("Listo1");

    // A second Order may independently use B.
    await o.getByRole("button", { name: "Iniciar nuevo Pedido" }).click();
    const secondComposition = o.getByRole("region", {
      name: "Composición inicial",
    });
    await secondComposition
      .getByLabel("Contexto para Primera Confirmacion")
      .selectOption({ label: contextB });
    await secondComposition
      .getByRole("button", {
        name: `Agregar ${directProduct} a Composición inicial`,
      })
      .click();
    const secondConfirmation = waitPost(
      o,
      /\/api\/order-operations\/first-confirmations$/,
    );
    await secondComposition
      .getByRole("button", { name: "Crear Pedido" })
      .click();
    const secondResponse = await secondConfirmation;
    expect(secondResponse.status()).toBe(201);
    const secondOrder = (await secondResponse.json()) as {
      operationalReference: string;
    };
    expect(secondOrder.operationalReference).not.toBe(originalReference);
    await expect(o.getByLabel("Contexto actual del Pedido")).toHaveText(
      contextB,
    );

    // Re-identify the original Order before fulfilling and freezing it.
    await o
      .getByRole("textbox", { name: "Referencia operacional", exact: true })
      .fill(originalReference);
    await o.getByRole("button", { name: "Buscar Pedido" }).click();
    await expect(o.getByLabel("Contexto actual del Pedido")).toHaveText(
      contextB,
    );
    await expect(
      o
        .getByRole("article", { name: "Incorporación 1" })
        .getByRole("row")
        .nth(1)
        .getByRole("cell")
        .nth(0),
    ).toContainText(preparedProduct);
    await expect(
      o.getByRole("button", { name: "Abrir entrega de este Pedido" }),
    ).toBeVisible();
    await o
      .getByRole("button", { name: "Abrir entrega de este Pedido" })
      .click();
    const delivery = o.getByRole("region", {
      name: `Entrega del Pedido ${originalReference}`,
    });
    const deliveryItem = delivery
      .getByRole("article")
      .filter({ hasText: preparedProduct });
    await expect(deliveryItem).toContainText("Disponible para entregar1");
    await deliveryItem
      .getByRole("button", { name: /^Entregar Papas E2E autorizadas/ })
      .click();
    await expect(deliveryItem).toContainText("Entregado1");

    const ending = o.getByRole("region", { name: "Liquidación y Cierre" });
    await ending
      .getByLabel("Medio de pago declarado")
      .fill("E2E Context checkpoint");
    const liquidation = waitPost(
      o,
      /\/api\/order-operations\/orders\/[^/]+\/liquidate-simple$/,
    );
    await ending.getByRole("button", { name: "Liquidar" }).click();
    expect((await liquidation).ok()).toBeTruthy();
    await expect(ending).toContainText("CongeladoSí");
    await expect(
      o.getByRole("button", { name: "Cambiar contexto" }),
    ).toHaveCount(0);

    // The second Order is still independently identifiable and shares B.
    await o
      .getByRole("textbox", { name: "Referencia operacional", exact: true })
      .fill(secondOrder.operationalReference);
    await o.getByRole("button", { name: "Buscar Pedido" }).click();
    await expect(o.getByLabel("Contexto actual del Pedido")).toHaveText(
      contextB,
    );
    await expect(
      o
        .getByRole("region", { name: "Pedido activo" })
        .getByText(secondOrder.operationalReference, { exact: true }),
    ).toBeVisible();
  } finally {
    await Promise.all([gContext.close(), oContext.close(), pContext.close()]);
  }
});
