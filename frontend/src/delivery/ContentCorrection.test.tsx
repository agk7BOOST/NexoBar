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
import { PreparationPanel } from "../preparation/PreparationPanel.tsx";
import { OrderLookup } from "../orderOperations/OrderLookup.tsx";
import { discardAntiforgeryToken } from "../identity/sessionClient.ts";
import type { OrderResponse } from "../orderOperations/orderOperationsClient.ts";
import type { OrderDeliveryContent } from "./deliveryClient.ts";
import type { PreparationWork } from "../preparation/preparationClient.ts";
import { maximumContentCorrection } from "./contentCorrection.ts";

const fetchMock = vi.fn<typeof fetch>();
const mutation = vi.fn<typeof fetch>();
const unauthorized = vi.fn();
let item: OrderDeliveryContent;
let works: PreparationWork[];
let order: OrderResponse;
let hold: Promise<void> | undefined;
const endpoint =
  "/api/order-operations/orders/order-id/incorporations/inc-1/contents/7/correct-content-quantity";
function json(value: unknown, status = 200) {
  return new Response(JSON.stringify(value), {
    status,
    headers: { "Content-Type": "application/json" },
  });
}
function result(quantity: number) {
  return json({
    orderId: "order-id",
    incorporationId: "inc-1",
    contentOrdinal: 7,
    correctedQuantity: quantity,
    confirmedQuantity: 5,
    resultingRemovedByCorrectionQuantity: quantity,
    resultingFulfillmentQuantity: 99,
  }); // Command results never replace authoritative reads.
}
function prepared(pending = 2, inPreparation = 1, ready = 2) {
  item = {
    ...item,
    requiresPreparationAtConfirmation: true,
    readyQuantity: ready,
    deliverableQuantity: ready - item.deliveredQuantity,
  };
  works = [
    {
      workId: "work",
      incorporationId: "inc-1",
      contentOrdinal: 7,
      incorporationOrdinal: 1,
      productId: "same-product",
      productOperationalName: "Agua",
      instruction: null,
      preparationResponsibilityId: "kitchen",
      operationalReference: "ref",
      context: "Mesa",
      confirmedAt: "2026-09-06T12:00:00Z",
      totalQuantity: 5,
      pendingQuantity: pending,
      inPreparationQuantity: inPreparation,
      readyQuantity: ready,
    },
  ];
}
function Harness() {
  const [request, setRequest] = useState({
    operationalReference: "ref",
    sequence: 0,
  });
  const [busy, setBusy] = useState(false);
  return (
    <>
      <OrderLookup
        products={[]}
        requestedLookup={request}
        activeOperationalReference="ref"
        onContinueOrder={vi.fn()}
        identityId="identity"
        isOrderMutationBusy={() => busy}
      />
      <PreparationPanel
        onUnauthorized={unauthorized}
        refreshSequence={request.sequence}
        isOrderBlocked={() => busy}
      />
      <DeliveryPanel
        operationalReference="ref"
        onUnauthorized={unauthorized}
        onBusyChange={(_reference, value) => setBusy(value)}
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
function article() {
  return screen.getByRole("article", {
    name: "Agua, sin instrucción, incorporación 1",
  });
}
function action() {
  return screen.getByRole("button", {
    name: "Corregir cantidad confirmada",
  });
}
function input() {
  return screen.getByLabelText(/^Cantidad a retirar \(x\)/);
}
function submit() {
  return screen.getByRole("button", {
    name: "Confirmar corrección de cantidad confirmada",
  });
}
async function open() {
  render(<Harness />);
  await screen.findByRole("article", {
    name: "Agua, sin instrucción, incorporación 1",
  });
}
async function correct(value: string) {
  fireEvent.click(action());
  fireEvent.change(input(), { target: { value } });
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
  hold = undefined;
  item = {
    incorporationId: "inc-1",
    incorporationOrdinal: 1,
    contentOrdinal: 7,
    productId: "same-product",
    productOperationalName: "Agua",
    instruction: null,
    confirmedQuantity: 5,
    removedByCorrectionQuantity: 0,
    currentFulfillmentQuantity: 5,
    totalQuantity: 5,
    requiresPreparationAtConfirmation: false,
    readyQuantity: null,
    deliveredQuantity: 1,
    deliverableQuantity: 4,
    remainingQuantity: 4,
  };
  works = [];
  order = {
    operationalReference: "ref",
    context: "Mesa",
    incorporations: [],
    functionalAmount: "10.00",
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
    if (hold) await hold;
    if (String(url).endsWith("/preparation-destinations"))
      return json([
        { preparationResponsibilityId: "kitchen", operationalName: "Cocina" },
      ]);
    if (String(url).includes("/preparation/work?")) return json(works);
    if (String(url).endsWith("/delivery"))
      return json({
        orderId: "order-id",
        operationalReference: "ref",
        currentContext: "Mesa",
        contents: [item],
      });
    if (String(url) === "/api/order-operations/orders/ref") return json(order);
    throw new Error(`Unexpected request: ${url}`);
  });
  vi.stubGlobal("fetch", fetchMock);
});

it("shows Q/R/F and excludes the delivered portion from direct eligibility", async () => {
  await open();
  expect(action()).toBeEnabled();
  expect(article()).toHaveTextContent("Q · Cantidad confirmada original5");
  expect(article()).toHaveTextContent("R · Retirada por corrección0");
  expect(article()).toHaveTextContent("F · Obligación vigente5");
  expect(article()).toHaveTextContent("Máximo corregible actualmente: 4");
});
it("uses exact Pending, excluding InPreparation and Ready", async () => {
  prepared();
  await open();
  expect(article()).toHaveTextContent("Máximo corregible actualmente: 2");
  fireEvent.click(action());
  expect(input()).toHaveAttribute("max", "2");
});
it("does not cross-associate same Product across Contents or Incorporations", () => {
  prepared();
  const exact = works[0];
  const other = {
    ...exact,
    contentOrdinal: 1,
    pendingQuantity: 5,
    inPreparationQuantity: 0,
    readyQuantity: 0,
  };
  expect(maximumContentCorrection(item, [other, exact])).toBe(2);
  expect(maximumContentCorrection(item, [other])).toBeNull();
  expect(
    maximumContentCorrection(item, [
      { ...exact, incorporationId: "inc-other" },
    ]),
  ).toBeNull();
  expect(maximumContentCorrection(item, [exact, exact])).toBeNull();
});
it.each(["missing", "total", "ready", "buckets", "qrf"])(
  "makes inconsistent %s reads unavailable",
  async (kind) => {
    prepared();
    if (kind === "missing") works = [];
    if (kind === "total") works[0].totalQuantity = 4;
    if (kind === "ready") item.readyQuantity = 3;
    if (kind === "buckets") works[0].pendingQuantity = 3;
    if (kind === "qrf") item.removedByCorrectionQuantity = 2;
    await open();
    expect(
      screen.queryByRole("button", {
        name: "Corregir cantidad confirmada",
      }),
    ).toBeNull();
    expect(article()).toHaveTextContent("Estado no disponible o inconsistente");
  },
);
it.each(["", "0", "-1", "1.5", "5"])(
  "rejects %s without sending or clamping",
  async (value) => {
    await open();
    await correct(value);
    expect(
      await screen.findByText("Ingresá una cantidad entera entre 1 y 4."),
    ).toBeVisible();
    expect(mutation).not.toHaveBeenCalled();
    expect(article()).toHaveTextContent("F resultante (prevista): —");
  },
);
it.each([1, 4])(
  "refreshes direct partial/full eligible correction %s, Q and unchanged amount; liquidation eligibility changes",
  async (quantity) => {
    mutation.mockImplementationOnce(async () => {
      item = {
        ...item,
        removedByCorrectionQuantity: quantity,
        currentFulfillmentQuantity: 5 - quantity,
        totalQuantity: 5 - quantity,
        remainingQuantity: 4 - quantity,
        deliverableQuantity: 4 - quantity,
      };
      order = {
        ...order,
        isLiquidationEligible: quantity === 4,
        liquidationBlockers: quantity === 4 ? [] : ["unresolved_fulfillment"],
      };
      return result(quantity);
    });
    await open();
    await correct(String(quantity));
    await waitFor(() =>
      expect(article()).toHaveTextContent(
        `R · Retirada por corrección${quantity}`,
      ),
    );
    expect(article()).toHaveTextContent(
      `F · Obligación vigente${5 - quantity}`,
    );
    expect(article()).toHaveTextContent("Q · Cantidad confirmada original5");
    expect(amount()).toHaveTextContent("10.00");
    if (quantity === 4)
      await waitFor(() =>
        expect(screen.getByRole("button", { name: "Liquidar" })).toBeEnabled(),
      );
    expect(mutation.mock.calls[0][0]).toBe(endpoint);
  },
);
it.each([2, 5])(
  "refreshes prepared Pending/Total and preserves original Q for correction %s, including F=0",
  async (quantity) => {
    item.deliveredQuantity = 0;
    item.remainingQuantity = 5;
    prepared(5, 0, 0);
    order.functionalAmount = "0.00";
    let release!: () => void;
    mutation.mockImplementationOnce(async () => {
      hold = new Promise<void>((resolve) => {
        release = resolve;
      });
      return result(quantity);
    });
    await open();
    fireEvent.click(action());
    fireEvent.change(input(), { target: { value: String(quantity) } });
    expect(article()).toHaveTextContent(
      `F resultante (prevista): ${5 - quantity}`,
    );
    fireEvent.click(submit());
    await waitFor(() => expect(mutation).toHaveBeenCalledOnce());
    expect(article()).toHaveTextContent("F · Obligación vigente5");
    item = {
      ...item,
      removedByCorrectionQuantity: quantity,
      currentFulfillmentQuantity: 5 - quantity,
      totalQuantity: 5 - quantity,
      remainingQuantity: 5 - quantity,
    };
    works[0] = {
      ...works[0],
      totalQuantity: 5 - quantity,
      pendingQuantity: 5 - quantity,
    };
    await act(async () => {
      hold = undefined;
      release();
    });
    await waitFor(() =>
      expect(article()).toHaveTextContent(
        `F · Obligación vigente${5 - quantity}`,
      ),
    );
    const prep = screen.getByRole("region", { name: "Preparación" });
    await waitFor(() =>
      expect(prep).toHaveTextContent(`Pendiente${5 - quantity}`),
    );
    expect(prep).toHaveTextContent(`Total${5 - quantity}`);
    expect(article()).toHaveTextContent("Q · Cantidad confirmada original5");
    expect(amount()).toHaveTextContent("0.00");
    if (quantity === 5) {
      expect(prep).toHaveTextContent("Sin obligación vigente");
      expect(prep).not.toHaveTextContent("Todo listo");
    }
  },
);
it.each(["network", "timeout", "5xx"])(
  "freezes exact intent on %s and blocks duplicate and conflicting actions",
  async (failure) => {
    prepared();
    mutation
      .mockImplementationOnce(async () => {
        if (failure === "5xx") return new Response(null, { status: 503 });
        if (failure === "timeout") return new Response(null, { status: 408 });
        throw new TypeError("network");
      })
      .mockResolvedValueOnce(result(1));
    await open();
    await correct("1");
    await screen.findByText(
      "No se pudo confirmar el resultado de la corrección de cantidad confirmada.",
    );
    const original = mutation.mock.calls[0];
    expect(original[0]).toBe(endpoint);
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
      within(article()).getByRole("button", { name: /^Entregar / }),
    ).toBeDisabled();
    expect(
      screen.getByRole("button", { name: /^Iniciar Agua/ }),
    ).toBeDisabled();
    fireEvent.click(submit());
    expect(mutation).toHaveBeenCalledOnce();
    discardAntiforgeryToken();
    fireEvent.click(screen.getByRole("button", { name: "Reintentar" }));
    await waitFor(() => expect(mutation).toHaveBeenCalledTimes(2));
    expect(mutation.mock.calls[1]).toEqual(original);
  },
);
it.each([401, 403])(
  "handles %s preserving the Identity distinction",
  async (status) => {
    mutation.mockResolvedValueOnce(new Response(null, { status }));
    await open();
    await correct("1");
    if (status === 401)
      await waitFor(() => expect(unauthorized).toHaveBeenCalledOnce());
    else {
      await screen.findByText(
        /Esta Identity no tiene autorización para corregir la cantidad confirmada/,
      );
      expect(unauthorized).not.toHaveBeenCalled();
    }
    expect(screen.queryByRole("button", { name: "Reintentar" })).toBeNull();
  },
);
it("resolves a known 409 by refreshing authoritative State", async () => {
  mutation.mockImplementationOnce(async () => {
    item = {
      ...item,
      removedByCorrectionQuantity: 4,
      currentFulfillmentQuantity: 1,
      totalQuantity: 1,
      remainingQuantity: 0,
      deliverableQuantity: 0,
    };
    return new Response(
      JSON.stringify({
        code: "order_operations.content_correction.quantity_exceeds_eligible",
      }),
      { status: 409, headers: { "Content-Type": "application/problem+json" } },
    );
  });
  await open();
  await correct("1");
  await waitFor(() =>
    expect(article()).toHaveTextContent("F · Obligación vigente1"),
  );
  expect(screen.queryByRole("button", { name: "Reintentar" })).toBeNull();
});
it("supports direct F=0 without presenting a delivery fact", async () => {
  item = {
    ...item,
    deliveredQuantity: 0,
    remainingQuantity: 5,
    deliverableQuantity: 5,
  };
  mutation.mockImplementationOnce(async () => {
    item = {
      ...item,
      removedByCorrectionQuantity: 5,
      currentFulfillmentQuantity: 0,
      totalQuantity: 0,
      remainingQuantity: 0,
      deliverableQuantity: 0,
    };
    return result(5);
  });
  await open();
  await correct("5");
  await waitFor(() =>
    expect(article()).toHaveTextContent("Sin obligación vigente"),
  );
  expect(article()).toHaveTextContent("Q · Cantidad confirmada original5");
  expect(
    within(article()).queryByText("Entregado", { exact: true }),
  ).toBeNull();
});
it("keeps the uncertain correction lock attached to its original Order across navigation", async () => {
  const busy = vi.fn();
  mutation.mockRejectedValueOnce(new TypeError("network"));
  const { rerender } = render(
    <DeliveryPanel
      operationalReference="ref"
      onUnauthorized={unauthorized}
      onBusyChange={busy}
    />,
  );
  await screen.findByRole("article", {
    name: "Agua, sin instrucción, incorporación 1",
  });
  await correct("1");
  await screen.findByRole("button", { name: "Reintentar" });
  rerender(
    <DeliveryPanel
      operationalReference={null}
      onUnauthorized={unauthorized}
      onBusyChange={busy}
    />,
  );
  expect(busy).toHaveBeenLastCalledWith("ref", true);
  expect(busy).not.toHaveBeenCalledWith("ref", false);
});
it("keeps an uncertain Preparation lock when refreshed Work is unavailable", async () => {
  prepared();
  const busy = vi.fn();
  mutation.mockRejectedValueOnce(new TypeError("network"));
  const { rerender } = render(
    <PreparationPanel
      onUnauthorized={unauthorized}
      onBusyOrdersChange={busy}
    />,
  );
  fireEvent.click(await screen.findByRole("button", { name: /^Iniciar Agua/ }));
  await screen.findByRole("button", { name: "Reintentar" });
  works = [];
  rerender(
    <PreparationPanel
      onUnauthorized={unauthorized}
      onBusyOrdersChange={busy}
      refreshSequence={1}
    />,
  );
  await waitFor(() =>
    expect(screen.queryByRole("button", { name: /^Iniciar Agua/ })).toBeNull(),
  );
  expect(busy).toHaveBeenLastCalledWith(["ref"]);
});
it("offers no ordinary correction when all prepared quantity is started or Ready", async () => {
  prepared(0, 3, 2);
  await open();
  expect(article()).toHaveTextContent("Máximo corregible actualmente: 0");
  expect(
    screen.queryByRole("button", { name: "Corregir cantidad confirmada" }),
  ).toBeNull();
});
it("keeps correction unavailable when the authoritative Order refresh fails", async () => {
  await open();
  fetchMock.mockImplementation(async (url) => {
    if (String(url).endsWith("/delivery"))
      return json({
        orderId: "order-id",
        operationalReference: "ref",
        currentContext: "Mesa",
        contents: [item],
      });
    throw new TypeError("read unavailable");
  });
  fireEvent.click(
    within(screen.getByRole("region", { name: "Delivery" })).getByRole(
      "button",
      { name: "Actualizar" },
    ),
  );
  await waitFor(() =>
    expect(article()).toHaveTextContent("Estado no disponible o inconsistente"),
  );
  expect(
    screen.queryByRole("button", { name: "Corregir cantidad confirmada" }),
  ).toBeNull();
});
it.each(["isFrozen", "isClosed"] as const)(
  "does not offer Correction for %s",
  async (flag) => {
    order[flag] = true;
    await open();
    expect(
      screen.queryByRole("button", {
        name: "Corregir cantidad confirmada",
      }),
    ).toBeNull();
  },
);
