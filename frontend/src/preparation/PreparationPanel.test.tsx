import { render, screen, waitFor } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { beforeEach, describe, expect, it, vi } from "vitest";
import {
  listPreparationDestinations,
  SessionProblemError,
} from "../identity/sessionClient.ts";
import { PreparationPanel } from "./PreparationPanel.tsx";
import {
  listPreparationWork,
  PreparationProblemError,
  type PreparationWork,
} from "./preparationClient.ts";

vi.mock("../identity/sessionClient.ts", async (importOriginal) => {
  const original =
    await importOriginal<typeof import("../identity/sessionClient.ts")>();
  return { ...original, listPreparationDestinations: vi.fn() };
});

vi.mock("./preparationClient.ts", async (importOriginal) => {
  const original =
    await importOriginal<typeof import("./preparationClient.ts")>();
  return { ...original, listPreparationWork: vi.fn() };
});

const firstDestination = {
  preparationResponsibilityId: "destination-1",
  operationalName: "Cocina",
};

const work: PreparationWork = {
  workId: "work-1",
  preparationResponsibilityId: "destination-1",
  operationalReference: "order-1",
  context: "Mesa 7",
  incorporationId: "incorporation-1",
  incorporationOrdinal: 1,
  productId: "product-technical-id",
  productOperationalName: "Papas",
  instruction: "Sin sal",
  totalQuantity: 2,
  pendingQuantity: 2,
  inPreparationQuantity: 0,
  readyQuantity: 0,
  confirmedAt: "2026-08-31T12:00:00Z",
};

describe("PreparationPanel", () => {
  beforeEach(() => {
    vi.mocked(listPreparationDestinations).mockReset();
    vi.mocked(listPreparationWork).mockReset();
  });

  it("shows zero enabled destinations without querying Work", async () => {
    vi.mocked(listPreparationDestinations).mockResolvedValue([]);
    render(<PreparationPanel onUnauthorized={vi.fn()} />);

    expect(
      await screen.findByText(
        "No hay destinos de preparación habilitados para esta Identity.",
      ),
    ).toBeInTheDocument();
    expect(listPreparationWork).not.toHaveBeenCalled();
  });

  it("auto-selects one destination and renders understandable read-only Work", async () => {
    vi.mocked(listPreparationDestinations).mockResolvedValue([
      firstDestination,
    ]);
    vi.mocked(listPreparationWork).mockResolvedValue([
      work,
      { ...work, workId: "work-2", instruction: null },
    ]);
    render(<PreparationPanel onUnauthorized={vi.fn()} />);

    expect(await screen.findAllByText("Papas")).toHaveLength(2);
    expect(screen.getByText("Cocina")).toBeInTheDocument();
    expect(screen.getByText("Sin sal")).toBeInTheDocument();
    expect(screen.getByText("Sin instrucción")).toBeInTheDocument();
    expect(screen.queryByText("product-technical-id")).not.toBeInTheDocument();
    expect(listPreparationWork).toHaveBeenCalledWith("destination-1");
    expect(
      screen.queryByRole("button", { name: /Start|Ready/i }),
    ).not.toBeInTheDocument();
  });

  it("offers multiple destinations by operational name and queries the selected UUID", async () => {
    vi.mocked(listPreparationDestinations).mockResolvedValue([
      firstDestination,
      {
        preparationResponsibilityId: "destination-2",
        operationalName: "Barra",
      },
    ]);
    vi.mocked(listPreparationWork).mockResolvedValue([]);
    const user = userEvent.setup();
    render(<PreparationPanel onUnauthorized={vi.fn()} />);
    const selector = await screen.findByLabelText("Destino de preparación");

    await user.selectOptions(selector, "destination-2");

    await waitFor(() =>
      expect(listPreparationWork).toHaveBeenCalledWith("destination-2"),
    );
    expect(screen.getByRole("option", { name: "Cocina" })).toBeInTheDocument();
    expect(screen.getByRole("option", { name: "Barra" })).toBeInTheDocument();
  });

  it("returns to login on protected 401", async () => {
    vi.mocked(listPreparationDestinations).mockResolvedValue([
      firstDestination,
    ]);
    vi.mocked(listPreparationWork).mockRejectedValue(
      new PreparationProblemError(401),
    );
    const onUnauthorized = vi.fn();
    render(<PreparationPanel onUnauthorized={onUnauthorized} />);

    await waitFor(() => expect(onUnauthorized).toHaveBeenCalledOnce());
  });

  it("keeps the authenticated surface on destinations or Work 403", async () => {
    vi.mocked(listPreparationDestinations).mockRejectedValue(
      new SessionProblemError(403, { code: "forbidden" }),
    );
    const onUnauthorized = vi.fn();
    const { unmount } = render(
      <PreparationPanel onUnauthorized={onUnauthorized} />,
    );
    expect(await screen.findByRole("alert")).toHaveTextContent(
      "Esta Identity no tiene autorización para esa preparación.",
    );
    expect(onUnauthorized).not.toHaveBeenCalled();
    unmount();

    vi.mocked(listPreparationDestinations).mockResolvedValue([
      firstDestination,
    ]);
    vi.mocked(listPreparationWork).mockRejectedValue(
      new PreparationProblemError(403),
    );
    render(<PreparationPanel onUnauthorized={onUnauthorized} />);
    expect(await screen.findByRole("alert")).toHaveTextContent(
      "Esta Identity no tiene autorización para esa preparación.",
    );
    expect(onUnauthorized).not.toHaveBeenCalled();
  });
});
