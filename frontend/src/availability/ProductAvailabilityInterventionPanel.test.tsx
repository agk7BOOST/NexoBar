import { render, screen, waitFor } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { beforeEach, describe, expect, it, vi } from "vitest";
import {
  AvailabilityNetworkError,
  AvailabilityProblemError,
  type AvailabilityAdministrationProduct,
} from "./availabilityClient.ts";
import { ProductAvailabilityInterventionPanel } from "./ProductAvailabilityInterventionPanel.tsx";

const { listMock, sendMock, tokenMock, discardMock } = vi.hoisted(() => ({
  listMock: vi.fn(),
  sendMock: vi.fn(),
  tokenMock: vi.fn(),
  discardMock: vi.fn(),
}));

vi.mock("./availabilityClient.ts", async (importOriginal) => {
  const original =
    await importOriginal<typeof import("./availabilityClient.ts")>();
  return {
    ...original,
    listAvailabilityAdministrationProducts: listMock,
    sendProductAvailabilityIntent: sendMock,
  };
});

vi.mock("../identity/sessionClient.ts", async (importOriginal) => {
  const original =
    await importOriginal<typeof import("../identity/sessionClient.ts")>();
  return {
    ...original,
    getAntiforgeryToken: tokenMock,
    discardAntiforgeryToken: discardMock,
  };
});

const available: AvailabilityAdministrationProduct = {
  id: "p1",
  operationalName: "Agua",
  isAvailable: true,
};
const unavailable: AvailabilityAdministrationProduct = {
  id: "p2",
  operationalName: "Café",
  isAvailable: false,
};

function renderPanel(
  products: AvailabilityAdministrationProduct[] = [available, unavailable],
) {
  const onUnauthorized = vi.fn();
  const onForbidden = vi.fn();
  render(
    <ProductAvailabilityInterventionPanel
      onUnauthorized={onUnauthorized}
      onForbidden={onForbidden}
    />,
  );
  return { onUnauthorized, onForbidden, products };
}

describe("ProductAvailabilityInterventionPanel", () => {
  beforeEach(() => {
    listMock.mockReset();
    sendMock.mockReset();
    tokenMock.mockReset().mockResolvedValue("csrf");
    discardMock.mockReset();
    vi.stubGlobal("crypto", { randomUUID: () => "availability-key" });
  });

  it("distinguishes a failed read from an empty list and allows an explicit retry", async () => {
    listMock
      .mockRejectedValueOnce(new AvailabilityNetworkError())
      .mockResolvedValueOnce([available]);
    const user = userEvent.setup();
    renderPanel();
    expect(await screen.findByText(/No se pudo consultar/)).toBeInTheDocument();
    expect(
      screen.queryByText("No hay productos vigentes."),
    ).not.toBeInTheDocument();
    await user.click(
      screen.getByRole("button", {
        name: "Reintentar consulta de disponibilidad",
      }),
    );
    expect(
      await screen.findByRole("button", { name: "Marcar no disponible Agua" }),
    ).toBeEnabled();
    expect(screen.queryByText(/No se pudo consultar/)).not.toBeInTheDocument();
  });

  it("finishes loading when access is denied even if the current Identity refresh is delayed", async () => {
    listMock.mockRejectedValueOnce(
      new AvailabilityProblemError({ status: 403 }),
    );
    const { onForbidden } = renderPanel();
    expect(
      await screen.findByText(/no tiene autorización/),
    ).toBeInTheDocument();
    expect(onForbidden).toHaveBeenCalledOnce();
    expect(screen.queryByText(/Cargando productos/)).not.toBeInTheDocument();
    expect(
      screen.queryByText("No hay productos vigentes."),
    ).not.toBeInTheDocument();
  });

  it("shows explicit states and only availability actions", async () => {
    listMock.mockResolvedValueOnce([available, unavailable]);
    renderPanel();

    expect(await screen.findByText("Disponible")).toBeInTheDocument();
    expect(screen.getByText("No disponible")).toBeInTheDocument();
    expect(
      screen.getByRole("button", { name: "Marcar no disponible Agua" }),
    ).toBeInTheDocument();
    expect(
      screen.getByRole("button", { name: "Marcar disponible Café" }),
    ).toBeInTheDocument();
    expect(
      screen.queryByText(/Retirar|Reactivar|Precio|Grupo|Preparación/),
    ).not.toBeInTheDocument();
  });

  it.each([
    [available, "Marcar no disponible Agua", false],
    [unavailable, "Marcar disponible Café", true],
  ])(
    "sends the observed state and requested transition",
    async (product, label, next) => {
      listMock.mockResolvedValueOnce([product]);
      sendMock.mockResolvedValueOnce({
        productId: product.id,
        isAvailable: next,
      });
      listMock.mockResolvedValueOnce([{ ...product, isAvailable: next }]);
      const user = userEvent.setup();
      renderPanel();

      await user.click(await screen.findByRole("button", { name: label }));
      await waitFor(() => expect(sendMock).toHaveBeenCalledTimes(1));
      expect(sendMock.mock.calls[0]?.[0]).toMatchObject({
        productId: product.id,
        request: {
          expectedCurrentAvailability: product.isAvailable,
          newAvailability: next,
        },
        idempotencyKey: "availability-key",
        antiforgeryToken: "csrf",
      });
      await waitFor(() => expect(listMock).toHaveBeenCalledTimes(2));
    },
  );

  it("reloads after a stale concurrency conflict without resubmitting", async () => {
    listMock
      .mockResolvedValueOnce([available])
      .mockResolvedValueOnce([{ ...available, isAvailable: true }]);
    sendMock.mockRejectedValueOnce(
      new AvailabilityProblemError({
        status: 409,
        code: "catalog.product.availability_concurrency_conflict",
        productId: "p1",
        currentAvailability: true,
      }),
    );
    const user = userEvent.setup();
    renderPanel();
    await user.click(
      await screen.findByRole("button", { name: "Marcar no disponible Agua" }),
    );

    expect(
      await screen.findByText(/disponibilidad cambió/),
    ).toBeInTheDocument();
    await waitFor(() => expect(listMock).toHaveBeenCalledTimes(2));
    expect(sendMock).toHaveBeenCalledTimes(1);
  });

  it.each([
    ["catalog.product.not_current", "ya no está vigente"],
    ["catalog.product.not_found", "ya no existe"],
  ])("reloads after %s and removes the stale Product", async (code, text) => {
    listMock.mockResolvedValueOnce([available]).mockResolvedValueOnce([]);
    sendMock.mockRejectedValueOnce(
      new AvailabilityProblemError({
        status: code === "catalog.product.not_found" ? 404 : 409,
        code,
      }),
    );
    const user = userEvent.setup();
    renderPanel();
    await user.click(
      await screen.findByRole("button", { name: "Marcar no disponible Agua" }),
    );

    expect(await screen.findByText(new RegExp(text))).toBeInTheDocument();
    await waitFor(() => expect(listMock).toHaveBeenCalledTimes(2));
    expect(screen.queryByText("Agua")).not.toBeInTheDocument();
  });

  it("keeps the exact intent and key for an uncertain explicit retry", async () => {
    listMock.mockResolvedValueOnce([available]);
    sendMock
      .mockRejectedValueOnce(new AvailabilityNetworkError())
      .mockResolvedValueOnce({ productId: "p1", isAvailable: false });
    listMock.mockResolvedValueOnce([{ ...available, isAvailable: false }]);
    const user = userEvent.setup();
    renderPanel();
    await user.click(
      await screen.findByRole("button", { name: "Marcar no disponible Agua" }),
    );
    await user.click(
      await screen.findByRole("button", { name: "Reintentar misma intención" }),
    );

    await waitFor(() => expect(sendMock).toHaveBeenCalledTimes(2));
    expect(sendMock.mock.calls[1]?.[0]).toBe(sendMock.mock.calls[0]?.[0]);
    await waitFor(() => expect(listMock).toHaveBeenCalledTimes(2));
  });
});
