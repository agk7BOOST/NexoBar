import { expect, type Locator } from "@playwright/test";

export async function selectInitialContext(
  composition: Locator,
  name = "Contexto base E2E",
): Promise<void> {
  const selector = composition.getByRole("combobox", {
    name: "Contexto para Primera Confirmacion",
    exact: true,
  });
  await expect(
    selector.getByRole("option", { name, exact: true }),
  ).toBeAttached();
  await selector.selectOption({ label: name });
}
