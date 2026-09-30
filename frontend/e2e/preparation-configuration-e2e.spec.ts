import { randomUUID } from "node:crypto";
import { selectInitialContext } from "./helpers/select-initial-context.js";
import {
  expect,
  test,
  type Locator,
  type Page,
  type Response,
} from "@playwright/test";

const configuredProductName = "Producto disponible S9 E2E";

type Actor = {
  identifier: string;
  secret: string;
  operationalName: string;
  responsibilities: string[];
};

const generalConfigurationActor: Actor = {
  identifier: "general-configuration-admin-a-e2e",
  secret: "general-configuration-admin-a-e2e-secret",
  operationalName: "Administradora general A E2E",
  responsibilities: ["GeneralConfiguration"],
};

const catalogConfigurationActor: Actor = {
  identifier: "price-catalog-e2e",
  secret: "price-catalog-e2e-secret",
  operationalName: "Catálogo precios E2E",
  responsibilities: ["CatalogConfiguration"],
};

const preparationActor: Actor = {
  identifier: "preparador-e2e",
  secret: "preparation-e2e-secret",
  operationalName: "Preparador E2E",
  responsibilities: ["Preparation"],
};

const orderActor: Actor = {
  identifier: "delivery-e2e",
  secret: "delivery-e2e-secret",
  operationalName: "Delivery E2E",
  responsibilities: ["OrderOperationsAndBasicClosure"],
};

function pathOf(url: string): string {
  return new URL(url).pathname;
}

function waitForResponse(
  page: Page,
  method: string,
  path: string | RegExp,
): Promise<Response> {
  return page.waitForResponse(
    (response) =>
      (typeof path === "string"
        ? pathOf(response.url()) === path
        : path.test(pathOf(response.url()))) &&
      response.request().method() === method,
  );
}

async function authenticateThroughCurrent(
  page: Page,
  actor: Actor,
): Promise<void> {
  await page.goto("/");
  await page.getByLabel("Usuario de acceso").fill(actor.identifier);
  await page.getByLabel("Contraseña").fill(actor.secret);
  await page.getByRole("button", { name: "Ingresar" }).click();
  await expect(
    page
      .getByRole("region", { name: "Usuario actual" })
      .getByText(actor.operationalName, { exact: true }),
  ).toBeVisible();

  const current = waitForResponse(
    page,
    "GET",
    "/api/identity-sessions/current",
  );
  await page.reload();
  const currentIdentity = (await (await current).json()) as {
    responsibilities: string[];
  };
  expect([...currentIdentity.responsibilities].sort()).toEqual(
    [...actor.responsibilities].sort(),
  );
}

function generalConfiguration(page: Page): Locator {
  return page
    .getByRole("heading", { name: "Configuración general", exact: true })
    .locator("..");
}

function identityRow(
  configuration: Locator,
  _page: Page,
  operationalName: string,
): Locator {
  return configuration.getByRole("region", {
    name: `Administrar identidad ${operationalName}`,
  });
}

function productRow(
  products: Locator,
  page: Page,
  operationalName: string,
): Locator {
  return products.getByRole("row").filter({
    has: page.getByRole("cell", { name: operationalName, exact: true }),
  });
}

test("MVP-FC-PREP-I2 compone configuración de Preparation con operadores separados", async ({
  browser,
}) => {
  const generalContext = await browser.newContext();
  const catalogContext = await browser.newContext();
  const orderContext = await browser.newContext();
  const preparationContext = await browser.newContext();
  const preparationResponsibilityName = `Cocina configuración E2E ${Date.now()}-${randomUUID().slice(0, 8)}`;
  const orderContextName = "Contexto base E2E";

  try {
    const generalPage = await generalContext.newPage();
    await authenticateThroughCurrent(generalPage, generalConfigurationActor);
    const configuration = generalConfiguration(generalPage);
    const responsibilityCreation = waitForResponse(
      generalPage,
      "POST",
      "/api/operational-configuration/preparation-responsibilities",
    );
    await configuration
      .getByLabel("Nombre del destino de preparación")
      .fill(preparationResponsibilityName);
    await configuration
      .getByRole("button", { name: "Crear destino de preparación" })
      .click();
    const createdResponsibilityResponse = await responsibilityCreation;
    expect(createdResponsibilityResponse.ok()).toBeTruthy();
    const createdResponsibility =
      (await createdResponsibilityResponse.json()) as {
        id: string;
        operationalName: string;
      };
    expect(createdResponsibility.operationalName).toBe(
      preparationResponsibilityName,
    );
    await expect(
      configuration.getByText(/Se creó el destino/, {
        exact: true,
      }),
    ).toBeVisible();
    await expect(
      configuration.getByRole("list", {
        name: "Listado de destinos de preparación",
      }),
    ).toContainText(preparationResponsibilityName);

    await configuration
      .getByRole("button", {
        name: `Administrar ${preparationActor.operationalName}`,
      })
      .click();
    const preparationOperator = identityRow(
      configuration,
      generalPage,
      preparationActor.operationalName,
    );
    await expect(preparationOperator).toBeVisible();
    await expect(
      preparationOperator.getByRole("button", {
        name: `Habilitar destino ${preparationResponsibilityName} a ${preparationActor.operationalName}`,
      }),
    ).toBeVisible();
    const enablementGrant = waitForResponse(
      generalPage,
      "POST",
      /\/api\/identities\/[^/]+\/preparation-enablement\/[^/]+\/grant/,
    );
    await preparationOperator
      .getByRole("button", {
        name: `Habilitar destino ${preparationResponsibilityName} a ${preparationActor.operationalName}`,
      })
      .click();
    const enablementResponse = await enablementGrant;
    expect(enablementResponse.ok()).toBeTruthy();
    const enablementState = (await enablementResponse.json()) as {
      preparationEnablements: string[];
    };
    expect(enablementState.preparationEnablements).toContain(
      createdResponsibility.id,
    );
    await expect(
      preparationOperator.getByRole("list", {
        name: `Destinos habilitados de ${preparationActor.operationalName}`,
      }),
    ).toContainText(`${preparationResponsibilityName}: Habilitada`);

    const catalogPage = await catalogContext.newPage();
    await authenticateThroughCurrent(catalogPage, catalogConfigurationActor);
    const catalog = catalogPage.getByRole("region", {
      name: "Productos",
      exact: true,
    });
    await expect(catalog).toBeVisible();
    const initialProducts = waitForResponse(
      catalogPage,
      "GET",
      "/api/catalog/products",
    );
    await catalog.getByRole("button", { name: "Actualizar" }).click();
    const initialProduct = (
      (await (await initialProducts).json()) as Array<{
        id: string;
        operationalName: string;
        requiresPreparation: boolean;
        preparationResponsibilityId: string | null;
      }>
    ).find((product) => product.operationalName === configuredProductName);
    expect(initialProduct).toMatchObject({
      requiresPreparation: false,
      preparationResponsibilityId: null,
    });
    const configuredProduct = productRow(
      catalog,
      catalogPage,
      configuredProductName,
    );
    await expect(configuredProduct).toContainText("No requiere preparación");
    await configuredProduct
      .getByRole("button", {
        name: `Configurar preparación de ${configuredProductName}`,
      })
      .click();
    const preparationConfiguration = catalogPage.getByRole("form", {
      name: `Configurar preparación de ${configuredProductName}`,
    });
    await preparationConfiguration.getByLabel("Requiere preparación").check();
    await preparationConfiguration
      .getByLabel("Destino de preparación")
      .selectOption({ label: preparationResponsibilityName });
    const configurationChange = waitForResponse(
      catalogPage,
      "POST",
      /\/api\/catalog\/products\/[^/]+\/preparation-configuration-changes/,
    );
    const configuredProductsReload = waitForResponse(
      catalogPage,
      "GET",
      "/api/catalog/products",
    );
    await preparationConfiguration
      .getByRole("button", { name: "Confirmar configuración de preparación" })
      .click();
    expect((await configurationChange).ok()).toBeTruthy();
    const configuredProductState = (
      (await (await configuredProductsReload).json()) as Array<{
        id: string;
        operationalName: string;
        requiresPreparation: boolean;
        preparationResponsibilityId: string | null;
      }>
    ).find((product) => product.operationalName === configuredProductName);
    expect(configuredProductState).toMatchObject({
      requiresPreparation: true,
      preparationResponsibilityId: createdResponsibility.id,
    });
    await expect(configuredProduct).toContainText(
      `Requiere preparación: ${preparationResponsibilityName}`,
    );

    const orderPage = await orderContext.newPage();
    await authenticateThroughCurrent(orderPage, orderActor);
    const composition = orderPage.getByRole("region", {
      name: "Preparar pedido",
    });
    await expect(composition).toBeVisible();
    await composition
      .getByRole("button", {
        name: `Agregar ${configuredProductName} a Preparar pedido`,
      })
      .click();
    await selectInitialContext(composition, orderContextName);
    const firstConfirmation = waitForResponse(
      orderPage,
      "POST",
      "/api/order-operations/first-confirmations",
    );
    await composition.getByRole("button", { name: "Crear Pedido" }).click();
    const confirmationResponse = await firstConfirmation;
    expect(confirmationResponse.ok()).toBeTruthy();
    const confirmedOrder = (await confirmationResponse.json()) as {
      operationalReference: string;
      firstIncorporation: { id: string };
    };
    expect(confirmedOrder.operationalReference).not.toBe("");
    expect(confirmedOrder.firstIncorporation.id).not.toBe("");
    await expect(
      orderPage.getByRole("region", { name: "Pedido activo" }),
    ).toContainText(confirmedOrder.operationalReference);

    const preparationPage = await preparationContext.newPage();
    await authenticateThroughCurrent(preparationPage, preparationActor);
    const preparation = preparationPage.getByRole("region", {
      name: "Preparación",
      exact: true,
    });
    const destination = preparation.getByLabel("Destino de preparación");
    await expect(
      destination.getByRole("option", {
        name: preparationResponsibilityName,
        exact: true,
      }),
    ).toBeAttached();
    await destination.selectOption({ label: preparationResponsibilityName });
    await expect(destination).toHaveValue(createdResponsibility.id);
    const work = preparation
      .getByRole("row")
      .filter({ hasText: configuredProductName })
      .filter({ hasText: orderContextName });
    await expect(work).toBeVisible();
    await expect(work).toContainText("Pendiente1");
    await expect(work).toContainText(confirmedOrder.operationalReference);
  } finally {
    await Promise.all([
      generalContext.close(),
      catalogContext.close(),
      orderContext.close(),
      preparationContext.close(),
    ]);
  }
});
