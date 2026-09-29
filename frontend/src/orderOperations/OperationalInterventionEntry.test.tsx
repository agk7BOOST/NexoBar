import { render, screen, waitFor } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { beforeEach, expect, it, vi } from "vitest";
import App from "../App.tsx";
import {
  getCurrentIdentity,
  SessionProblemError,
} from "../identity/sessionClient.ts";

vi.mock("../catalog/catalogClient.ts", () => ({
  listProducts: vi.fn(async () => []),
}));
vi.mock("../catalog/CatalogPanel.tsx", () => ({ CatalogPanel: () => null }));
vi.mock("./OrderWorkflow.tsx", () => ({
  OrderWorkflow: () => <p>Composición disponible</p>,
}));
vi.mock("./OrderLookup.tsx", () => ({ OrderLookup: () => null }));
vi.mock("../preparation/PreparationPanel.tsx", () => ({
  PreparationPanel: () => null,
}));
vi.mock("../delivery/DeliveryPanel.tsx", () => ({ DeliveryPanel: () => null }));
vi.mock("../inventory/InventoryPanel.tsx", () => ({
  InventoryPanel: () => null,
}));
vi.mock("../identity/SessionBar.tsx", () => ({ SessionBar: () => null }));
vi.mock("../identity/LoginPanel.tsx", () => ({
  LoginPanel: () => <p>Iniciar sesión</p>,
}));
vi.mock("../identity/sessionClient.ts", async (importOriginal) => ({
  ...(await importOriginal<typeof import("../identity/sessionClient.ts")>()),
  getCurrentIdentity: vi.fn(),
}));

beforeEach(() => {
  vi.mocked(getCurrentIdentity).mockReset();
});

it("mounts the exact intervention lookup for an authenticated Identity without Preparation claims", async () => {
  vi.mocked(getCurrentIdentity).mockResolvedValue({
    identityId: "actor",
    operationalName: "Operador",
    responsibilities: ["OperationalIntervention"],
  });
  render(<App />);
  await userEvent
    .setup()
    .click(
      await screen.findByRole("button", {
        name: "Intervención en preparación",
      }),
    );
  expect(
    await screen.findByRole("region", { name: "Intervención operacional" }),
  ).toBeInTheDocument();
  expect(
    screen.getByRole("button", { name: "Consultar para intervenir" }),
  ).toBeEnabled();
  expect(screen.queryByText("Composición disponible")).not.toBeInTheDocument();
  expect(
    screen.queryByRole("button", { name: "Cancelar cantidad ya lista" }),
  ).not.toBeInTheDocument();
});

it("does not offer intervention without a usable Session", async () => {
  vi.mocked(getCurrentIdentity).mockRejectedValue(
    new SessionProblemError(401, {}),
  );
  render(<App />);
  await waitFor(() =>
    expect(screen.getByText("Iniciar sesión")).toBeInTheDocument(),
  );
  expect(
    screen.queryByRole("region", { name: "Intervención operacional" }),
  ).not.toBeInTheDocument();
});
