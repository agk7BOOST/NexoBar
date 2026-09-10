import { render, screen, waitFor, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { beforeEach, describe, expect, it, vi } from "vitest";
import {
  discardAntiforgeryToken,
  getAntiforgeryToken,
  listPreparationDestinations,
} from "../identity/sessionClient.ts";
import { PreparationPanel } from "./PreparationPanel.tsx";
import {
  correctPreparationReady,
  correctPreparationStart,
  listPreparationWork,
  PreparationProblemError,
  type PreparationCommandResult,
  type PreparationWork,
} from "./preparationClient.ts";

vi.mock("../identity/sessionClient.ts", async (importOriginal) => ({
  ...(await importOriginal<typeof import("../identity/sessionClient.ts")>()),
  discardAntiforgeryToken: vi.fn(),
  getAntiforgeryToken: vi.fn(),
  listPreparationDestinations: vi.fn(),
}));

vi.mock("./preparationClient.ts", async (importOriginal) => ({
  ...(await importOriginal<typeof import("./preparationClient.ts")>()),
  correctPreparationReady: vi.fn(),
  correctPreparationStart: vi.fn(),
  listPreparationWork: vi.fn(),
}));

const destination = {
  preparationResponsibilityId: "destination-1",
  operationalName: "Cocina",
};
const key =
  "11111111-1111-4111-8111-111111111111" as `${string}-${string}-${string}-${string}-${string}`;

const work: PreparationWork = {
  workId: "work-1",
  preparationResponsibilityId: destination.preparationResponsibilityId,
  operationalReference: "order-1",
  context: "Mesa 7",
  incorporationId: "incorporation-1",
  contentOrdinal: 2,
  incorporationOrdinal: 1,
  productId: "product-1",
  productOperationalName: "Papas",
  instruction: "Sin sal",
  totalQuantity: 8,
  pendingQuantity: 2,
  inPreparationQuantity: 2,
  readyQuantity: 4,
  deliveredQuantity: 2,
  confirmedAt: "2026-09-10T11:00:00Z",
};

function result(
  item: PreparationWork,
  quantities: Partial<PreparationCommandResult> = {},
): PreparationCommandResult {
  return {
    workId: item.workId,
    historyId: "history-1",
    occurredAt: "2026-09-10T12:00:00Z",
    totalQuantity: item.totalQuantity,
    pendingQuantity: item.pendingQuantity,
    inPreparationQuantity: item.inPreparationQuantity,
    readyQuantity: item.readyQuantity,
    ...quantities,
  };
}

function correctionInput(name: "inicio" | "listo") {
  return screen.getByLabelText(
    `Cantidad a corregir ${name} de Papas, incorporación 1, Mesa 7, Sin sal`,
  );
}

function correctionButton(name: "inicio" | "listo") {
  return screen.getByRole("button", {
    name: `Corregir ${name} Papas, incorporación 1, Mesa 7, Sin sal`,
  });
}

async function renderWork(
  item: PreparationWork = work,
  props: Partial<React.ComponentProps<typeof PreparationPanel>> = {},
) {
  vi.mocked(listPreparationWork).mockResolvedValueOnce([item]);
  render(<PreparationPanel onUnauthorized={vi.fn()} {...props} />);
  await screen.findByText("Papas", { selector: "strong" });
}

describe("Preparation progress correction", () => {
  beforeEach(() => {
    vi.mocked(discardAntiforgeryToken).mockReset();
    vi.mocked(getAntiforgeryToken).mockReset().mockResolvedValue("csrf-1");
    vi.mocked(listPreparationDestinations)
      .mockReset()
      .mockResolvedValue([destination]);
    vi.mocked(listPreparationWork).mockReset();
    vi.mocked(correctPreparationStart).mockReset();
    vi.mocked(correctPreparationReady).mockReset();
    vi.spyOn(crypto, "randomUUID").mockReturnValue(key);
  });

  it("offers Correct Start only with InPreparation and enforces that boundary", async () => {
    const user = userEvent.setup();
    await renderWork();

    expect(correctionInput("inicio")).toHaveAttribute("max", "2");
    await user.clear(correctionInput("inicio"));
    await user.type(correctionInput("inicio"), "3");
    await user.click(correctionButton("inicio"));

    expect(await screen.findByRole("alert")).toHaveTextContent(
      "Ingresá una cantidad entera entre 1 y 2 para corregir el inicio.",
    );
    expect(correctPreparationStart).not.toHaveBeenCalled();
  });

  it("does not offer Correct Start without InPreparation", async () => {
    await renderWork({ ...work, inPreparationQuantity: 0, pendingQuantity: 4 });
    expect(
      screen.queryByRole("button", { name: /Corregir inicio Papas/ }),
    ).not.toBeInTheDocument();
  });

  it("previews Correct Start and finishes with the authoritative refresh", async () => {
    const refreshed = {
      ...work,
      pendingQuantity: 3,
      inPreparationQuantity: 1,
    };
    vi.mocked(correctPreparationStart).mockResolvedValue(
      result(work, { pendingQuantity: 4, inPreparationQuantity: 0 }),
    );
    vi.mocked(listPreparationWork)
      .mockResolvedValueOnce([work])
      .mockResolvedValueOnce([refreshed]);
    const user = userEvent.setup();
    await renderWork();

    await user.clear(correctionInput("inicio"));
    await user.type(correctionInput("inicio"), "1");
    expect(
      screen.getByText(/En preparación 2 → 1; Pendiente 2 → 3/),
    ).toBeVisible();
    await user.click(correctionButton("inicio"));

    await waitFor(() => expect(listPreparationWork).toHaveBeenCalledTimes(2));
    const row = screen.getByRole("row", { name: /Papas/ });
    await waitFor(() => expect(row).toHaveTextContent("Pendiente3"));
    expect(row).toHaveTextContent("En preparación1");
  });

  it("offers Correct Ready with max Ready minus Delivered and respects Delivered", async () => {
    await renderWork();

    expect(correctionInput("listo")).toHaveAttribute("max", "2");
  });

  it("does not offer Correct Ready at the Delivered boundary", async () => {
    await renderWork({
      ...work,
      readyQuantity: 2,
      deliveredQuantity: 2,
      pendingQuantity: 4,
    });
    expect(
      screen.queryByRole("button", { name: /Corregir listo Papas/ }),
    ).not.toBeInTheDocument();
  });

  it("previews Correct Ready and finishes with the authoritative refresh", async () => {
    const refreshed = {
      ...work,
      inPreparationQuantity: 3,
      readyQuantity: 3,
    };
    vi.mocked(correctPreparationReady).mockResolvedValue(
      result(work, { inPreparationQuantity: 4, readyQuantity: 2 }),
    );
    vi.mocked(listPreparationWork)
      .mockResolvedValueOnce([work])
      .mockResolvedValueOnce([refreshed]);
    const user = userEvent.setup();
    await renderWork();

    await user.clear(correctionInput("listo"));
    await user.type(correctionInput("listo"), "1");
    expect(screen.getByText(/Listo 4 → 3; En preparación 2 → 3/)).toBeVisible();
    await user.click(correctionButton("listo"));

    await waitFor(() => expect(listPreparationWork).toHaveBeenCalledTimes(2));
    const row = screen.getByRole("row", { name: /Papas/ });
    await waitFor(() => expect(row).toHaveTextContent("Listo3"));
    expect(row).toHaveTextContent("En preparación3");
  });

  it("distinguishes progress correction from cancellation/content correction and has no Ready to Pending", async () => {
    await renderWork();
    const row = screen.getByRole("row", { name: /Papas/ });

    expect(row).toHaveTextContent(
      "Correcciones de progreso registrado por error",
    );
    expect(row).toHaveTextContent("Cancellation, Content Correction");
    expect(row).toHaveTextContent(
      "Delivery Correction, OperationalIntervention",
    );
    expect(row).toHaveTextContent("deshacer genérico");
    expect(
      within(row).queryByText(/Ready.*Pending|Listo.*Pendiente/),
    ).not.toBeInTheDocument();
    expect(
      within(row).queryByRole("button", {
        name: /Cancelar|Corregir contenido/,
      }),
    ).not.toBeInTheDocument();
  });

  it.each(["Frozen", "Closed"])(
    "does not offer corrections when %s",
    async () => {
      await renderWork(work, { isOrderBlocked: () => true });
      expect(
        screen.queryByRole("button", { name: /Corregir inicio/ }),
      ).not.toBeInTheDocument();
      expect(
        screen.queryByRole("button", { name: /Corregir listo/ }),
      ).not.toBeInTheDocument();
    },
  );

  it("retries an uncertain correction with the exact Work, quantity, key and antiforgery token", async () => {
    vi.mocked(correctPreparationReady)
      .mockRejectedValueOnce(new TypeError("network"))
      .mockResolvedValueOnce(result(work));
    vi.mocked(listPreparationWork)
      .mockResolvedValueOnce([work])
      .mockResolvedValueOnce([work]);
    const user = userEvent.setup();
    await renderWork();
    await user.clear(correctionInput("listo"));
    await user.type(correctionInput("listo"), "1");
    await user.click(correctionButton("listo"));
    await user.click(await screen.findByRole("button", { name: "Reintentar" }));

    await waitFor(() =>
      expect(correctPreparationReady).toHaveBeenCalledTimes(2),
    );
    expect(vi.mocked(correctPreparationReady).mock.calls[1][0]).toEqual(
      vi.mocked(correctPreparationReady).mock.calls[0][0],
    );
    expect(getAntiforgeryToken).toHaveBeenCalledTimes(1);
  });

  it.each([401, 403, 409])(
    "handles correction %s with existing Preparation behavior",
    async (status) => {
      const onUnauthorized = vi.fn();
      vi.mocked(correctPreparationStart).mockRejectedValueOnce(
        new PreparationProblemError(status),
      );
      if (status === 409) {
        vi.mocked(listPreparationWork)
          .mockResolvedValueOnce([work])
          .mockResolvedValueOnce([
            { ...work, inPreparationQuantity: 1, pendingQuantity: 3 },
          ]);
      }
      await renderWork(work, { onUnauthorized });
      await userEvent.setup().click(correctionButton("inicio"));
      if (status === 401) {
        await waitFor(() => expect(onUnauthorized).toHaveBeenCalledOnce());
        expect(discardAntiforgeryToken).toHaveBeenCalled();
      } else if (status === 403) {
        expect(await screen.findByRole("alert")).toHaveTextContent(
          "Esta Identity no tiene autorización para esa preparación.",
        );
        expect(onUnauthorized).not.toHaveBeenCalled();
      } else {
        expect(await screen.findByRole("alert")).toHaveTextContent(
          "El estado de preparación cambió. Se actualizó la información.",
        );
        await waitFor(() =>
          expect(listPreparationWork).toHaveBeenCalledTimes(2),
        );
      }
    },
  );
});
