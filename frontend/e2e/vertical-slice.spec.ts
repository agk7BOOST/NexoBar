import { randomUUID } from "node:crypto";
import { expect, test, type Locator, type Page } from "@playwright/test";

function rowForProduct(
  container: Locator,
  page: Page,
  productName: string,
): Locator {
  return container.getByRole("row").filter({
    has: page.getByRole("cell", { name: productName, exact: true }),
  });
}

function compositionRowForProduct(
  composition: Locator,
  page: Page,
  productName: string,
): Locator {
  return composition.getByRole("row").filter({
    has: page.getByRole("cell", {
      name: `Cantidad de ${productName}`,
      exact: true,
    }),
  });
}

async function readActiveOperationalReference(
  composition: Locator,
): Promise<string> {
  const label = "Referencia del Pedido activo";
  const action = "Iniciar nuevo Pedido";
  const activeOrderSummary = composition.getByRole("status").filter({
    hasText: label,
  });

  await expect(activeOrderSummary).toBeVisible();

  const summaryLines = (await activeOrderSummary.innerText())
    .split("\n")
    .map((line) => line.trim())
    .filter((line) => line.length > 0);
  const labelIndex = summaryLines.indexOf(label);
  const actionIndex = summaryLines.indexOf(action);
  const operationalReference = summaryLines
    .slice(labelIndex + 1, actionIndex === -1 ? undefined : actionIndex)
    .join("\n")
    .trim();

  expect(labelIndex).toBeGreaterThanOrEqual(0);
  expect(operationalReference).not.toBe("");

  return operationalReference;
}

async function createConfirmedOrder(page: Page): Promise<{
  productName: string;
  operationalReference: string;
}> {
  const productName = `E2E-${Date.now()}-${randomUUID().slice(0, 8)}`;

  await page.goto("/");
  await page.getByLabel("Identificador de acceso").fill("delivery-e2e");
  await page.getByLabel("Secreto").fill("delivery-e2e-secret");
  await page.getByRole("button", { name: "Ingresar" }).click();
  await expect(
    page
      .getByRole("region", { name: "Identity actual" })
      .getByText("Delivery E2E", { exact: true }),
  ).toBeVisible();
  await page.getByLabel("Nombre operacional").fill(productName);
  await page.getByLabel("Precio", { exact: true }).fill("10");
  await page.getByRole("button", { name: "Crear producto" }).click();

  const products = page.getByRole("region", { name: "Productos vigentes" });
  const productRow = rowForProduct(products, page, productName);
  await expect(productRow).toBeVisible();
  await expect(
    productRow.getByRole("cell", { name: "10", exact: true }),
  ).toBeVisible();

  const initialComposition = page.getByRole("region", {
    name: "Composición inicial",
  });
  const addButton = initialComposition.getByRole("button", {
    name: `Agregar ${productName} a Composición inicial`,
  });
  await addButton.click();
  await addButton.click();

  const compositionRow = compositionRowForProduct(
    initialComposition,
    page,
    productName,
  );
  await expect(
    compositionRow.getByRole("cell", {
      name: `Cantidad de ${productName}`,
      exact: true,
    }),
  ).toHaveText("2");
  await initialComposition.getByLabel("Contexto").fill("Mesa 7");
  await initialComposition
    .getByRole("button", { name: "Confirmar Primera Composición" })
    .click();

  const subsequentComposition = page.getByRole("region", {
    name: "Nueva Composición",
  });
  await expect(subsequentComposition).toBeVisible();

  const activeOrder = page.getByRole("region", { name: "Pedido activo" });
  await expect(activeOrder).toBeVisible();

  const incorporation1 = activeOrder.getByRole("article", {
    name: "Incorporación 1",
  });
  await expect(incorporation1).toBeVisible();

  const operationalReference = await readActiveOperationalReference(
    subsequentComposition,
  );
  await expect(
    activeOrder.getByText(operationalReference, { exact: true }),
  ).toBeVisible();
  await expect(activeOrder.getByText("Mesa 7", { exact: true })).toBeVisible();

  const firstItem = rowForProduct(incorporation1, page, productName);
  await expect(firstItem).toBeVisible();
  await expect(
    firstItem.getByRole("cell", { name: "2", exact: true }),
  ).toBeVisible();
  await expect(
    firstItem.getByRole("cell", { name: "10", exact: true }),
  ).toBeVisible();
  await expect(incorporation1.getByRole("time")).toHaveAttribute(
    "datetime",
    /\S+/,
  );

  return { productName, operationalReference };
}

async function lookupActiveOrder(page: Page, operationalReference: string) {
  await page.getByLabel("Referencia operacional").fill(operationalReference);
  await page.getByRole("button", { name: "Buscar Pedido" }).click();
  return page.getByRole("region", { name: "Pedido activo" });
}

test("conserva el Precio aplicado histórico entre Incorporaciones del mismo Pedido", async ({
  page,
}) => {
  const { productName, operationalReference } =
    await createConfirmedOrder(page);

  const products = page.getByRole("region", { name: "Productos vigentes" });
  const productRow = rowForProduct(products, page, productName);
  await productRow
    .getByRole("button", { name: `Cambiar precio de ${productName}` })
    .click();

  const priceChangeForm = page.getByRole("form", {
    name: `Cambiar precio de ${productName}`,
  });
  await priceChangeForm.getByLabel("Nuevo precio").fill("12");
  await priceChangeForm
    .getByRole("button", { name: "Confirmar cambio de Precio" })
    .click();

  await expect(
    productRow.getByRole("cell", { name: "12", exact: true }),
  ).toBeVisible();

  const subsequentComposition = page.getByRole("region", {
    name: "Nueva Composición",
  });
  await expect(subsequentComposition).toBeVisible();
  await expect(
    subsequentComposition.getByText(operationalReference, { exact: true }),
  ).toBeVisible();

  const availableProducts = subsequentComposition.getByRole("region", {
    name: "Productos para la Composición",
  });
  const availableProductRow = rowForProduct(
    availableProducts,
    page,
    productName,
  );
  await expect(
    availableProductRow.getByRole("cell", { name: "12", exact: true }),
  ).toBeVisible();
  await subsequentComposition
    .getByRole("button", {
      name: `Agregar ${productName} a Nueva Composición`,
    })
    .click();
  await expect(
    subsequentComposition.getByText(
      "Composición pendiente autoritativa activa",
      { exact: true },
    ),
  ).toBeVisible();

  const subsequentCompositionRow = compositionRowForProduct(
    subsequentComposition,
    page,
    productName,
  );
  await expect(
    subsequentCompositionRow.getByRole("cell", {
      name: `Cantidad de ${productName}`,
      exact: true,
    }),
  ).toHaveText("1");
  await expect(
    subsequentCompositionRow.getByRole("cell", {
      name: "12",
      exact: true,
    }),
  ).toBeVisible();

  await subsequentComposition
    .getByRole("button", { name: "Confirmar nueva Incorporación" })
    .click();
  await expect(
    subsequentComposition.getByText(
      "Composición pendiente autoritativa activa",
      { exact: true },
    ),
  ).toHaveCount(0);

  const activeOrder = page.getByRole("region", { name: "Pedido activo" });
  const incorporation2 = activeOrder.getByRole("article", {
    name: "Incorporación 2",
  });
  await expect(incorporation2).toBeVisible();

  const incorporation1 = activeOrder.getByRole("article", {
    name: "Incorporación 1",
  });
  const firstItem = rowForProduct(incorporation1, page, productName);
  await expect(firstItem).toBeVisible();
  await expect(
    firstItem.getByRole("cell", { name: "2", exact: true }),
  ).toBeVisible();
  await expect(
    firstItem.getByRole("cell", { name: "10", exact: true }),
  ).toBeVisible();

  const secondItem = rowForProduct(incorporation2, page, productName);
  await expect(secondItem).toBeVisible();
  await expect(
    secondItem.getByRole("cell", { name: "1", exact: true }),
  ).toBeVisible();
  await expect(
    secondItem.getByRole("cell", { name: "12", exact: true }),
  ).toBeVisible();
  await expect(
    activeOrder.getByText(operationalReference, { exact: true }),
  ).toBeVisible();
  await expect(
    subsequentComposition.getByText(operationalReference, { exact: true }),
  ).toBeVisible();
});

test("otro contexto autorizado ve el marcador remoto y no puede iniciar un segundo", async ({
  page,
}) => {
  const { productName, operationalReference } =
    await createConfirmedOrder(page);
  const composition = page.getByRole("region", { name: "Nueva Composición" });
  await composition
    .getByRole("button", {
      name: `Agregar ${productName} a Nueva Composición`,
    })
    .click();
  await expect(
    composition.getByText("Composición pendiente autoritativa activa", {
      exact: true,
    }),
  ).toBeVisible();

  const otherPage = await page.context().newPage();
  try {
    await otherPage.goto("/");
    await expect(
      otherPage
        .getByRole("region", { name: "Identity actual" })
        .getByText("Delivery E2E", { exact: true }),
    ).toBeVisible();
    await otherPage
      .getByLabel("Referencia operacional")
      .fill(operationalReference);
    await otherPage.getByRole("button", { name: "Buscar Pedido" }).click();
    const consultedOrder = otherPage.getByRole("region", {
      name: "Pedido consultado",
    });
    await expect(consultedOrder).toBeVisible();
    await consultedOrder
      .getByRole("button", { name: "Continuar este Pedido" })
      .click();

    const otherComposition = otherPage.getByRole("region", {
      name: "Nueva Composición",
    });
    await expect(
      otherComposition.getByText(
        /líneas no están disponibles en esta memoria local/,
      ),
    ).toBeVisible();
    await otherComposition
      .getByRole("button", {
        name: `Agregar ${productName} a Nueva Composición`,
      })
      .click();
    await expect(
      otherComposition.getByText(
        /Descartala explícitamente para comenzar otra/,
      ),
    ).toBeVisible();

    const antiforgery = await otherPage
      .context()
      .request.get("/api/security/antiforgery");
    expect(antiforgery.ok()).toBeTruthy();
    const { requestToken } = (await antiforgery.json()) as {
      requestToken: string;
    };
    const secondStart = await otherPage
      .context()
      .request.post(
        `/api/orders/${encodeURIComponent(operationalReference)}/pending-composition`,
        {
          headers: {
            "Idempotency-Key": randomUUID(),
            "X-NexoBar-CSRF": requestToken,
          },
        },
      );
    expect(secondStart.status()).toBe(409);
    expect((await secondStart.json()).code).toBe(
      "order.pending_composition_already_exists",
    );
  } finally {
    await otherPage.close();
  }
});

test("informa un Pedido inexistente sin conservar el resultado previo", async ({
  page,
}) => {
  const { operationalReference } = await createConfirmedOrder(page);
  const priorResult = await lookupActiveOrder(page, operationalReference);
  await expect(priorResult).toBeVisible();
  await expect(
    priorResult.getByText(operationalReference, { exact: true }),
  ).toBeVisible();

  const missingReference = "00000000-0000-0000-0000-000000000001";
  const referenceInput = page.getByLabel("Referencia operacional");
  await referenceInput.fill(missingReference);
  await page.getByRole("button", { name: "Buscar Pedido" }).click();

  await expect(
    page.getByText("No se encontró un Pedido con esa Referencia operacional.", {
      exact: true,
    }),
  ).toBeVisible();
  await expect(priorResult).toHaveCount(0);
  await expect(
    page.getByRole("region", { name: "Pedido consultado" }),
  ).toHaveCount(0);
  await expect(referenceInput).toHaveValue(missingReference);

  const subsequentComposition = page.getByRole("region", {
    name: "Nueva Composición",
  });
  await expect(
    subsequentComposition.getByText(operationalReference, { exact: true }),
  ).toBeVisible();
  await expect(
    subsequentComposition.getByText(missingReference, { exact: true }),
  ).toHaveCount(0);
});

test("Preparation y Delivery operan cantidades parciales con Identities reales", async ({
  page,
}) => {
  await page.goto("/");
  await page.getByLabel("Identificador de acceso").fill("preparador-e2e");
  await page.getByLabel("Secreto").fill("preparation-e2e-secret");
  await page.getByRole("button", { name: "Ingresar" }).click();

  const identity = page.getByRole("region", { name: "Identity actual" });
  await expect(
    identity.getByText("Preparador E2E", { exact: true }),
  ).toBeVisible();
  const preparation = page.getByRole("region", { name: "Preparación" });
  await expect(
    preparation.getByText("Cocina E2E", { exact: true }),
  ).toBeVisible();
  const preparationRow = preparation
    .getByRole("row")
    .filter({ hasText: "Papas E2E autorizadas" });
  await expect(preparationRow).toBeVisible();
  await expect(preparation.getByText("Sin sal", { exact: true })).toBeVisible();
  await expect(
    preparation.getByText("Trago E2E no autorizado", { exact: true }),
  ).toHaveCount(0);

  const startQuantity = preparation.getByLabel(
    "Cantidad a iniciar de Papas E2E autorizadas, incorporación 1, Mesa seguridad E2E, Sin sal",
  );
  await expect(startQuantity).toHaveValue("2");
  await startQuantity.fill("1");
  await preparation
    .getByRole("button", {
      name: "Iniciar Papas E2E autorizadas, incorporación 1, Mesa seguridad E2E, Sin sal",
    })
    .click();
  await expect(preparationRow).toContainText("Pendiente1");
  await expect(preparationRow).toContainText("En preparación1");
  await expect(preparationRow).toContainText("Listo0");

  await identity
    .getByRole("button", { name: "Cambiar persona / salir" })
    .click();
  await expect(page.getByRole("heading", { name: "Ingresar" })).toBeVisible();
  await expect(page.getByText("Preparador E2E", { exact: true })).toHaveCount(
    0,
  );

  await page.getByLabel("Identificador de acceso").fill("preparadora-b-e2e");
  await page.getByLabel("Secreto").fill("preparation-b-e2e-secret");
  await page.getByRole("button", { name: "Ingresar" }).click();

  const secondIdentity = page.getByRole("region", { name: "Identity actual" });
  await expect(
    secondIdentity.getByText("Preparadora E2E B", { exact: true }),
  ).toBeVisible();
  const secondPreparation = page.getByRole("region", { name: "Preparación" });
  const secondPreparationRow = secondPreparation
    .getByRole("row")
    .filter({ hasText: "Papas E2E autorizadas" });
  await expect(secondPreparationRow).toContainText("Pendiente1");
  await expect(secondPreparationRow).toContainText("En preparación1");
  const readyQuantity = secondPreparation.getByLabel(
    "Cantidad a marcar lista de Papas E2E autorizadas, incorporación 1, Mesa seguridad E2E, Sin sal",
  );
  await expect(readyQuantity).toHaveValue("1");
  await secondPreparation
    .getByRole("button", {
      name: "Marcar listo Papas E2E autorizadas, incorporación 1, Mesa seguridad E2E, Sin sal",
    })
    .click();

  await expect(secondPreparationRow).toContainText("Pendiente1");
  await expect(secondPreparationRow).toContainText("En preparación0");
  await expect(secondPreparationRow).toContainText("Listo1");
  await expect(secondPreparation.getByText(/Entreg/i)).toHaveCount(0);

  const operationalReference = (
    await secondPreparationRow.locator(".technical-reference").innerText()
  ).trim();
  expect(operationalReference).not.toBe("");

  await secondIdentity
    .getByRole("button", { name: "Cambiar persona / salir" })
    .click();
  await page.getByLabel("Identificador de acceso").fill("delivery-e2e");
  await page.getByLabel("Secreto").fill("delivery-e2e-secret");
  await page.getByRole("button", { name: "Ingresar" }).click();

  const deliveryIdentity = page.getByRole("region", {
    name: "Identity actual",
  });
  await expect(
    deliveryIdentity.getByText("Delivery E2E", { exact: true }),
  ).toBeVisible();
  await page.getByLabel("Referencia operacional").fill(operationalReference);
  await page.getByRole("button", { name: "Buscar Pedido" }).click();
  await page
    .getByRole("button", { name: "Abrir entrega de este Pedido" })
    .click();

  const delivery = page.getByRole("region", {
    name: `Entrega del Pedido ${operationalReference}`,
  });
  const preparedDelivery = delivery.getByRole("article", {
    name: "Papas E2E autorizadas, Sin sal, incorporación 1",
  });
  await expect(preparedDelivery).toContainText("Ready1");
  await expect(preparedDelivery).toContainText("Delivered0");
  await expect(preparedDelivery).toContainText("Deliverable1");
  await expect(preparedDelivery).toContainText("Remaining2");
  const preparedDeliveryQuantity = preparedDelivery.getByLabel(
    "Cantidad a entregar — Papas E2E autorizadas — Sin sal — incorporación 1",
  );
  await expect(preparedDeliveryQuantity).toHaveValue("1");
  await preparedDelivery
    .getByRole("button", {
      name: "Entregar Papas E2E autorizadas, Sin sal, incorporación 1",
    })
    .click();
  await expect(preparedDelivery).toContainText("Ready1");
  await expect(preparedDelivery).toContainText("Delivered1");
  await expect(preparedDelivery).toContainText("Deliverable0");
  await expect(preparedDelivery).toContainText("Remaining1");

  const directDelivery = delivery.getByRole("article", {
    name: "Bebida E2E directa, sin instrucción, incorporación 1",
  });
  await expect(directDelivery).toContainText("Preparación no requerida");
  await expect(directDelivery).toContainText("Deliverable2");
  const directDeliveryQuantity = directDelivery.getByLabel(
    "Cantidad a entregar — Bebida E2E directa — sin instrucción — incorporación 1",
  );
  await expect(directDeliveryQuantity).toHaveValue("2");
  await directDeliveryQuantity.fill("1");
  await directDelivery
    .getByRole("button", {
      name: "Entregar Bebida E2E directa, sin instrucción, incorporación 1",
    })
    .click();
  await expect(directDelivery).toContainText("Delivered1");
  await expect(directDelivery).toContainText("Deliverable1");
  await expect(directDelivery).toContainText("Remaining1");
});

for (const mode of ["simple", "external"] as const) {
  test(`Pedido termina por Liquidación ${mode}, Freeze y Cierre explícito`, async ({
    page,
  }) => {
    const { productName, operationalReference } =
      await createConfirmedOrder(page);
    await page
      .getByRole("button", { name: "Abrir entrega de este Pedido" })
      .click();
    const delivery = page.getByRole("region", {
      name: `Entrega del Pedido ${operationalReference}`,
    });
    const content = delivery.getByRole("article", {
      name: `${productName}, sin instrucción, incorporación 1`,
    });
    await expect(content).toContainText("Preparación no requerida");
    await content
      .getByRole("button", {
        name: `Entregar ${productName}, sin instrucción, incorporación 1`,
      })
      .click();
    await expect(content).toContainText("Delivered2");
    const ending = page.getByRole("region", { name: "Liquidación y Cierre" });
    await expect(
      ending.getByText("Importe funcional actual").locator("..").locator("dd"),
    ).toHaveText(/^20(?:\.0+)?$/);
    await expect(
      ending.getByRole("button", { name: "Liquidar", exact: true }),
    ).toBeEnabled();
    await expect(
      ending.getByRole("button", { name: "Cerrar Pedido" }),
    ).toHaveCount(0);
    await expect(
      ending.getByText("Después de Liquidar, el Pedido quedará congelado."),
    ).toBeVisible();
    if (mode === "simple") {
      await ending
        .getByLabel("Medio de pago declarado")
        .fill("  Vale del club / septiembre  ");
      await ending
        .getByRole("button", { name: "Liquidar", exact: true })
        .click();
    } else {
      await ending
        .getByRole("button", {
          name: "Registrar cobro gestionado externamente",
        })
        .click();
    }
    await expect(ending.getByText(/Pedido congelado/)).toBeVisible();
    await expect(
      ending.getByText("Importe liquidado").locator("..").locator("dd"),
    ).toHaveText(/^20(?:\.0+)?$/);
    await expect(ending.getByRole("time")).toHaveAttribute("datetime", /\S+/);
    if (mode === "simple") {
      await expect(
        ending.getByText("Vale del club / septiembre", { exact: true }),
      ).toBeVisible();
    } else {
      await expect(ending.getByText("Medio de pago declarado")).toHaveCount(0);
      await expect(
        ending.getByText("Cobro gestionado externamente", { exact: true }),
      ).toBeVisible();
    }
    await expect(
      ending.getByText("Pedido cerrado", { exact: true }),
    ).toHaveCount(0);
    const composition = page.getByRole("region", { name: "Nueva Composición" });
    await expect(
      composition.getByRole("button", {
        name: `Agregar ${productName} a Nueva Composición`,
        exact: true,
      }),
    ).toBeDisabled();
    await ending.getByRole("button", { name: "Cerrar Pedido" }).click();
    await expect(
      ending.getByText("Pedido cerrado", { exact: true }),
    ).toBeVisible();
    await expect(
      ending.getByText("Cerrado el").getByRole("time"),
    ).toHaveAttribute("datetime", /\S+/);
    await lookupActiveOrder(page, operationalReference);
    await expect(
      ending.getByText("Pedido cerrado", { exact: true }),
    ).toBeVisible();
    await expect(
      page.getByRole("article", { name: "Incorporación 1", exact: true }),
    ).toBeVisible();
    await expect(
      ending.getByRole("button", { name: /Liquidar|Cerrar Pedido|reabrir/i }),
    ).toHaveCount(0);
    await expect(
      page.getByRole("button", { name: "Continuar este Pedido" }),
    ).toHaveCount(0);
  });
}

test("Inventario ejecuta operaciones físicas e Historia con capacidades separadas", async ({
  page,
}) => {
  const itemName = `Harina E2E ${randomUUID().slice(0, 8)}`;
  await page.goto("/");
  await page.getByLabel("Identificador de acceso").fill("inventory-config-e2e");
  await page.getByLabel("Secreto").fill("inventory-config-e2e-secret");
  await page.getByRole("button", { name: "Ingresar" }).click();

  const configurationIdentity = page.getByRole("region", {
    name: "Identity actual",
  });
  await expect(
    configurationIdentity.getByText("Configurador Inventario E2E", {
      exact: true,
    }),
  ).toBeVisible();
  const configuration = page.getByRole("region", {
    name: "Configuración de Inventario",
  });
  await configuration.getByLabel("Nombre operacional").fill(itemName);
  await configuration.getByLabel("Unidad operacional").fill("kg");
  await configuration.getByRole("button", { name: "Crear elemento" }).click();
  await expect(
    configuration.getByText(itemName, { exact: true }),
  ).toBeVisible();
  await expect(
    page.getByText(
      "Esta Identity no tiene autorización para operar Inventario.",
      { exact: true },
    ),
  ).toBeVisible();

  await configurationIdentity
    .getByRole("button", { name: "Cambiar persona / salir" })
    .click();
  await page
    .getByLabel("Identificador de acceso")
    .fill("inventory-operation-e2e");
  await page.getByLabel("Secreto").fill("inventory-operation-e2e-secret");
  await page.getByRole("button", { name: "Ingresar" }).click();

  const operationIdentity = page.getByRole("region", {
    name: "Identity actual",
  });
  await expect(
    operationIdentity.getByText("Operador Inventario E2E", { exact: true }),
  ).toBeVisible();
  await expect(
    page.getByText(
      "Esta Identity no tiene autorización para configurar Inventario.",
      { exact: true },
    ),
  ).toBeVisible();

  const operation = page.getByRole("region", {
    name: "Estado actual de Inventario",
  });
  const item = operation.getByRole("article", { name: itemName });
  await expect(item).toContainText("Existencia no establecida");
  await item.getByLabel(`Cantidad observada para ${itemName}`).fill("5.5");
  await item.getByRole("button", { name: "Registrar conteo" }).click();
  await expect(item).toContainText("Conteo registrado: 5.5 kg");
  await expect(item).toContainText("El saldo no fue modificado");
  await item.getByRole("button", { name: "Reconciliar conteo" }).click();
  await expect(item).toContainText("Existencia inicial establecida en 5.5");
  await expect(item).toContainText("Existencia registrada5.5 kg");

  await item.getByLabel(`Cantidad de entrada para ${itemName}`).fill("2");
  await item.getByRole("button", { name: "Registrar entrada" }).click();
  await expect(item).toContainText("Existencia registrada7.5 kg");

  await item.getByLabel(`Cantidad de salida manual para ${itemName}`).fill("8");
  await item.getByRole("button", { name: "Registrar salida manual" }).click();
  await expect(item).toContainText("Existencia registrada-0.5 kg");
  await expect(item.getByRole("alert")).toContainText(
    "Inconsistencia de saldo",
  );

  await item.getByLabel(`Cantidad de merma para ${itemName}`).fill("0.5");
  await item.getByRole("button", { name: "Registrar merma" }).click();
  await expect(item).toContainText("Existencia registrada-1 kg");
  await item
    .getByRole("button", { name: `Ver movimientos de ${itemName}` })
    .click();

  const history = operation.getByRole("region", { name: "Movimientos" });
  await expect(history.getByRole("heading", { name: "Merma" })).toBeVisible();
  await expect(
    history.getByRole("heading", { name: "Salida manual" }),
  ).toBeVisible();
  await expect(history.getByRole("heading", { name: "Entrada" })).toBeVisible();
  await expect(
    history.getByRole("heading", { name: "Reconciliación" }),
  ).toBeVisible();
  await expect(
    history.getByText("Existencia establecida mediante conteo"),
  ).toBeVisible();
  await expect(history.getByText("Cantidad", { exact: true })).toHaveCount(3);
  await expect(history.getByText("Resultado", { exact: true })).toHaveCount(1);
  await expect(history).toContainText("Saldo resultante-1 kg");
});

test("Content Correction conserva Q, reduce F y permite cumplir y Liquidar", async ({
  page,
}) => {
  const { productName, operationalReference } =
    await createConfirmedOrder(page);
  await page
    .getByRole("button", { name: "Abrir entrega de este Pedido" })
    .click();
  const content = page
    .getByRole("region", { name: `Entrega del Pedido ${operationalReference}` })
    .getByRole("article", {
      name: `${productName}, sin instrucción, incorporación 1`,
    });
  const ending = page.getByRole("region", { name: "Liquidación y Cierre" });
  const amount = ending
    .getByText("Importe funcional actual")
    .locator("..")
    .locator("dd");
  await content
    .getByRole("button", { name: "Corregir cantidad confirmada", exact: true })
    .click();
  await content.getByLabel(/^Cantidad a retirar \(x\)/).fill("1");
  await expect(content).toContainText("Máximo corregible actualmente: 2");
  await expect(content).toContainText("F resultante (prevista): 1");
  await content
    .getByRole("button", {
      name: "Confirmar corrección de cantidad confirmada",
    })
    .click();
  await expect(content).toContainText("Q · Cantidad confirmada original2");
  await expect(content).toContainText("R · Retirada por corrección1");
  await expect(content).toContainText("F · Obligación vigente1");
  await expect(amount).toHaveText(/^0(?:\.0+)?$/);
  await content.getByRole("button", { name: /^Entregar / }).click();
  await expect(content).toContainText("Delivered1");
  await expect(amount).toHaveText(/^10(?:\.0+)?$/);
  await ending.getByLabel("Medio de pago declarado").fill("Efectivo");
  await ending.getByRole("button", { name: "Liquidar", exact: true }).click();
  await expect(ending.getByText(/Pedido congelado/)).toBeVisible();
  await expect(
    content.getByRole("button", {
      name: "Corregir cantidad confirmada",
      exact: true,
    }),
  ).toHaveCount(0);
  await expect(content).toContainText("Q · Cantidad confirmada original2");
});

test("Delivery Correction reduce importe, permite reentrega y desaparece al Liquidar", async ({
  page,
}) => {
  const { productName, operationalReference } =
    await createConfirmedOrder(page);
  await page
    .getByRole("button", { name: "Abrir entrega de este Pedido" })
    .click();
  const delivery = page.getByRole("region", {
    name: `Entrega del Pedido ${operationalReference}`,
  });
  const content = delivery.getByRole("article", {
    name: `${productName}, sin instrucción, incorporación 1`,
  });
  const ending = page.getByRole("region", { name: "Liquidación y Cierre" });
  const amount = ending
    .getByText("Importe funcional actual")
    .locator("..")
    .locator("dd");
  const deliver = content.getByRole("button", {
    name: `Entregar ${productName}, sin instrucción, incorporación 1`,
  });
  await expect(content).toContainText("Preparación no requerida");
  await deliver.click();
  await expect(content).toContainText("Delivered2");
  await expect(amount).toHaveText(/^20(?:\.0+)?$/);
  await content.getByRole("button", { name: /^Corregir entrega / }).click();
  await content.getByLabel(/^Cantidad a corregir —/).fill("1");
  await expect(content).toContainText("Actualmente entregado: 2");
  await expect(content).toContainText("Entrega resultante (prevista): 1");
  await content
    .getByRole("button", { name: "Confirmar corrección de entrega" })
    .click();
  await expect(content).toContainText("Delivered1");
  await expect(content).toContainText("Deliverable1");
  await expect(amount).toHaveText(/^10(?:\.0+)?$/);
  await expect(content.getByLabel(/^Cantidad a entregar —/)).toHaveValue("1");
  await deliver.click();
  await expect(content).toContainText("Delivered2");
  await expect(amount).toHaveText(/^20(?:\.0+)?$/);
  await ending.getByLabel("Medio de pago declarado").fill("Efectivo");
  await ending.getByRole("button", { name: "Liquidar", exact: true }).click();
  await expect(ending.getByText(/Pedido congelado/)).toBeVisible();
  await expect(
    content.getByRole("button", { name: /^Corregir entrega / }),
  ).toHaveCount(0);
});
