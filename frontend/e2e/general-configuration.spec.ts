import {
  expect,
  test,
  type Browser,
  type BrowserContext,
  type Locator,
  type Page,
  type Response,
} from "@playwright/test";

const preparationResponsibilityName = "Cocina E2E";
const targetInitialName = "Identity configuraci\u00f3n general objetivo E2E";
const targetRenamedName = "Identity configuraci\u00f3n general renombrada E2E";
const targetLoginIdentifier = "general-configuration-target-e2e";
const targetSecret = "general-configuration-target-e2e-secret";
const adminANewSecret = "general-configuration-admin-a-e2e-new-secret";

type Administrator = {
  identifier: string;
  secret: string;
  operationalName: string;
};

const adminA: Administrator = {
  identifier: "general-configuration-admin-a-e2e",
  secret: "general-configuration-admin-a-e2e-secret",
  operationalName: "Administradora general A E2E",
};

const adminB: Administrator = {
  identifier: "general-configuration-admin-b-e2e",
  secret: "general-configuration-admin-b-e2e-secret",
  operationalName: "Administrador general B E2E",
};

function pathOf(url: string): string {
  return new URL(url).pathname;
}

function generalConfiguration(page: Page): Locator {
  return page
    .getByRole("heading", { name: "Configuraci\u00f3n general", exact: true })
    .locator("..");
}

function identityRow(
  configuration: Locator,
  page: Page,
  operationalName: string,
): Locator {
  return configuration.getByRole("row").filter({
    has: page.getByRole("cell", { name: operationalName, exact: true }),
  });
}

async function authenticateThroughCurrent(
  page: Page,
  administrator: Administrator,
): Promise<void> {
  await page.goto("/");
  await page.getByLabel("Identificador de acceso").fill(administrator.identifier);
  await page.getByLabel("Secreto").fill(administrator.secret);
  await page.getByRole("button", { name: "Ingresar" }).click();
  await expect(
    page
      .getByRole("region", { name: "Identity actual" })
      .getByText(administrator.operationalName, { exact: true }),
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
  expect([...currentIdentity.responsibilities].sort()).toEqual([
    "GeneralConfiguration",
  ]);
  await expect(generalConfiguration(page)).toBeVisible();
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

async function refreshAdministrativeState(
  page: Page,
  configuration: Locator,
): Promise<unknown> {
  const identities = waitForResponse(page, "GET", "/api/identities");
  const refresh = configuration.getByRole("button", { name: "Actualizar" });
  await expect(refresh).toBeEnabled();
  await refresh.click();
  const response = await identities;
  expect(response.ok()).toBeTruthy();
  return response.json();
}

test("S9 General Configuration administra Identities con sesiones y endpoints reales", async ({
  browser,
}) => {
  const adminAContext = await browser.newContext();
  const adminBContext = await browser.newContext();

  try {
    const adminAPage = await adminAContext.newPage();
    const adminBPage = await adminBContext.newPage();
    await authenticateThroughCurrent(adminAPage, adminA);
    await authenticateThroughCurrent(adminBPage, adminB);

    let configuration = generalConfiguration(adminAPage);
    await configuration.getByLabel("Nombre operacional").fill(targetInitialName);
    const created = waitForResponse(adminAPage, "POST", "/api/identities");
    await configuration.getByRole("button", { name: "Crear Identity" }).click();
    expect((await created).ok()).toBeTruthy();
    await expect(
      configuration.getByText("Identity creada correctamente.", { exact: true }),
    ).toBeVisible();

    let target = identityRow(configuration, adminAPage, targetInitialName);
    await expect(target).toBeVisible();
    await expect(
      target.getByRole("cell", { name: "Inactiva", exact: true }),
    ).toBeVisible();
    await expect(
      target.getByRole("cell", { name: "No configurada", exact: true }),
    ).toBeVisible();
    const afterCreate = (await refreshAdministrativeState(
      adminAPage,
      configuration,
    )) as Array<{
      operationalName: string;
      isActive: boolean;
      hasLocalCredential: boolean;
      loginIdentifier: string | null;
    }>;
    expect(afterCreate).toEqual(
      expect.arrayContaining([
        expect.objectContaining({
          operationalName: targetInitialName,
          isActive: false,
          hasLocalCredential: false,
          loginIdentifier: null,
        }),
      ]),
    );

    await target
      .getByRole("button", { name: `Cambiar nombre de ${targetInitialName}` })
      .click();
    const rename = configuration.getByRole("form", {
      name: "Cambiar nombre operacional",
    });
    await rename.getByLabel("Nuevo nombre operacional").fill(targetRenamedName);
    const renamed = waitForResponse(
      adminAPage,
      "POST",
      /\/api\/identities\/[^/]+\/change-operational-name/,
    );
    await rename
      .getByRole("button", { name: "Confirmar cambio de nombre" })
      .click();
    expect((await renamed).ok()).toBeTruthy();
    await expect(
      configuration.getByText(
        `Nombre operacional de ${targetInitialName} actualizado correctamente.`,
        { exact: true },
      ),
    ).toBeVisible();
    await refreshAdministrativeState(adminAPage, configuration);
    target = identityRow(configuration, adminAPage, targetRenamedName);
    await expect(target).toBeVisible();

    const assignPreparation = waitForResponse(
      adminAPage,
      "POST",
      /\/api\/identities\/[^/]+\/responsibilities\/Preparation\/assign/,
    );
    await target
      .getByRole("button", {
        name: `Asignar Preparation a ${targetRenamedName}`,
      })
      .click();
    const assignedTarget = await assignPreparation;
    expect(assignedTarget.ok()).toBeTruthy();
    expect(
      [...((await assignedTarget.json()) as { responsibilities: string[] })
        .responsibilities].sort(),
    ).toEqual(["Preparation"]);
    await expect(
      target.getByRole("list", {
        name: `Responsabilidades de ${targetRenamedName}`,
      }),
    ).toContainText("Preparation: Asignada");

    const grantPreparationEnablement = waitForResponse(
      adminAPage,
      "POST",
      /\/api\/identities\/[^/]+\/preparation-enablement\/[^/]+\/grant/,
    );
    await target
      .getByRole("button", {
        name: `Otorgar habilitaci\u00f3n ${preparationResponsibilityName} a ${targetRenamedName}`,
      })
      .click();
    const enabledTarget = await grantPreparationEnablement;
    expect(enabledTarget.ok()).toBeTruthy();
    const enablementState = (await enabledTarget.json()) as {
      responsibilities: string[];
      preparationEnablements: string[];
    };
    expect([...enablementState.responsibilities].sort()).toEqual([
      "Preparation",
    ]);
    expect(enablementState.preparationEnablements).toHaveLength(1);
    await expect(
      target.getByRole("list", {
        name: `Habilitaciones de preparaci\u00f3n de ${targetRenamedName}`,
      }),
    ).toContainText(`${preparationResponsibilityName}: Habilitada`);
    await refreshAdministrativeState(adminAPage, configuration);
    target = identityRow(configuration, adminAPage, targetRenamedName);
    await expect(
      target.getByRole("list", {
        name: `Responsabilidades de ${targetRenamedName}`,
      }),
    ).toContainText("Preparation: Asignada");
    await expect(
      target.getByRole("list", {
        name: `Habilitaciones de preparaci\u00f3n de ${targetRenamedName}`,
      }),
    ).toContainText(`${preparationResponsibilityName}: Habilitada`);

    await target
      .getByRole("button", {
        name: `Configurar credencial de ${targetRenamedName}`,
      })
      .click();
    const credential = configuration.getByRole("form", {
      name: "Configurar credencial local",
    });
    await credential.getByLabel("Identificador de acceso").fill(targetLoginIdentifier);
    await credential.getByLabel("Nueva clave secreta").fill(targetSecret);
    const credentialSet = waitForResponse(
      adminAPage,
      "POST",
      /\/api\/identities\/[^/]+\/credential/,
    );
    await credential.getByRole("button", { name: "Guardar credencial" }).click();
    const credentialResponse = await credentialSet;
    expect(credentialResponse.ok()).toBeTruthy();
    expect(await credentialResponse.json()).toMatchObject({
      hasLocalCredential: true,
      loginIdentifier: targetLoginIdentifier,
    });
    await expect(
      configuration.getByText("Credencial actualizada correctamente.", {
        exact: true,
      }),
    ).toBeVisible();
    await expect(
      target.getByRole("cell", { name: "Configurada", exact: true }),
    ).toBeVisible();
    await expect(
      target.getByRole("cell", { name: targetLoginIdentifier, exact: true }),
    ).toBeVisible();

    const activatedForLifecycle = waitForResponse(
      adminAPage,
      "POST",
      /\/api\/identities\/[^/]+\/activate/,
    );
    await target
      .getByRole("button", { name: `Activar ${targetRenamedName}` })
      .click();
    expect((await activatedForLifecycle).ok()).toBeTruthy();
    await expect(target.getByRole("cell", { name: "Activa", exact: true })).toBeVisible();

    const deactivated = waitForResponse(
      adminAPage,
      "POST",
      /\/api\/identities\/[^/]+\/deactivate/,
    );
    await target
      .getByRole("button", { name: `Desactivar ${targetRenamedName}` })
      .click();
    expect((await deactivated).ok()).toBeTruthy();
    await expect(target.getByRole("cell", { name: "Inactiva", exact: true })).toBeVisible();
    await refreshAdministrativeState(adminAPage, configuration);
    target = identityRow(configuration, adminAPage, targetRenamedName);
    await expect(target.getByRole("cell", { name: "Inactiva", exact: true })).toBeVisible();

    const activated = waitForResponse(
      adminAPage,
      "POST",
      /\/api\/identities\/[^/]+\/activate/,
    );
    await target
      .getByRole("button", { name: `Activar ${targetRenamedName}` })
      .click();
    expect((await activated).ok()).toBeTruthy();
    await expect(target.getByRole("cell", { name: "Activa", exact: true })).toBeVisible();
    await refreshAdministrativeState(adminAPage, configuration);
    target = identityRow(configuration, adminAPage, targetRenamedName);
    await expect(target.getByRole("cell", { name: "Activa", exact: true })).toBeVisible();

    const adminBRow = identityRow(configuration, adminAPage, adminB.operationalName);
    const revokeAdminB = waitForResponse(
      adminAPage,
      "POST",
      /\/api\/identities\/[^/]+\/responsibilities\/GeneralConfiguration\/revoke/,
    );
    await adminBRow
      .getByRole("button", {
        name: `Revocar GeneralConfiguration a ${adminB.operationalName}`,
      })
      .click();
    expect((await revokeAdminB).ok()).toBeTruthy();

    const adminBCurrent = adminBPage.waitForResponse(
      (response) =>
        pathOf(response.url()) === "/api/identity-sessions/current" &&
        response.request().method() === "GET" &&
        response.ok(),
    );
    await adminBPage.reload();
    expect(
      [...((await (await adminBCurrent).json()) as { responsibilities: string[] })
        .responsibilities].sort(),
    ).toEqual([]);
    await expect(
      adminBPage
        .getByRole("region", { name: "Identity actual" })
        .getByText(adminB.operationalName, { exact: true }),
    ).toBeVisible();
    await expect(
      adminBPage.getByRole("heading", {
        name: "Configuraci\u00f3n general",
        exact: true,
      }),
    ).toHaveCount(0);

    const adminARow = identityRow(configuration, adminAPage, adminA.operationalName);
    const lastPathRejection = waitForResponse(
      adminAPage,
      "POST",
      /\/api\/identities\/[^/]+\/responsibilities\/GeneralConfiguration\/revoke/,
    );
    await adminARow
      .getByRole("button", {
        name: `Revocar GeneralConfiguration a ${adminA.operationalName}`,
      })
      .click();
    const lastPathResponse = await lastPathRejection;
    expect(lastPathResponse.status()).toBe(409);
    expect(await lastPathResponse.json()).toMatchObject({
      code: "identities_and_capabilities.last_general_configuration_path",
    });
    await expect(
      configuration.getByText(
        "Debe permanecer al menos una v\u00eda administrativa utilizable.",
        { exact: true },
      ),
    ).toBeVisible();
    await expect(
      adminARow.getByRole("list", {
        name: `Responsabilidades de ${adminA.operationalName}`,
      }),
    ).toContainText("GeneralConfiguration: Asignada");

    const adminACurrent = adminAPage.waitForResponse(
      (response) =>
        pathOf(response.url()) === "/api/identity-sessions/current" &&
        response.request().method() === "GET" &&
        response.ok(),
    );
    await adminAPage.reload();
    expect(
      [...((await (await adminACurrent).json()) as { responsibilities: string[] })
        .responsibilities].sort(),
    ).toEqual(["GeneralConfiguration"]);
    configuration = generalConfiguration(adminAPage);
    await expect(configuration).toBeVisible();

    const currentAdminA = identityRow(configuration, adminAPage, adminA.operationalName);
    await currentAdminA
      .getByRole("button", {
        name: `Configurar credencial de ${adminA.operationalName}`,
      })
      .click();
    const replacement = configuration.getByRole("form", {
      name: "Configurar credencial local",
    });
    await replacement.getByLabel("Nueva clave secreta").fill(adminANewSecret);
    const selfCredentialReplacement = waitForResponse(
      adminAPage,
      "POST",
      /\/api\/identities\/[^/]+\/credential/,
    );
    await replacement
      .getByRole("button", { name: "Guardar credencial" })
      .click();
    expect((await selfCredentialReplacement).ok()).toBeTruthy();
    await expect(adminAPage.getByRole("heading", { name: "Ingresar" })).toBeVisible();
    await expect(
      adminAPage.getByRole("heading", {
        name: "Configuraci\u00f3n general",
        exact: true,
      }),
    ).toHaveCount(0);

    await authenticateThroughCurrent(adminAPage, {
      ...adminA,
      secret: adminANewSecret,
    });
  } finally {
    await Promise.all([adminAContext.close(), adminBContext.close()]);
  }
});
