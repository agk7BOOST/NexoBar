import { render, screen, waitFor } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { beforeEach, describe, expect, it, vi } from "vitest";
import {
  discardAntiforgeryToken,
  getAntiforgeryToken,
  listPreparationDestinations,
  SessionProblemError,
} from "../identity/sessionClient.ts";
import { PreparationPanel } from "./PreparationPanel.tsx";
import {
  listPreparationWork,
  markPreparationQuantityReady,
  PreparationProblemError,
  startPreparationQuantity,
  type PreparationCommandResult,
  type PreparationWork,
} from "./preparationClient.ts";

vi.mock("../identity/sessionClient.ts", async (importOriginal) => {
  const original =
    await importOriginal<typeof import("../identity/sessionClient.ts")>();
  return {
    ...original,
    discardAntiforgeryToken: vi.fn(),
    getAntiforgeryToken: vi.fn(),
    listPreparationDestinations: vi.fn(),
  };
});

vi.mock("./preparationClient.ts", async (importOriginal) => {
  const original =
    await importOriginal<typeof import("./preparationClient.ts")>();
  return {
    ...original,
    listPreparationWork: vi.fn(),
    markPreparationQuantityReady: vi.fn(),
    startPreparationQuantity: vi.fn(),
  };
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
  totalQuantity: 5,
  pendingQuantity: 5,
  inPreparationQuantity: 0,
  readyQuantity: 0,
  confirmedAt: "2026-08-31T12:00:00Z",
};

const mixedWork: PreparationWork = {
  ...work,
  totalQuantity: 5,
  pendingQuantity: 1,
  inPreparationQuantity: 2,
  readyQuantity: 2,
};

const firstKey =
  "11111111-1111-4111-8111-111111111111" as `${string}-${string}-${string}-${string}-${string}`;
const secondKey =
  "22222222-2222-4222-8222-222222222222" as `${string}-${string}-${string}-${string}-${string}`;

function commandResult(
  item: PreparationWork,
  quantities: Partial<
    Pick<
      PreparationCommandResult,
      | "totalQuantity"
      | "pendingQuantity"
      | "inPreparationQuantity"
      | "readyQuantity"
    >
  > = {},
): PreparationCommandResult {
  return {
    workId: item.workId,
    historyId: "history-1",
    occurredAt: "2026-08-31T12:05:00Z",
    totalQuantity: item.totalQuantity,
    pendingQuantity: item.pendingQuantity,
    inPreparationQuantity: item.inPreparationQuantity,
    readyQuantity: item.readyQuantity,
    ...quantities,
  };
}

function deferred<T>() {
  let resolve!: (value: T) => void;
  let reject!: (reason?: unknown) => void;
  const promise = new Promise<T>((resolvePromise, rejectPromise) => {
    resolve = resolvePromise;
    reject = rejectPromise;
  });
  return { promise, resolve, reject };
}

async function renderWithWork(items: PreparationWork[] = [work]) {
  vi.mocked(listPreparationWork).mockResolvedValueOnce(items);
  render(<PreparationPanel onUnauthorized={vi.fn()} />);
  if (items.length > 0) {
    await screen.findAllByText(items[0].productOperationalName, {
      selector: "strong",
    });
  }
}

function startInput(item = work) {
  return screen.getByLabelText(
    `Cantidad a iniciar de ${item.productOperationalName}, incorporación ${item.incorporationOrdinal}, ${item.context}, ${item.instruction ?? "sin instrucción"}`,
  );
}

function readyInput(item = work) {
  return screen.getByLabelText(
    `Cantidad a marcar lista de ${item.productOperationalName}, incorporación ${item.incorporationOrdinal}, ${item.context}, ${item.instruction ?? "sin instrucción"}`,
  );
}

function startButton(item = work) {
  return screen.getByRole("button", {
    name: `Iniciar ${item.productOperationalName}, incorporación ${item.incorporationOrdinal}, ${item.context}, ${item.instruction ?? "sin instrucción"}`,
  });
}

function readyButton(item = work) {
  return screen.getByRole("button", {
    name: `Marcar listo ${item.productOperationalName}, incorporación ${item.incorporationOrdinal}, ${item.context}, ${item.instruction ?? "sin instrucción"}`,
  });
}

describe("PreparationPanel", () => {
  beforeEach(() => {
    vi.mocked(discardAntiforgeryToken).mockReset();
    vi.mocked(getAntiforgeryToken).mockReset().mockResolvedValue("csrf-1");
    vi.mocked(listPreparationDestinations)
      .mockReset()
      .mockResolvedValue([firstDestination]);
    vi.mocked(listPreparationWork).mockReset().mockResolvedValue([work]);
    vi.mocked(markPreparationQuantityReady).mockReset();
    vi.mocked(startPreparationQuantity).mockReset();
  });

  it("shows zero enabled destinations without querying Work", async () => {
    vi.mocked(listPreparationDestinations).mockResolvedValueOnce([]);
    render(<PreparationPanel onUnauthorized={vi.fn()} />);

    expect(
      await screen.findByText(
        "No hay destinos de preparación habilitados para esta Identity.",
      ),
    ).toBeInTheDocument();
    expect(listPreparationWork).not.toHaveBeenCalled();
  });

  it("renders mixed quantities without collapsing them into one status", async () => {
    await renderWithWork([
      mixedWork,
      { ...mixedWork, workId: "work-2", instruction: null },
    ]);

    expect(screen.getAllByText("Papas")).toHaveLength(2);
    expect(screen.getByText("Cocina")).toBeInTheDocument();
    expect(screen.getByText("Sin sal")).toBeInTheDocument();
    expect(screen.getByText("Sin instrucción")).toBeInTheDocument();
    expect(screen.queryByText("product-technical-id")).not.toBeInTheDocument();

    const row = screen.getAllByRole("row", { name: /Papas/ })[0];
    expect(row).toHaveTextContent("Total5");
    expect(row).toHaveTextContent("Pendiente1");
    expect(row).toHaveTextContent("En preparación2");
    expect(row).toHaveTextContent("Listo2");
    expect(startInput(mixedWork)).toHaveValue(1);
    expect(readyInput(mixedWork)).toHaveValue(2);
  });

  it("offers multiple destinations by operational name and preserves the selected destination", async () => {
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
    await user.click(
      screen.getByRole("button", { name: "Actualizar preparación" }),
    );

    await waitFor(() => expect(selector).toHaveValue("destination-2"));
    expect(screen.getByRole("option", { name: "Cocina" })).toBeInTheDocument();
    expect(screen.getByRole("option", { name: "Barra" })).toBeInTheDocument();
  });

  it("sends an exact partial Start and updates counters from the authoritative response", async () => {
    const updated = {
      ...work,
      pendingQuantity: 3,
      inPreparationQuantity: 2,
    };
    vi.spyOn(crypto, "randomUUID").mockReturnValue(firstKey);
    vi.mocked(startPreparationQuantity).mockResolvedValue(
      commandResult(work, {
        pendingQuantity: 3,
        inPreparationQuantity: 2,
      }),
    );
    vi.mocked(listPreparationWork)
      .mockResolvedValueOnce([work])
      .mockResolvedValueOnce([updated]);
    const user = userEvent.setup();
    await renderWithWork();

    await user.clear(startInput());
    await user.type(startInput(), "2");
    await user.click(startButton());

    await waitFor(() =>
      expect(startPreparationQuantity).toHaveBeenCalledWith({
        workId: "work-1",
        quantity: 2,
        idempotencyKey: firstKey,
        antiforgeryToken: "csrf-1",
      }),
    );
    const row = screen.getByRole("row", { name: /Papas/ });
    await waitFor(() => expect(row).toHaveTextContent("Pendiente3"));
    expect(row).toHaveTextContent("En preparación2");
    expect(listPreparationWork).toHaveBeenCalledTimes(2);
  });

  it("disables the whole Work while Start is pending and prevents double submit", async () => {
    const pending = deferred<PreparationCommandResult>();
    vi.mocked(startPreparationQuantity).mockReturnValue(pending.promise);
    const user = userEvent.setup();
    await renderWithWork([mixedWork]);

    await user.dblClick(startButton(mixedWork));

    expect(startPreparationQuantity).toHaveBeenCalledTimes(1);
    expect(startInput(mixedWork)).toBeDisabled();
    expect(readyInput(mixedWork)).toBeDisabled();
    expect(startButton(mixedWork)).toBeDisabled();
    expect(readyButton(mixedWork)).toBeDisabled();
    pending.resolve(commandResult(mixedWork));
  });

  it("hides source-empty actions, presents fully Ready, and has no Delivery language", async () => {
    const fullyReady = {
      ...work,
      pendingQuantity: 0,
      inPreparationQuantity: 0,
      readyQuantity: 5,
    };
    await renderWithWork([fullyReady]);

    expect(screen.getByText("Todo listo")).toBeInTheDocument();
    expect(screen.queryByText("Cantidad a iniciar")).not.toBeInTheDocument();
    expect(
      screen.queryByText("Cantidad a marcar lista"),
    ).not.toBeInTheDocument();
    expect(screen.queryByText(/Entreg/i)).not.toBeInTheDocument();
    expect(screen.queryByText(/Completar/i)).not.toBeInTheDocument();
  });

  it("sends an exact partial Ready and updates all counters from its response", async () => {
    const updated = {
      ...mixedWork,
      inPreparationQuantity: 1,
      readyQuantity: 3,
    };
    vi.spyOn(crypto, "randomUUID").mockReturnValue(firstKey);
    vi.mocked(markPreparationQuantityReady).mockResolvedValue(
      commandResult(mixedWork, {
        inPreparationQuantity: 1,
        readyQuantity: 3,
      }),
    );
    vi.mocked(listPreparationWork)
      .mockResolvedValueOnce([mixedWork])
      .mockResolvedValueOnce([updated]);
    const user = userEvent.setup();
    await renderWithWork([mixedWork]);

    await user.clear(readyInput(mixedWork));
    await user.type(readyInput(mixedWork), "1");
    await user.click(readyButton(mixedWork));

    await waitFor(() =>
      expect(markPreparationQuantityReady).toHaveBeenCalledWith({
        workId: "work-1",
        quantity: 1,
        idempotencyKey: firstKey,
        antiforgeryToken: "csrf-1",
      }),
    );
    const row = screen.getByRole("row", { name: /Papas/ });
    await waitFor(() => expect(row).toHaveTextContent("En preparación1"));
    expect(row).toHaveTextContent("Listo3");
  });

  it.each([
    { kind: "Start", item: work },
    { kind: "Ready", item: mixedWork },
  ])(
    "keeps the exact $kind key/body after uncertainty and clears it after successful retry",
    async ({ kind, item }) => {
      vi.spyOn(crypto, "randomUUID").mockReturnValue(firstKey);
      const commandMock =
        kind === "Start"
          ? vi.mocked(startPreparationQuantity)
          : vi.mocked(markPreparationQuantityReady);
      commandMock
        .mockRejectedValueOnce(new TypeError("network disconnected"))
        .mockResolvedValueOnce(commandResult(item));
      const user = userEvent.setup();
      await renderWithWork([item]);

      await user.click(
        kind === "Start" ? startButton(item) : readyButton(item),
      );
      expect(
        await screen.findByText("No se pudo confirmar el resultado."),
      ).toBeInTheDocument();
      const firstCall = commandMock.mock.calls[0][0];
      expect(firstCall.idempotencyKey).toBe(firstKey);
      expect(startButton(item)).toBeDisabled();
      if (item.inPreparationQuantity > 0)
        expect(readyButton(item)).toBeDisabled();

      await user.click(screen.getByRole("button", { name: "Reintentar" }));

      await waitFor(() => expect(commandMock).toHaveBeenCalledTimes(2));
      expect(commandMock.mock.calls[1][0]).toEqual(firstCall);
      await waitFor(() =>
        expect(
          screen.queryByRole("button", { name: "Reintentar" }),
        ).not.toBeInTheDocument(),
      );
    },
  );

  it("clears an uncertain intent on retry 409 and refreshes before a new intent", async () => {
    const refreshed = { ...work, pendingQuantity: 3 };
    vi.spyOn(crypto, "randomUUID").mockReturnValue(firstKey);
    vi.mocked(startPreparationQuantity)
      .mockRejectedValueOnce(new TypeError("timeout"))
      .mockRejectedValueOnce(
        new PreparationProblemError(409, {
          code: "order_operations.preparation_start.pending_quantity_insufficient",
        }),
      );
    vi.mocked(listPreparationWork)
      .mockResolvedValueOnce([work])
      .mockResolvedValueOnce([refreshed]);
    const user = userEvent.setup();
    await renderWithWork();
    await user.click(startButton());
    await user.click(await screen.findByRole("button", { name: "Reintentar" }));

    expect(
      await screen.findByText(
        "El estado de preparación cambió. Se actualizó la información.",
      ),
    ).toBeInTheDocument();
    await waitFor(() => expect(startInput()).toHaveValue(3));
    expect(startInput()).toBeEnabled();
    expect(
      screen.queryByRole("button", { name: "Reintentar" }),
    ).not.toBeInTheDocument();
    expect(listPreparationWork).toHaveBeenCalledTimes(2);
  });

  it("creates a new UUID only after a known command resolves", async () => {
    const afterFirst = {
      ...work,
      pendingQuantity: 4,
      inPreparationQuantity: 1,
    };
    vi.spyOn(crypto, "randomUUID")
      .mockReturnValueOnce(firstKey)
      .mockReturnValueOnce(secondKey);
    vi.mocked(startPreparationQuantity)
      .mockResolvedValueOnce(
        commandResult(work, {
          pendingQuantity: 4,
          inPreparationQuantity: 1,
        }),
      )
      .mockResolvedValueOnce(
        commandResult(work, {
          pendingQuantity: 3,
          inPreparationQuantity: 2,
        }),
      );
    vi.mocked(listPreparationWork).mockResolvedValue([afterFirst]);
    const user = userEvent.setup();
    await renderWithWork([work]);

    await user.clear(startInput());
    await user.type(startInput(), "1");
    await user.click(startButton());
    await waitFor(() => expect(startInput()).toBeEnabled());
    await user.clear(startInput());
    await user.type(startInput(), "1");
    await user.click(startButton());

    await waitFor(() =>
      expect(startPreparationQuantity).toHaveBeenCalledTimes(2),
    );
    expect(
      vi.mocked(startPreparationQuantity).mock.calls[0][0].idempotencyKey,
    ).toBe(firstKey);
    expect(
      vi.mocked(startPreparationQuantity).mock.calls[1][0].idempotencyKey,
    ).toBe(secondKey);
  });

  it("returns to login and clears antiforgery/intents on mutation 401", async () => {
    vi.mocked(startPreparationQuantity).mockRejectedValue(
      new PreparationProblemError(401, {
        code: "identities_and_capabilities.invalid_session",
      }),
    );
    const onUnauthorized = vi.fn();
    const user = userEvent.setup();
    vi.mocked(listPreparationWork).mockResolvedValueOnce([work]);
    render(<PreparationPanel onUnauthorized={onUnauthorized} />);
    await screen.findByText("Papas", { selector: "strong" });

    await user.click(startButton());

    await waitFor(() => expect(onUnauthorized).toHaveBeenCalledOnce());
    expect(discardAntiforgeryToken).toHaveBeenCalledOnce();
    expect(
      screen.queryByRole("button", { name: "Reintentar" }),
    ).not.toBeInTheDocument();
  });

  it("keeps the authenticated surface on mutation 403", async () => {
    vi.mocked(startPreparationQuantity).mockRejectedValue(
      new PreparationProblemError(403, {
        code: "order_operations.preparation.forbidden",
      }),
    );
    const onUnauthorized = vi.fn();
    const user = userEvent.setup();
    vi.mocked(listPreparationWork).mockResolvedValueOnce([work]);
    render(<PreparationPanel onUnauthorized={onUnauthorized} />);
    await screen.findByText("Papas", { selector: "strong" });
    await user.click(startButton());

    expect(await screen.findByRole("alert")).toHaveTextContent(
      "Esta Identity no tiene autorización para esa preparación.",
    );
    expect(onUnauthorized).not.toHaveBeenCalled();
    expect(startInput()).toBeEnabled();
  });

  it("shows a generic unavailable message and refreshes on mutation 404", async () => {
    vi.mocked(startPreparationQuantity).mockRejectedValue(
      new PreparationProblemError(404, {
        code: "order_operations.preparation_work.not_found",
      }),
    );
    vi.mocked(listPreparationWork)
      .mockResolvedValueOnce([work])
      .mockResolvedValueOnce([]);
    const user = userEvent.setup();
    await renderWithWork();
    await user.click(startButton());

    expect(
      await screen.findByText("El trabajo ya no está disponible."),
    ).toBeInTheDocument();
    await waitFor(() => expect(listPreparationWork).toHaveBeenCalledTimes(2));
    expect(screen.queryByText("perdiste habilitación")).not.toBeInTheDocument();
  });

  it("allows another Work to mutate while one Work is submitting", async () => {
    const otherWork = {
      ...work,
      workId: "work-2",
      incorporationOrdinal: 2,
      productOperationalName: "Milanesa",
    };
    const firstPending = deferred<PreparationCommandResult>();
    vi.mocked(startPreparationQuantity).mockImplementation((command) =>
      command.workId === work.workId
        ? firstPending.promise
        : Promise.resolve(commandResult(otherWork)),
    );
    const user = userEvent.setup();
    await renderWithWork([work, otherWork]);

    await user.click(startButton(work));
    await user.click(startButton(otherWork));

    await waitFor(() =>
      expect(startPreparationQuantity).toHaveBeenCalledTimes(2),
    );
    expect(startButton(work)).toBeDisabled();
    firstPending.resolve(commandResult(work));
  });

  it("validates integer quantity against the observed bucket before creating an intent", async () => {
    const randomUuid = vi.spyOn(crypto, "randomUUID");
    const user = userEvent.setup();
    await renderWithWork([work]);

    await user.clear(startInput());
    await user.type(startInput(), "6");
    await user.click(startButton());

    expect(
      await screen.findByText(
        "Ingresá una cantidad entera entre 1 y 5 para iniciar.",
      ),
    ).toBeInTheDocument();
    expect(startPreparationQuantity).not.toHaveBeenCalled();
    expect(randomUuid).not.toHaveBeenCalled();
  });

  it("keeps read authorization handling: 401 logs out and 403 does not", async () => {
    vi.mocked(listPreparationWork).mockRejectedValueOnce(
      new PreparationProblemError(401),
    );
    const onUnauthorized = vi.fn();
    render(<PreparationPanel onUnauthorized={onUnauthorized} />);
    await waitFor(() => expect(onUnauthorized).toHaveBeenCalledOnce());

    vi.mocked(listPreparationDestinations).mockRejectedValueOnce(
      new SessionProblemError(403, { code: "forbidden" }),
    );
    const stillAuthenticated = vi.fn();
    render(<PreparationPanel onUnauthorized={stillAuthenticated} />);
    expect(await screen.findByRole("alert")).toHaveTextContent(
      "Esta Identity no tiene autorización para esa preparación.",
    );
    expect(stillAuthenticated).not.toHaveBeenCalled();
  });
});
