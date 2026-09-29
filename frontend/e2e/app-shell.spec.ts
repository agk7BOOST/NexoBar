import { expect, test, type Page } from "@playwright/test";

async function signIn(page: Page, login: string, secret = `${login}-secret`) {
  await page.goto("/");
  await page.getByLabel("Identificador de acceso").fill(login);
  await page.getByLabel("Secreto").fill(secret);
  await page.getByRole("button", { name: "Ingresar" }).click();
  await expect(
    page.getByRole("region", { name: "Usuario actual" }),
  ).toBeVisible();
}

test("workspace navigation preserves uncertain availability and moves keyboard focus", async ({
  page,
}) => {
  let reads = 0;
  const attempts: { body: string | null; key: string | undefined }[] = [];
  await page.route(
    "**/api/catalog/availability-administration-products",
    async (route) => {
      reads++;
      await route.fulfill({
        json: [
          {
            id: "shell-product",
            operationalName: "Agua de prueba",
            isAvailable: true,
          },
        ],
      });
    },
  );
  await page.route(
    "**/api/catalog/products/shell-product/availability-changes",
    async (route) => {
      attempts.push({
        body: route.request().postData(),
        key: route.request().headers()["idempotency-key"],
      });
      await route.abort("failed");
    },
  );
  await signIn(page, "complete-cancellation-e2e");
  const navigation = page.getByRole("navigation", {
    name: "Espacios de trabajo",
  });
  await expect(navigation.getByRole("button")).toHaveText([
    "Pedidos",
    "Productos",
    "Intervención en preparación",
  ]);
  await navigation
    .getByRole("button", { name: "Productos", exact: true })
    .focus();
  await page.keyboard.press("Enter");
  await expect(
    page.getByRole("heading", { level: 1, name: "Productos" }),
  ).toBeFocused();
  await page
    .getByRole("button", { name: "Marcar no disponible Agua de prueba" })
    .click();
  await expect(
    page.getByText(
      "Resultado incierto. Reintentá exactamente la misma intención.",
    ),
  ).toBeVisible();
  await navigation
    .getByRole("button", { name: "Pedidos", exact: true })
    .click();
  await expect(
    page.getByRole("heading", { level: 1, name: "Pedidos" }),
  ).toBeFocused();
  await expect(
    page.getByRole("button", { name: "Reintentar misma intención" }),
  ).toBeHidden();
  await navigation
    .getByRole("button", { name: "Productos", exact: true })
    .click();
  await expect(
    page.getByRole("button", { name: "Marcar no disponible Agua de prueba" }),
  ).toBeDisabled();
  await page
    .getByRole("button", { name: "Reintentar misma intención" })
    .click();
  await expect.poll(() => attempts.length).toBe(2);
  expect(attempts[1]).toEqual(attempts[0]);
  expect(attempts[0]?.key).toBeTruthy();
  expect(reads).toBe(1);
});

const surfaces = [
  { login: "delivery-e2e", workspace: "Pedidos" },
  {
    login: "preparador-e2e",
    secret: "preparation-e2e-secret",
    workspace: "Preparación",
  },
  { login: "price-catalog-e2e", workspace: "Productos" },
  { login: "intervention-e2e", workspace: "Productos" },
  { login: "inventory-config-e2e", workspace: "Inventario" },
  { login: "inventory-operation-e2e", workspace: "Inventario" },
  { login: "general-configuration-admin-a-e2e", workspace: "Configuración" },
];

for (const surface of surfaces) {
  test(`responsive workspace: ${surface.login}`, async ({ page }, testInfo) => {
    await signIn(page, surface.login, surface.secret);
    await expect(
      page.getByRole("heading", { level: 1, name: surface.workspace }),
    ).toBeVisible();
    await expect(
      page.getByText(/^Cargando/).filter({ visible: true }),
    ).toHaveCount(0);
    for (const width of [1280, 768, 390, 320]) {
      await page.setViewportSize({ width, height: 900 });
      await expect
        .poll(() =>
          page.evaluate(
            "document.documentElement.scrollWidth <= window.innerWidth",
          ),
        )
        .toBe(true);
      // Horizontal scrolling is confined to tables; labels stay above their fields.
      for (const form of await page.locator("form:visible").all()) {
        for (const label of await form.locator(":scope > label[for]").all()) {
          const id = await label.getAttribute("for");
          const input = form.locator(`[id="${id}"]`);
          if (!(await input.isVisible())) continue;
          const labelBox = await label.boundingBox();
          const inputBox = await input.boundingBox();
          expect(labelBox).not.toBeNull();
          expect(inputBox).not.toBeNull();
          expect(Math.abs(labelBox!.x - inputBox!.x)).toBeLessThan(2);
          expect(labelBox!.y + labelBox!.height).toBeLessThanOrEqual(
            inputBox!.y + 1,
          );
          if (width <= 640) {
            const formBox = await form.boundingBox();
            expect(Math.abs(inputBox!.width - formBox!.width)).toBeLessThan(2);
          }
        }
      }
      await page.screenshot({
        path: testInfo.outputPath(`workspace-${width}.png`),
        fullPage: true,
      });
      if (width === 320) {
        const tableAction = page
          .locator(".table-scroll")
          .getByRole("button")
          .first();
        if (await tableAction.count()) {
          await tableAction.focus();
          await expect(tableAction).toBeInViewport();
          await page.screenshot({
            path: testInfo.outputPath("table-actions-mobile.png"),
          });
        }
      }
    }
  });
}
