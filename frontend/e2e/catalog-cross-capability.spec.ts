import {
  expect,
  test,
  type Browser,
  type BrowserContext,
  type Page,
  type Response,
} from "@playwright/test";

const availableProduct = "Producto disponible S9 E2E";
const unavailableProduct = "Producto no disponible S9 E2E";

type Actor = {
  identifier: string;
  secret: string;
  operationalName: string;
  responsibilities: string[];
};

const catalogAdministrator: Actor = {
  identifier: "price-catalog-e2e",
  secret: "price-catalog-e2e-secret",
  operationalName: "Cat\u00e1logo precios E2E",
  responsibilities: ["CatalogConfiguration"],
};

const ordinaryOperator: Actor = {
  identifier: "delivery-e2e",
  secret: "delivery-e2e-secret",
  operationalName: "Delivery E2E",
  responsibilities: ["OrderOperationsAndBasicClosure"],
};

const interventionOperator: Actor = {
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

function requestsTo(page: Page, path: string): () => number {
  const requests: string[] = [];
  page.on("request", (request) => requests.push(pathOf(request.url())));
  return () => requests.filter((requestPath) => requestPath === path).length;
}

function responsesTo(page: Page, path: string): Response[] {
  const responses: Response[] = [];
  page.on("response", (response) => {
    if (pathOf(response.url()) === path) responses.push(response);
  });
  return responses;
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
      .getByRole("region", { name: "Identity actual" })
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

test("S9 Catalog separates administration, ordinary composition, and unavailable Product visibility", async ({
  browser,
}) => {
  const adminContext = await newContext(browser);
  const ordinaryContext = await newContext(browser);
  const interventionContext = await newContext(browser);

  try {
    const adminPage = await adminContext.newPage();
    const adminRequests = requestsTo(adminPage, "/api/catalog/products");
    const adminOperationalRequests = requestsTo(
      adminPage,
      "/api/catalog/operational-products",
    );
    await authenticateThroughCurrent(adminPage, catalogAdministrator);
    const adminCatalog = adminPage.getByRole("region", {
      name: "Productos vigentes",
    });
    await expect(adminCatalog).toBeVisible();
    await expect(
      adminPage.getByRole("region", { name: "Composici\u00f3n inicial" }),
    ).toHaveCount(0);
    const adminProduct = adminCatalog.getByRole("row").filter({
      has: adminPage.getByRole("cell", { name: availableProduct, exact: true }),
    });
    await adminProduct
      .getByRole("button", { name: `Cambiar precio de ${availableProduct}` })
      .click();
    const priceChange = adminPage.getByRole("form", {
      name: `Cambiar precio de ${availableProduct}`,
    });
    await priceChange.getByLabel("Nuevo precio").fill("12");
    await priceChange
      .getByRole("button", { name: "Confirmar cambio de Precio" })
      .click();
    await expect(
      adminProduct.getByRole("cell", { name: "12", exact: true }),
    ).toBeVisible();
    expect(adminRequests()).toBeGreaterThan(0);
    expect(adminOperationalRequests()).toBe(0);

    const ordinaryPage = await ordinaryContext.newPage();
    const ordinaryAdminRequests = requestsTo(
      ordinaryPage,
      "/api/catalog/products",
    );
    const ordinaryOperationalRequests = requestsTo(
      ordinaryPage,
      "/api/catalog/operational-products",
    );
    await authenticateThroughCurrent(ordinaryPage, ordinaryOperator);
    const ordinaryComposition = ordinaryPage.getByRole("region", {
      name: "Composici\u00f3n inicial",
    });
    await expect(ordinaryComposition).toBeVisible();
    await expect(
      ordinaryPage.getByRole("region", { name: "Productos vigentes" }),
    ).toHaveCount(0);
    await expect(
      ordinaryComposition.getByText(availableProduct, { exact: true }),
    ).toBeVisible();
    await expect(
      ordinaryComposition.getByText(unavailableProduct, { exact: true }),
    ).toHaveCount(0);
    await ordinaryComposition
      .getByRole("button", {
        name: `Agregar ${availableProduct} a Composici\u00f3n inicial`,
      })
      .click();
    await expect(
      ordinaryComposition.getByRole("cell", {
        name: `Cantidad de ${availableProduct}`,
        exact: true,
      }),
    ).toHaveText("1");
    expect(ordinaryOperationalRequests()).toBeGreaterThan(0);
    expect(ordinaryAdminRequests()).toBe(0);

    const interventionPage = await interventionContext.newPage();
    const interventionAdminRequests = requestsTo(
      interventionPage,
      "/api/catalog/products",
    );
    const interventionOperationalResponses = responsesTo(
      interventionPage,
      "/api/catalog/operational-products",
    );
    await authenticateThroughCurrent(interventionPage, interventionOperator);
    const interventionComposition = interventionPage.getByRole("region", {
      name: "Composici\u00f3n inicial",
    });
    await expect(interventionComposition).toBeVisible();
    await expect(
      interventionPage.getByRole("region", { name: "Productos vigentes" }),
    ).toHaveCount(0);
    await expect(
      interventionComposition.getByText(availableProduct, { exact: true }),
    ).toBeVisible();
    const unavailableRow = interventionComposition.getByRole("row").filter({
      has: interventionPage.getByRole("cell", {
        name: unavailableProduct,
        exact: true,
      }),
    });
    await expect(unavailableRow).toContainText("No disponible");
    await expect(
      unavailableRow.getByRole("button", {
        name: `Agregar ${unavailableProduct} a Composici\u00f3n inicial`,
      }),
    ).toBeDisabled();
    await expect(
      interventionComposition.getByRole("button", {
        name: "Confirmar Primera Composici\u00f3n",
      }),
    ).toBeDisabled();
    const operationalResponse = interventionOperationalResponses.at(-1);
    expect(operationalResponse).toBeDefined();
    expect(await operationalResponse!.json()).toEqual(
      expect.arrayContaining([
        expect.objectContaining({
          operationalName: unavailableProduct,
          isAvailable: false,
        }),
      ]),
    );
    expect(interventionAdminRequests()).toBe(0);
  } finally {
    await Promise.all([
      adminContext.close(),
      ordinaryContext.close(),
      interventionContext.close(),
    ]);
  }
});
