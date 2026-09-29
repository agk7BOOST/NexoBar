import {
  expect,
  test,
  type Browser,
  type BrowserContext,
  type Page,
} from "@playwright/test";
import { selectInitialContext } from "./helpers/select-initial-context.js";

const productName = "Producto disponible S9 E2E";

type Actor = {
  identifier: string;
  secret: string;
  operationalName: string;
  responsibilities: string[];
};

const interventionOnly: Actor = {
  identifier: "intervention-e2e",
  secret: "intervention-e2e-secret",
  operationalName: "Intervenci\u00f3n E2E",
  responsibilities: ["OperationalIntervention"],
};

const ordinaryOperator: Actor = {
  identifier: "delivery-e2e",
  secret: "delivery-e2e-secret",
  operationalName: "Delivery E2E",
  responsibilities: ["OrderOperationsAndBasicClosure"],
};

const dualOperator: Actor = {
  identifier: "complete-cancellation-e2e",
  secret: "complete-cancellation-e2e-secret",
  operationalName: "Cancelaci\u00f3n completa E2E",
  responsibilities: [
    "OrderOperationsAndBasicClosure",
    "OperationalIntervention",
  ],
};

function pathOf(url: string): string {
  return new URL(url).pathname;
}

async function authenticateThroughCurrent(
  page: Page,
  actor: Actor,
): Promise<void> {
  await page.goto("/");
  await page.getByLabel("Identificador de acceso").fill(actor.identifier);
  await page.getByLabel("Secreto").fill(actor.secret);
  await page.getByRole("button", { name: "Ingresar" }).click();
  await expect(
    page
      .getByRole("region", { name: "Usuario actual" })
      .getByText(actor.operationalName, { exact: true }),
  ).toBeVisible();

  const current = page.waitForResponse(
    (response) =>
      pathOf(response.url()) === "/api/identity-sessions/current" &&
      response.request().method() === "GET" &&
      response.ok(),
  );
  await page.reload();
  const currentIdentity = (await (await current).json()) as {
    responsibilities: string[];
  };
  expect([...currentIdentity.responsibilities].sort()).toEqual(
    [...actor.responsibilities].sort(),
  );
}

async function newContext(browser: Browser): Promise<BrowserContext> {
  return browser.newContext();
}

test("MVP-FC-AVAIL-I3 separates availability intervention from ordinary and S10 operation", async ({
  browser,
}) => {
  const interventionContext = await newContext(browser);
  const ordinaryContext = await newContext(browser);
  const dualContext = await newContext(browser);

  try {
    const interventionPage = await interventionContext.newPage();
    await authenticateThroughCurrent(interventionPage, interventionOnly);
    const availability = interventionPage.getByRole("region", {
      name: "Intervenci\u00f3n de disponibilidad de productos",
    });
    await expect(availability).toBeVisible();
    await expect(
      availability.getByText(productName, { exact: true }),
    ).toBeVisible();
    await expect(
      availability.getByLabel(`Disponibilidad de ${productName}`),
    ).toHaveText("Disponible");
    await expect(
      interventionPage.getByRole("region", { name: "Productos", exact: true }),
    ).toHaveCount(0);
    await expect(
      interventionPage.getByRole("region", {
        name: "Composici\u00f3n inicial",
      }),
    ).toHaveCount(0);

    const ordinaryPage = await ordinaryContext.newPage();
    await authenticateThroughCurrent(ordinaryPage, ordinaryOperator);
    const composition = ordinaryPage.getByRole("region", {
      name: "Composici\u00f3n inicial",
    });
    await expect(composition).toBeVisible();
    await expect(
      composition.getByText(productName, { exact: true }),
    ).toBeVisible();
    await composition
      .getByRole("button", {
        name: `Agregar ${productName} a Composici\u00f3n inicial`,
      })
      .click();
    await expect(
      composition.getByRole("cell", {
        name: `Cantidad de ${productName}`,
        exact: true,
      }),
    ).toHaveText("1");
    await selectInitialContext(composition);

    await availability
      .getByRole("button", {
        name: `Marcar no disponible ${productName}`,
      })
      .click();
    await expect(
      availability.getByLabel(`Disponibilidad de ${productName}`),
    ).toHaveText("No disponible");

    const ordinaryBrowsePage = await ordinaryContext.newPage();
    await ordinaryBrowsePage.goto("/");
    const ordinaryBrowse = ordinaryBrowsePage.getByRole("region", {
      name: "Composici\u00f3n inicial",
    });
    await expect(ordinaryBrowse).toBeVisible();
    await expect(
      ordinaryBrowse.getByText(productName, { exact: true }),
    ).toHaveCount(0);

    const rejectedConfirmation = ordinaryPage.waitForResponse(
      (response) =>
        pathOf(response.url()) ===
          "/api/order-operations/first-confirmations" &&
        response.request().method() === "POST",
    );
    await composition
      .getByRole("button", { name: "Confirmar Primera Composici\u00f3n" })
      .click();
    const rejected = await rejectedConfirmation;
    expect(rejected.status()).toBe(409);
    const rejectedBody = (await rejected.json()) as {
      code: string;
      operationalReference?: string;
    };
    expect(rejectedBody.code).toBe(
      "order_operations.first_confirmation.product_unavailable",
    );
    expect(rejectedBody.operationalReference).toBeUndefined();
    await expect(
      ordinaryPage.getByRole("region", { name: "Pedido activo" }),
    ).toHaveCount(0);
    await expect(composition).toContainText("ya no est\u00e1 disponible");

    const dualPage = await dualContext.newPage();
    await authenticateThroughCurrent(dualPage, dualOperator);
    const dualComposition = dualPage.getByRole("region", {
      name: "Composici\u00f3n inicial",
    });
    await expect(dualComposition).toBeVisible();
    const unavailableRow = dualComposition.getByRole("row").filter({
      has: dualPage.getByRole("cell", { name: productName, exact: true }),
    });
    await expect(unavailableRow).toContainText("No disponible");
    await expect(
      unavailableRow.getByRole("button", {
        name: `Agregar ${productName} a Composici\u00f3n inicial`,
      }),
    ).toBeDisabled();
    const interventionAdd = unavailableRow.getByRole("button", {
      name: `Agregar ${productName} mediante intervenci\u00f3n a Composici\u00f3n inicial`,
    });
    await expect(interventionAdd).toBeEnabled();
    await interventionAdd.click();
    await expect(
      dualComposition.getByText("Intervenci\u00f3n solicitada"),
    ).toBeVisible();
    await selectInitialContext(dualComposition);

    const confirmedResponse = dualPage.waitForResponse(
      (response) =>
        pathOf(response.url()) ===
          "/api/order-operations/first-confirmations" &&
        response.request().method() === "POST",
    );
    await dualComposition
      .getByRole("button", { name: "Confirmar con intervenci\u00f3n" })
      .click();
    const confirmed = await confirmedResponse;
    expect(confirmed.ok()).toBeTruthy();
    const confirmation = (await confirmed.json()) as {
      operationalReference: string;
      firstIncorporation: {
        id?: string;
        items: {
          productId: string;
          unavailableProductExceptionApplied: boolean;
        }[];
      };
    };
    const committedItem = confirmation.firstIncorporation.items.find(
      (item) => item.productId !== undefined,
    );
    expect(committedItem).toMatchObject({
      unavailableProductExceptionApplied: true,
    });
    const committedProductId = committedItem!.productId;

    const orderResponse = await dualContext.request.get(
      `/api/order-operations/orders/${encodeURIComponent(confirmation.operationalReference)}`,
    );
    expect(orderResponse.ok()).toBeTruthy();
    const order = (await orderResponse.json()) as {
      incorporations: {
        contents?: {
          productId: string;
          unavailableProductExceptionApplied: boolean;
        }[];
        items?: {
          productId: string;
          unavailableProductExceptionApplied: boolean;
        }[];
      }[];
    };
    const historicalItems = order.incorporations.flatMap(
      (incorporation) => incorporation.items ?? incorporation.contents ?? [],
    );
    expect(historicalItems).toEqual(
      expect.arrayContaining([
        expect.objectContaining({
          productId: committedProductId,
          unavailableProductExceptionApplied: true,
        }),
      ]),
    );

    await interventionPage.reload();
    const reloadedAvailability = interventionPage.getByRole("region", {
      name: "Intervenci\u00f3n de disponibilidad de productos",
    });
    await expect(
      reloadedAvailability.getByLabel(`Disponibilidad de ${productName}`),
    ).toHaveText("No disponible");
    await reloadedAvailability
      .getByRole("button", { name: `Marcar disponible ${productName}` })
      .click();
    await expect(
      reloadedAvailability.getByLabel(`Disponibilidad de ${productName}`),
    ).toHaveText("Disponible");

    await ordinaryPage.reload();
    const ordinaryCompositionAfter = ordinaryPage.getByRole("region", {
      name: "Composici\u00f3n inicial",
    });
    await expect(ordinaryCompositionAfter).toBeVisible();
    await expect(
      ordinaryCompositionAfter.getByText(productName, { exact: true }),
    ).toBeVisible();

    const historicalAfterAvailability = await dualContext.request.get(
      `/api/order-operations/orders/${encodeURIComponent(confirmation.operationalReference)}`,
    );
    expect(historicalAfterAvailability.ok()).toBeTruthy();
    const orderAfterAvailability =
      (await historicalAfterAvailability.json()) as typeof order;
    const historicalItemsAfterAvailability =
      orderAfterAvailability.incorporations.flatMap(
        (incorporation) => incorporation.items ?? incorporation.contents ?? [],
      );
    expect(historicalItemsAfterAvailability).toEqual(
      expect.arrayContaining([
        expect.objectContaining({
          productId: committedProductId,
          unavailableProductExceptionApplied: true,
        }),
      ]),
    );
  } finally {
    await Promise.all([
      interventionContext.close(),
      ordinaryContext.close(),
      dualContext.close(),
    ]);
  }
});
