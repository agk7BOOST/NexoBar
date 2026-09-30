import { randomUUID } from "node:crypto";
import { expect, test, type Page } from "@playwright/test";

const accounts = {
  general: [
    "general-configuration-admin-a-e2e",
    "general-configuration-admin-a-e2e-secret",
  ],
  order: ["delivery-e2e", "delivery-e2e-secret"],
  inventory: [
    "inventory-operation-sse-a-e2e",
    "inventory-operation-sse-a-e2e-secret",
  ],
};
async function login(page: Page, account: keyof typeof accounts) {
  await page.goto("/");
  await page
    .getByLabel("Usuario de acceso", { exact: true })
    .fill(accounts[account][0]);
  await page
    .getByLabel("Contraseña", { exact: true })
    .fill(accounts[account][1]);
  await page.getByLabel("Contraseña", { exact: true }).press("Enter");
  await expect(
    page.getByRole("region", { name: "Usuario actual" }),
  ).toBeVisible();
}
async function command(page: Page, url: string, data: object) {
  const token = await page.request.get("/api/security/antiforgery");
  const { requestToken } = (await token.json()) as { requestToken: string };
  const response = await page.request.post(url, {
    data,
    headers: {
      "X-NexoBar-CSRF": requestToken,
      "Idempotency-Key": randomUUID(),
    },
  });
  expect(response.ok()).toBeTruthy();
  return response;
}

test("polish: session read failure, GET retry, local empty validation, password toggle, 401, Enter and logout", async ({
  page,
}) => {
  let fail = true;
  await page.route("**/api/identity-sessions/current", async (route) => {
    if (route.request().method() === "GET" && fail) {
      await route.abort();
    } else await route.continue();
  });
  await page.goto("/");
  await expect(page.getByRole("alert")).toHaveText(
    "No pudimos comprobar tu sesión.",
  );
  await expect(page.getByRole("heading", { name: "Ingresar" })).toHaveCount(0);
  fail = false;
  await page.getByRole("button", { name: "Reintentar", exact: true }).click();
  await expect(page.getByRole("heading", { name: "Ingresar" })).toBeVisible();
  let posts = 0;
  page.on("request", (request) => {
    if (
      new URL(request.url()).pathname === "/api/identity-sessions" &&
      request.method() === "POST"
    )
      posts++;
  });
  await page.getByRole("button", { name: "Ingresar", exact: true }).click();
  await expect(page.getByRole("alert")).toContainText(
    "Ingresá tu usuario y contraseña.",
  );
  expect(posts).toBe(0);
  const password = page.getByLabel("Contraseña", { exact: true });
  await password.fill("wrong");
  await page.getByRole("button", { name: "Mostrar contraseña" }).click();
  await expect(password).toHaveAttribute("type", "text");
  await expect(password).toHaveValue("wrong");
  await page.getByRole("button", { name: "Ocultar contraseña" }).click();
  await expect(password).toHaveAttribute("type", "password");
  await page
    .getByLabel("Usuario de acceso", { exact: true })
    .fill(`missing-${randomUUID()}`);
  await password.press("Enter");
  await expect(page.getByRole("alert")).toHaveText(
    "No pudimos ingresar. Revisá el usuario y la contraseña.",
  );
  await expect(password).not.toHaveAttribute("aria-invalid", "true");
  await page
    .getByLabel("Usuario de acceso", { exact: true })
    .fill(accounts.general[0]);
  await password.fill(accounts.general[1]);
  await password.press("Enter");
  await expect(
    page.getByRole("region", { name: "Usuario actual" }),
  ).toBeVisible();
  await page
    .getByRole("button", { name: "Cerrar sesión", exact: true })
    .click();
  await expect(page.getByRole("heading", { name: "Ingresar" })).toBeVisible();
});

test("polish: own access save returns to login with its confirmed reason", async ({
  page,
}) => {
  await login(page, "general");
  const name = `Acceso polish ${randomUUID().slice(0, 8)}`;
  const created = await command(page, "/api/identities", {
    operationalName: name,
    isActive: true,
  });
  const { identityId } = (await created.json()) as { identityId: string };
  await command(
    page,
    `/api/identities/${identityId}/responsibilities/GeneralConfiguration/assign`,
    {},
  );
  await command(page, `/api/identities/${identityId}/credential`, {
    loginIdentifier: name,
    secret: "polish-original-secret",
  });
  await page
    .getByRole("button", { name: "Cerrar sesión", exact: true })
    .click();
  await page.getByLabel("Usuario de acceso", { exact: true }).fill(name);
  await page
    .getByLabel("Contraseña", { exact: true })
    .fill("polish-original-secret");
  await page.getByRole("button", { name: "Ingresar", exact: true }).click();
  await page
    .getByRole("button", { name: `Administrar ${name}`, exact: true })
    .click();
  await page
    .getByRole("button", { name: `Cambiar acceso de ${name}`, exact: true })
    .click();
  const field = page.getByLabel("Nueva contraseña");
  await expect(field).toBeFocused();
  await expect(field).toHaveAttribute("autocomplete", "new-password");
  await field.fill("polish-replacement-secret");
  await page
    .getByRole("button", { name: "Guardar acceso", exact: true })
    .click();
  await expect(page.getByRole("heading", { name: "Ingresar" })).toBeVisible();
  await expect(
    page.getByText("Tu acceso fue actualizado. Volvé a ingresar."),
  ).toBeVisible();
});

test("polish: failed Products GET is not empty and retry does not send a command", async ({
  page,
}) => {
  let reads = 0,
    failRead = true;
  await page.route("**/api/catalog/operational-products", async (route) => {
    reads++;
    if (failRead) await route.abort();
    else
      await route.fulfill({
        status: 200,
        contentType: "application/json",
        body: "[]",
      });
  });
  await login(page, "order");
  const workflow = page.getByRole("region", {
    name: "Preparar pedido",
    exact: true,
  });
  await expect(
    workflow.getByText("No pudimos consultar los productos."),
  ).toBeVisible();
  await expect(
    workflow.getByText("No hay productos vigentes para agregar."),
  ).toHaveCount(0);
  const previousReads = reads;
  failRead = false;
  await workflow.getByRole("button", { name: "Reintentar consulta" }).click();
  await expect(
    workflow.getByText("No hay productos vigentes para agregar."),
  ).toBeVisible();
  expect(reads).toBe(previousReads + 1);
});

test("polish: Context creation stays confirmed after a failed refresh; retry only reads", async ({
  page,
}) => {
  await login(page, "general");
  await expect(
    page.getByRole("list", { name: "Contextos configurados" }),
  ).toBeVisible();
  let failed = false,
    posts = 0;
  await page.route(
    "**/api/operational-configuration/contexts",
    async (route) => {
      if (route.request().method() === "POST") {
        posts++;
        await route.continue();
      } else if (!failed) {
        failed = true;
        await route.abort();
      } else await route.continue();
    },
  );
  const name = `Terraza polish ${randomUUID().slice(0, 8)}`;
  await page.getByLabel("Nombre del contexto").fill(name);
  await page
    .getByRole("button", { name: "Crear contexto", exact: true })
    .click();
  await expect(
    page.getByText(`Se creó “${name}”, pero no pudimos actualizar la lista.`),
  ).toBeVisible();
  await page
    .getByRole("button", { name: "Reintentar consulta", exact: true })
    .click();
  await expect(
    page.getByRole("group", { name: `Administrar ${name}`, exact: true }),
  ).toBeVisible();
  expect(posts).toBe(1);
});

for (const entity of ["contexts", "preparation-responsibilities"] as const) {
  test(`polish: ${entity} rename focus, Escape and native delete safe focus and return`, async ({
    page,
  }) => {
    await login(page, "general");
    const name = `Polish ${entity} ${randomUUID().slice(0, 8)}`;
    await page
      .getByLabel(
        entity === "contexts"
          ? "Nombre del contexto"
          : "Nombre del destino de preparación",
      )
      .fill(name);
    await page
      .getByRole("button", {
        name:
          entity === "contexts"
            ? "Crear contexto"
            : "Crear destino de preparación",
        exact: true,
      })
      .click();
    const row = page.getByRole("group", {
      name: `Administrar ${name}`,
      exact: true,
    });
    const rename = row.getByRole("button", {
      name: `Cambiar nombre de ${name}`,
      exact: true,
    });
    await rename.click();
    const input = row.getByLabel(`Nuevo nombre de ${name}`);
    await expect(input).toBeFocused();
    expect(
      await input.evaluate(
        (element) =>
          (
            element as unknown as {
              selectionStart: number;
              selectionEnd: number;
            }
          ).selectionEnd! -
          (
            element as unknown as {
              selectionStart: number;
              selectionEnd: number;
            }
          ).selectionStart!,
      ),
    ).toBe(name.length);
    await input.press("Escape");
    await expect(rename).toBeFocused();
    const open = row.getByRole("button", {
      name: `Eliminar definitivamente ${name}`,
      exact: true,
    });
    await open.click();
    const dialog = page.getByRole("dialog", {
      name: `Eliminar definitivamente “${name}”`,
      exact: true,
    });
    await expect(
      dialog.getByRole("button", { name: "Volver", exact: true }),
    ).toBeFocused();
    await page.keyboard.press("Shift+Tab");
    await expect(
      dialog.getByRole("button", {
        name: "Eliminar definitivamente",
        exact: true,
      }),
    ).toBeFocused();
    await page.keyboard.press("Escape");
    await expect(dialog).toHaveCount(0);
    await expect(open).toBeFocused();
    await open.click();
    await dialog
      .getByRole("button", { name: "Eliminar definitivamente", exact: true })
      .click();
    await expect(row).toHaveCount(0);
    await expect(
      page.locator(
        entity === "contexts"
          ? "#contexts-title"
          : "#preparation-responsibilities-title",
      ),
    ).toBeFocused();
    await expect(
      page.getByText(`Se eliminó definitivamente “${name}”.`),
    ).toBeVisible();
  });
  test(`polish: ${entity} uncertain lifecycle retries the exact committed intent`, async ({
    page,
  }) => {
    await login(page, "general");
    const name = `Retry polish ${randomUUID().slice(0, 8)}`;
    await page
      .getByLabel(
        entity === "contexts"
          ? "Nombre del contexto"
          : "Nombre del destino de preparación",
      )
      .fill(name);
    await page
      .getByRole("button", {
        name:
          entity === "contexts"
            ? "Crear contexto"
            : "Crear destino de preparación",
        exact: true,
      })
      .click();
    const row = page.getByRole("group", {
      name: `Administrar ${name}`,
      exact: true,
    });
    const requests: { url: string; body: string | null; key: string }[] = [];
    await page.route(
      `**/api/operational-configuration/${entity}/*/retire`,
      async (route) => {
        const request = route.request();
        requests.push({
          url: request.url(),
          body: request.postData(),
          key: request.headers()["idempotency-key"],
        });
        if (requests.length === 1) {
          expect((await route.fetch()).ok()).toBeTruthy();
          await route.abort();
        } else await route.continue();
      },
    );
    await row
      .getByRole("button", { name: `Retirar ${name}`, exact: true })
      .click();
    await row
      .getByRole("button", { name: "Confirmar retiro", exact: true })
      .click();
    await expect(
      row.getByText(/No pudimos confirmar el resultado/),
    ).toBeVisible();
    await expect(
      row.getByRole("button", { name: `Cambiar nombre de ${name}` }),
    ).toBeDisabled();
    await row
      .getByRole("button", { name: "Reintentar esta operación", exact: true })
      .click();
    await expect(row.getByText("Retirado", { exact: true })).toBeVisible();
    expect(requests).toHaveLength(2);
    expect(requests[1]).toEqual(requests[0]);
  });
}

test("polish: Inventory correction sending, known failure, uncertainty, exact retry and success", async ({
  page,
}) => {
  await login(page, "inventory");
  const itemName = "Insumo corrección Inventario E2E";
  const item = page.getByRole("article", { name: itemName, exact: true });
  await item.getByRole("button", { name: "Entrada", exact: true }).click();
  await item.getByLabel(`Cantidad de entrada para ${itemName}`).fill("1");
  await item
    .getByRole("button", { name: "Registrar entrada", exact: true })
    .click();
  await expect(item).toContainText("Entrada registrada");
  await item
    .getByRole("button", {
      name: `Ver movimientos de ${itemName}`,
      exact: true,
    })
    .click();
  const history = page.getByRole("region", {
    name: "Movimientos",
    exact: true,
  });
  await history
    .getByRole("button", { name: "Corregir movimiento", exact: true })
    .first()
    .click();
  await page.route("**/api/inventory/movements/*/corrections", (route) =>
    route.fulfill({
      status: 409,
      contentType: "application/problem+json",
      body: JSON.stringify({
        status: 409,
        code: "inventory.movement_correction.stale_revision",
      }),
    }),
  );
  await history
    .getByRole("button", { name: "Guardar corrección", exact: true })
    .click();
  await expect(history.getByRole("alert")).toContainText(
    "El movimiento cambió.",
  );
  await expect(history.getByLabel("Cantidad corregida")).toBeEnabled();
  await page.unroute("**/api/inventory/movements/*/corrections");
  await history.getByLabel("Cantidad corregida").fill("0");
  let release!: () => void;
  const gate = new Promise<void>((resolve) => {
    release = resolve;
  });
  const requests: { url: string; body: string | null; key: string }[] = [];
  await page.route(
    "**/api/inventory/movements/*/corrections",
    async (route) => {
      const request = route.request();
      requests.push({
        url: request.url(),
        body: request.postData(),
        key: request.headers()["idempotency-key"],
      });
      if (requests.length === 1) {
        await gate;
        await route.abort();
      } else await route.continue();
    },
  );
  await history
    .getByRole("button", { name: "Guardar corrección", exact: true })
    .click();
  await expect(
    history.getByRole("button", { name: "Guardando corrección…", exact: true }),
  ).toBeDisabled();
  await expect(
    history.getByRole("button", { name: "Reintentar corrección", exact: true }),
  ).toHaveCount(0);
  expect(requests).toHaveLength(1);
  release();
  await expect(
    history.getByText(/No pudimos confirmar si se guardó la corrección/),
  ).toBeVisible();
  await history
    .getByRole("button", { name: "Reintentar corrección", exact: true })
    .click();
  await expect(
    history.getByText(`Se guardó la corrección de “${itemName}”.`),
  ).toBeVisible();
  expect(requests).toHaveLength(2);
  expect(requests[1]).toEqual(requests[0]);
});

for (const entity of ["contexts", "preparation-responsibilities"] as const) {
  test(`polish: ${entity} confirmed rename plus failed refresh retries only GET`, async ({
    page,
  }) => {
    await login(page, "general");
    const name = `Refresh polish ${randomUUID().slice(0, 8)}`;
    const label =
      entity === "contexts"
        ? "Nombre del contexto"
        : "Nombre del destino de preparación";
    await page.getByLabel(label).fill(name);
    await page
      .getByRole("button", {
        name:
          entity === "contexts"
            ? "Crear contexto"
            : "Crear destino de preparación",
        exact: true,
      })
      .click();
    const row = page.getByRole("group", {
      name: `Administrar ${name}`,
      exact: true,
    });
    await row
      .getByRole("button", { name: `Cambiar nombre de ${name}`, exact: true })
      .click();
    const renamed = name + " nuevo";
    await row.getByLabel(`Nuevo nombre de ${name}`).fill(renamed);
    let failRead = true,
      posts = 0;
    await page.route(
      `**/api/operational-configuration/${entity}`,
      async (route) => {
        if (route.request().method() === "GET" && failRead) await route.abort();
        else await route.continue();
      },
    );
    page.on("request", (request) => {
      if (
        request.method() === "POST" &&
        request.url().includes(`/${entity}/`) &&
        request.url().endsWith("/operational-name-changes")
      )
        posts++;
    });
    await row
      .getByRole("button", { name: "Guardar nombre", exact: true })
      .click();
    await expect(
      row.getByText(
        `“${name}” ahora se llama “${renamed}”, pero no pudimos actualizar la lista.`,
      ),
    ).toBeVisible();
    await expect(
      row.getByRole("button", {
        name: "Reintentar esta operación",
        exact: true,
      }),
    ).toHaveCount(0);
    failRead = false;
    await row
      .getByRole("button", { name: "Reintentar consulta", exact: true })
      .click();
    await expect(
      page.getByRole("group", { name: `Administrar ${renamed}`, exact: true }),
    ).toBeVisible();
    expect(posts).toBe(1);
  });
}

test("polish: created and consulted Order references copy the complete value", async ({
  page,
  context,
}) => {
  await context.grantPermissions(["clipboard-read", "clipboard-write"]);
  await login(page, "order");
  const workflow = page.getByRole("region", {
    name: "Preparar pedido",
    exact: true,
  });
  await workflow
    .getByRole("button", { name: /^Agregar .* a Preparar pedido$/ })
    .first()
    .click();
  await workflow
    .getByLabel("Contexto del pedido")
    .selectOption({ label: "Contexto base E2E" });
  await workflow
    .getByRole("button", { name: "Crear Pedido", exact: true })
    .click();
  const active = page.getByRole("region", {
    name: "Agregar productos al pedido",
    exact: true,
  });
  const reference = await active
    .locator(".active-order-summary > strong")
    .innerText();
  expect(reference).toMatch(/^[0-9a-f-]{36}$/i);
  await active
    .getByRole("button", { name: "Copiar referencia", exact: true })
    .click();
  await expect(active.getByText("Copiado", { exact: true })).toBeVisible();
  expect(await page.evaluate("navigator.clipboard.readText()")).toBe(reference);
  const order = page.getByRole("region", {
    name: "Pedido activo",
    exact: true,
  });
  await order
    .getByRole("button", { name: "Copiar referencia", exact: true })
    .click();
  await expect(order.getByText("Copiado", { exact: true })).toBeVisible();
  expect(await page.evaluate("navigator.clipboard.readText()")).toBe(reference);
});
