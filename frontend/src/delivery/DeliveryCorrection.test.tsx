import {
  act,
  fireEvent,
  render,
  screen,
  waitFor,
  within,
} from "@testing-library/react";
import { beforeEach, expect, it, vi } from "vitest";
import { useState } from "react";
import { DeliveryPanel } from "./DeliveryPanel.tsx";
import { OrderLookup } from "../orderOperations/OrderLookup.tsx";
import { discardAntiforgeryToken } from "../identity/sessionClient.ts";
import type { OrderResponse } from "../orderOperations/orderOperationsClient.ts";

const fetchMock = vi.fn<typeof fetch>();
const mutation = vi.fn<typeof fetch>();
const unauthorized = vi.fn();
let delivered: number;
let order: OrderResponse;
let holdRefresh: Promise<void> | undefined;
const target =
  "/api/order-operations/orders/order-id/incorporations/inc-1/contents/2/correct-delivery";
function json(value: unknown, status = 200) {
  return new Response(JSON.stringify(value), {
    status,
    headers: { "Content-Type": "application/json" },
  });
}
function conflict(code: string) {
  return new Response(JSON.stringify({ code }), {
    status: 409,
    headers: { "Content-Type": "application/problem+json" },
  });
}
function result(quantity: number, resultingDeliveredQuantity = 99) {
  return json({
    orderId: "order-id",
    incorporationId: "inc-1",
    contentOrdinal: 2,
    historyId: "history",
    occurredAt: "2026-09-05T12:00:00Z",
    correctedQuantity: quantity,
    previousDeliveredQuantity: 3,
    resultingDeliveredQuantity,
  });
}
function Harness() {
  const [request, setRequest] = useState({
    operationalReference: "ref",
    sequence: 0,
  });
  const [blocked, setBlocked] = useState(false);
  return (
    <>
      <OrderLookup
        products={[]}
        requestedLookup={request}
        activeOperationalReference="ref"
        onContinueOrder={vi.fn()}
        identityId="identity"
        onOrderState={(loaded) =>
          setBlocked(loaded.isFrozen || loaded.isClosed)
        }
      />
      <DeliveryPanel
        operationalReference="ref"
        onUnauthorized={unauthorized}
        ordinaryMutationsBlocked={blocked}
        onOrderChanged={(reference) =>
          setRequest((current) => ({
            operationalReference: reference,
            sequence: current.sequence + 1,
          }))
        }
      />
    </>
  );
}
async function open() {
  render(<Harness />);
  await screen.findByRole("article", {
    name: "Agua, sin instrucción, incorporación 1",
  });
  await screen.findByText("Importe funcional actual");
}
function content() {
  return screen.getByRole("article", {
    name: "Agua, sin instrucción, incorporación 1",
  });
}
function action() {
  return screen.getByRole("button", { name: /^Corregir entrega / });
}
function submit() {
  return screen.getByRole("button", {
    name: "Confirmar corrección de entrega",
  });
}
function input() {
  return screen.getByLabelText(/^Cantidad a corregir —/);
}
async function correct(quantity: string) {
  fireEvent.click(action());
  fireEvent.change(input(), { target: { value: quantity } });
  fireEvent.click(submit());
}
function amount() {
  return screen.getByText("Importe funcional actual").parentElement!;
}
beforeEach(() => {
  discardAntiforgeryToken();
  fetchMock.mockReset();
  mutation.mockReset();
  unauthorized.mockReset();
  holdRefresh = undefined;
  delivered = 3;
  order = {
    operationalReference: "ref",
    context: "Mesa",
    incorporations: [],
    functionalAmount: "30.00",
    isLiquidationEligible: false,
    liquidationBlockers: ["unresolved_fulfillment"],
    isLiquidated: false,
    isFrozen: false,
    liquidatedAmount: null,
    liquidationMode: null,
    declaredPaymentMedium: null,
    isClosureEligible: false,
    isClosed: false,
    closedAt: null,
  };
  fetchMock.mockImplementation(async (url, init) => {
    if (init?.method === "POST") return mutation(url, init);
    if (String(url).endsWith("/antiforgery"))
      return json({ requestToken: "csrf-original" });
    if (holdRefresh) await holdRefresh;
    if (String(url).endsWith("/delivery"))
      return json({
        orderId: "order-id",
        operationalReference: "ref",
        currentContext: "Mesa",
        contents: [
          {
            incorporationId: "inc-1",
            incorporationOrdinal: 1,
            contentOrdinal: 2,
            productId: "product",
            productOperationalName: "Agua",
            instruction: null,
            totalQuantity: 5,
            requiresPreparationAtConfirmation: true,
            readyQuantity: 5,
            deliveredQuantity: delivered,
            deliverableQuantity: 5 - delivered,
            remainingQuantity: 5 - delivered,
          },
        ],
      });
    if (String(url) === "/api/order-operations/orders/ref") return json(order);
    throw new Error(`Unexpected request: ${url}`);
  });
  vi.stubGlobal("fetch", fetchMock);
});
it("offers explicit Correction only with effective delivery", async () => {
  delivered = 0;
  await open();
  expect(
    screen.queryByRole("button", { name: /^Corregir entrega / }),
  ).not.toBeInTheDocument();
  delivered = 3;
  fireEvent.click(screen.getByRole("button", { name: "Actualizar" }));
  expect(
    await screen.findByRole("button", { name: /^Corregir entrega / }),
  ).toBeEnabled();
});
it.each(["", "0", "-1", "1.5", "4"])(
  "rejects exact invalid quantity %s without clamping or sending",
  async (value) => {
    await open();
    await correct(value);
    expect(
      await screen.findByText("Ingresá una cantidad entera entre 1 y 3."),
    ).toBeVisible();
    expect(mutation).not.toHaveBeenCalled();
    expect(screen.getByText("Entrega resultante (prevista): —")).toBeVisible();
  },
);
it.each([1, 3])(
  "corrects %s with authoritative quantities, amount and ordinary redelivery; preserves Preparation",
  async (quantity) => {
    let release!: () => void;
    mutation.mockImplementationOnce(async () => {
      holdRefresh = new Promise<void>((resolve) => {
        release = resolve;
      });
      return result(quantity);
    });
    await open();
    expect(amount()).toHaveTextContent("30.00");
    fireEvent.click(action());
    fireEvent.change(input(), { target: { value: String(quantity) } });
    expect(
      screen.getByText(`Entrega resultante (prevista): ${3 - quantity}`),
    ).toBeVisible();
    fireEvent.click(submit());
    await waitFor(() => expect(mutation).toHaveBeenCalledTimes(1));
    expect(mutation.mock.calls[0][0]).toBe(target);
    expect(JSON.parse(mutation.mock.calls[0][1]!.body as string)).toEqual({
      quantity,
    });

    expect(content()).toHaveTextContent("Delivered3");
    expect(content()).not.toHaveTextContent("Delivered99");
    delivered = 3 - quantity;
    order = { ...order, functionalAmount: quantity === 3 ? "0.00" : "20.00" };
    await act(async () => {
      holdRefresh = undefined;
      release();
    });
    await waitFor(() =>
      expect(content()).toHaveTextContent(`Delivered${3 - quantity}`),
    );
    await waitFor(() =>
      expect(amount()).toHaveTextContent(order.functionalAmount),
    );
    expect(content()).toHaveTextContent(`Deliverable${2 + quantity}`);
    expect(content()).toHaveTextContent("Ready5");
    expect(content()).toHaveTextContent("Total5");
    if (quantity === 3)
      expect(
        screen.queryByRole("button", { name: /^Corregir entrega / }),
      ).not.toBeInTheDocument();
    mutation.mockImplementationOnce(async () => {
      delivered = 3;
      order = { ...order, functionalAmount: "30.00" };
      return json({
        incorporationId: "inc-1",
        contentOrdinal: 2,
        historyId: "redelivery",
        occurredAt: "2026-09-05T12:00:00Z",
        deliveredQuantity: 3,
      });
    });
    fireEvent.change(screen.getByLabelText(/^Cantidad a entregar —/), {
      target: { value: String(quantity) },
    });
    fireEvent.click(screen.getByRole("button", { name: /^Entregar Agua/ }));
    await waitFor(() => expect(amount()).toHaveTextContent("30.00"));
    expect(content()).toHaveTextContent("Ready5");
    expect(content()).toHaveTextContent("Total5");
    expect(mutation.mock.calls.map(([url]) => url)).toEqual([
      target,
      "/api/order-operations/incorporations/inc-1/contents/2/deliver",
    ]);
  },
);
it.each(["network", "timeout", "5xx"])(
  "freezes target/body/key/token on %s; blocks duplicates and retries exactly",
  async (failure) => {
    mutation
      .mockImplementationOnce(async () => {
        if (failure === "5xx") return new Response("failure", { status: 500 });
        throw new Error(failure);
      })
      .mockImplementationOnce(async () => result(1));
    await open();
    await correct("1");
    await screen.findByText(
      "No se pudo confirmar el resultado de la corrección de entrega.",
    );
    const original = mutation.mock.calls[0];
    expect(original[0]).toBe(target);
    expect(original[1]).toMatchObject({
      body: '{"quantity":1}',
      headers: {
        "Idempotency-Key": expect.stringMatching(
          /^[0-9a-f]{8}-[0-9a-f]{4}-4[0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$/,
        ),
        "X-NexoBar-CSRF": "csrf-original",
      },
    });
    expect(action()).toBeDisabled();
    expect(input()).toBeDisabled();
    expect(submit()).toBeDisabled();
    expect(
      screen.getByRole("button", { name: /^Entregar Agua/ }),
    ).toBeDisabled();
    fireEvent.click(submit());
    fireEvent.click(action());
    expect(mutation).toHaveBeenCalledTimes(1);
    discardAntiforgeryToken();
    fireEvent.click(screen.getByRole("button", { name: "Reintentar" }));
    await waitFor(() => expect(mutation).toHaveBeenCalledTimes(2));
    expect(mutation.mock.calls[1]).toEqual(original);
    expect(
      fetchMock.mock.calls.filter(([url]) =>
        String(url).endsWith("/antiforgery"),
      ),
    ).toHaveLength(1);
  },
);
it.each([401, 403])(
  "handles HTTP %s without losing the auth distinction",
  async (status) => {
    mutation.mockResolvedValueOnce(new Response(null, { status }));
    await open();
    await correct("1");
    if (status === 401)
      await waitFor(() => expect(unauthorized).toHaveBeenCalledOnce());
    else {
      expect(
        await screen.findByText(
          /Esta Identity no tiene autorización para realizar entregas/,
        ),
      ).toBeVisible();
      expect(unauthorized).not.toHaveBeenCalled();
    }
    expect(
      screen.queryByRole("button", { name: "Reintentar" }),
    ).not.toBeInTheDocument();
  },
);
it.each([
  "no_effective_delivery",
  "quantity_exceeds_delivered",
  "idempotency_key_conflict",
])("resolves uncertain retry %s and refreshes both states", async (code) => {
  mutation
    .mockRejectedValueOnce(new Error("network"))
    .mockImplementationOnce(async () => {
      delivered = 0;
      order = { ...order, functionalAmount: "0.00" };
      return conflict(`order_operations.delivery_correction.${code}`);
    });
  await open();
  await correct("1");
  await screen.findByRole("button", { name: "Reintentar" });
  fireEvent.click(screen.getByRole("button", { name: "Reintentar" }));
  await waitFor(() => expect(amount()).toHaveTextContent("0.00"));
  expect(content()).toHaveTextContent("Delivered0");
  expect(
    screen.queryByRole("button", { name: "Reintentar" }),
  ).not.toBeInTheDocument();
  expect(within(content()).getByRole("alert")).toHaveTextContent(
    code === "idempotency_key_conflict"
      ? "no coincide"
      : "estado de la entrega cambió",
  );
});
it.each(["isFrozen", "isClosed"] as const)(
  "hides Correction when Order is %s",
  async (flag) => {
    order = { ...order, [flag]: true };
    await open();
    await waitFor(() =>
      expect(
        screen.queryByRole("button", { name: /^Corregir entrega / }),
      ).not.toBeInTheDocument(),
    );
  },
);
it("refreshes stale frozen conflict, explains it and immediately hides Correction", async () => {
  mutation.mockImplementationOnce(async () => {
    order = { ...order, isFrozen: true };
    return conflict("order_operations.order.frozen");
  });
  await open();
  await correct("1");
  expect(
    await screen.findByText(
      "El Pedido está congelado y ya no puede modificarse.",
    ),
  ).toBeVisible();
  expect(
    screen.queryByRole("button", { name: /^Corregir entrega / }),
  ).not.toBeInTheDocument();
  await screen.findByText(/Pedido congelado. Las operaciones ordinarias/);
});
