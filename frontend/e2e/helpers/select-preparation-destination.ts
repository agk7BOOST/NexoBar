import { expect, type Locator } from "@playwright/test";

export async function selectPreparationDestination(
  preparation: Locator,
  name: string,
): Promise<void> {
  const selector = preparation.getByRole("combobox", {
    name: "Destino de preparación",
    exact: true,
  });
  const singleDestination = preparation.getByText(`Destino: ${name}`, {
    exact: true,
  });

  // The destination lookup renders these variants exclusively once it resolves.
  await expect(selector.or(singleDestination)).toBeVisible();
  if (await selector.isVisible()) {
    await expect(
      selector.getByRole("option", { name, exact: true }),
    ).toBeAttached();
    await selector.selectOption({ label: name });
  } else {
    await expect(singleDestination).toBeVisible();
  }
}
