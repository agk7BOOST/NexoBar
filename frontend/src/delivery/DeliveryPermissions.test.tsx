import { fireEvent, render, screen, waitFor } from "@testing-library/react";
import { beforeEach, expect, it, vi } from "vitest";
import { DeliveryPanel } from "./DeliveryPanel.tsx";
import { discardAntiforgeryToken } from "../identity/sessionClient.ts";

const fetchMock = vi.fn<typeof fetch>();

function json(value: unknown, status = 200) {
  return new Response(JSON.stringify(value), {
    status,
    headers: { "Content-Type": "application/json" },
  });
}

beforeEach(() => {
  discardAntiforgeryToken();
  fetchMock.mockReset();
  vi.stubGlobal("fetch", fetchMock);
});

it.each([
  [
    "correct-content-quantity",
    "Corregir cantidad confirmada",
    "Cantidad confirmada a corregir",
    "Confirmar corrección de cantidad confirmada",
  ],
  [
    "cancel-content-quantity",
    "Cancelar cantidad pendiente",
    "Cantidad a cancelar",
    "Confirmar cancelación de cantidad pendiente",
  ],
])(
  "allows %s for an order operator without Preparation access",
  async (command, action, input, confirm) => {
    let pending = 2;
    let removed = 0;
    let cancelled = 0;
    fetchMock.mockImplementation(async (url, init) => {
      const path = String(url);
      if (path.includes("/preparation")) return json({}, 403);
      if (path.endsWith("/antiforgery")) return json({ requestToken: "csrf" });
      if (path.endsWith(`/${command}`) && init?.method === "POST") {
        expect(JSON.parse(String(init.body))).toEqual({ quantity: 1 });
        pending -= 1;
        if (command === "correct-content-quantity") removed += 1;
        else cancelled += 1;
        return json({
          orderId: "order-id",
          incorporationId: "inc-1",
          contentOrdinal: 1,
          correctedQuantity: 1,
          cancelledQuantity: 1,
          confirmedQuantity: 5,
          resultingRemovedByCorrectionQuantity: removed,
          resultingCancelledQuantity: cancelled,
          resultingFulfillmentQuantity: 5 - removed - cancelled,
        });
      }
      if (path.endsWith("/delivery"))
        return json({
          orderId: "order-id",
          operationalReference: "ref",
          currentContext: "Mesa 1",
          contents: [
            {
              incorporationId: "inc-1",
              incorporationOrdinal: 1,
              contentOrdinal: 1,
              productId: "burger",
              productOperationalName: "Hamburguesa",
              instruction: null,
              confirmedQuantity: 5,
              removedByCorrectionQuantity: removed,
              cancelledQuantity: cancelled,
              currentFulfillmentQuantity: 5 - removed - cancelled,
              totalQuantity: 5 - removed - cancelled,
              requiresPreparationAtConfirmation: true,
              pendingQuantity: pending,
              inPreparationQuantity: 1,
              readyQuantity: 2,
              deliveredQuantity: 1,
              deliverableQuantity: 1,
              remainingQuantity: 4 - removed - cancelled,
            },
          ],
        });
      if (path === "/api/order-operations/orders/ref")
        return json({
          operationalReference: "ref",
          contextId: "mesa-1",
          context: "Mesa 1",
          incorporations: [],
          functionalAmount: "35000",
          liquidationBlockers: ["unresolved_fulfillment"],
          isFrozen: false,
          isClosed: false,
        });
      throw new Error(`Unexpected request: ${path}`);
    });

    render(
      <DeliveryPanel operationalReference="ref" onUnauthorized={vi.fn()} />,
    );
    fireEvent.click(await screen.findByRole("button", { name: action }));
    const quantity = screen.getByLabelText(new RegExp(`^${input}`));
    expect(quantity).toHaveAttribute("max", "2");
    fireEvent.change(quantity, { target: { value: "1" } });
    fireEvent.click(screen.getByRole("button", { name: confirm }));
    await waitFor(() =>
      expect(screen.getByRole("article")).toHaveTextContent(
        "F · Obligación vigente4",
      ),
    );
    expect(
      fetchMock.mock.calls.some(([url]) =>
        String(url).includes("/preparation"),
      ),
    ).toBe(false);
    expect(
      fetchMock.mock.calls.filter(([, init]) => init?.method === "POST"),
    ).toHaveLength(1);
  },
);
