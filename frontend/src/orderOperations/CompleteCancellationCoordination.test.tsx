import { render, screen, waitFor } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { expect, it, vi } from "vitest";
import App from "../App.tsx";

const reference = "01991e32-2a00-7000-8000-000000000001";
const probes = vi.hoisted(() => ({
  preparation: vi.fn(),
  delivery: vi.fn(),
  intervention: vi.fn(),
}));
vi.mock("../catalog/CatalogPanel.tsx", () => ({ CatalogPanel: () => null }));
vi.mock("../catalog/catalogClient.ts", () => ({
  listProducts: async () => [],
}));
vi.mock("../inventory/InventoryPanel.tsx", () => ({
  InventoryPanel: () => null,
}));
vi.mock("../preparation/PreparationPanel.tsx", () => ({
  PreparationPanel: (props: unknown) => {
    probes.preparation(props);
    return null;
  },
}));
vi.mock("../delivery/DeliveryPanel.tsx", () => ({
  DeliveryPanel: (props: unknown) => {
    probes.delivery(props);
    return null;
  },
}));
vi.mock("./OperationalInterventionPanel.tsx", () => ({
  OperationalInterventionPanel: (props: unknown) => {
    probes.intervention(props);
    return null;
  },
}));

it("coordinates cancellation uncertainty and authoritative terminal refresh across existing operational panels and PendingComposition", async () => {
  let cancelled = false;
  let posts = 0;
  let pendingReads = 0;
  const json = (value: unknown) =>
    new Response(JSON.stringify(value), {
      headers: { "Content-Type": "application/json" },
    });
  const fetchMock = vi.fn<typeof fetch>(async (url, init) => {
    if (url === "/api/identity-sessions/current")
      return json({ identityId: "base", operationalName: "Actor" });
    if (url === "/api/security/antiforgery")
      return json({ requestToken: "csrf" });
    if (url === `/api/orders/${reference}/pending-composition`) {
      pendingReads++;
      return json({
        orderId: reference,
        pendingComposition: cancelled
          ? null
          : {
              pendingCompositionId: "pending",
              createdAt: "2026-09-11T00:00:00Z",
              createdByIdentityId: "base",
            },
      });
    }
    if (url === `/api/orders/${reference}/complete-cancellation`) {
      if (init?.method === "POST") {
        posts++;
        if (posts === 1) throw new TypeError("uncertain");
        cancelled = true;
        return json({
          orderId: reference,
          isCompletelyCancelled: true,
          cancellationId: "cancel",
          occurredAt: "2026-09-11T01:00:00Z",
          pendingCompositionDiscarded: true,
          consequences: [],
        });
      }
      return json({
        orderId: reference,
        isTerminal: cancelled,
        isCompletelyCancelled: cancelled,
        cancellationId: cancelled ? "cancel" : null,
        cancelledAt: null,
        hasEffectiveDelivery: false,
        hasPendingComposition: !cancelled,
        remainingFulfillmentQuantity: 0,
        requiresOperationalIntervention: false,
        isEligible: !cancelled,
        blockers: cancelled ? ["already_completely_cancelled"] : [],
        consequences: [],
      });
    }
    if (url === `/api/order-operations/orders/${reference}`)
      return json({
        operationalReference: reference,
        context: "Mesa",
        incorporations: [],
        functionalAmount: "0",
        isLiquidationEligible: false,
        liquidationBlockers: cancelled
          ? ["order_completely_cancelled"]
          : ["pending_composition"],
        isLiquidated: false,
        isFrozen: false,
        liquidatedAmount: null,
        liquidationMode: null,
        declaredPaymentMedium: null,
        isClosureEligible: false,
        isClosed: false,
        closedAt: null,
      });
    throw new Error(`Unexpected request ${String(url)}`);
  });
  vi.stubGlobal("fetch", fetchMock);
  const user = userEvent.setup();
  render(<App />);
  await screen.findByText("Actor");
  await user.type(screen.getByLabelText("Referencia operacional"), reference);
  await user.click(screen.getByRole("button", { name: "Buscar Pedido" }));
  await screen.findByRole("button", { name: "Cancelar pedido completo" });
  await user.click(
    screen.getByRole("button", { name: "Continuar este Pedido" }),
  );
  await waitFor(() => expect(pendingReads).toBeGreaterThan(0));
  await screen.findByText(/Existe una Composición pendiente autoritativa/);
  await user.click(
    screen.getByRole("button", { name: "Abrir entrega de este Pedido" }),
  );
  await user.click(
    screen.getByRole("button", { name: "Cancelar pedido completo" }),
  );
  await user.click(
    screen.getByRole("button", { name: "Confirmar cancelación completa" }),
  );
  await screen.findByText(/Resultado incierto/);
  expect(probes.preparation.mock.lastCall![0].isOrderBlocked(reference)).toBe(
    true,
  );
  expect(probes.delivery.mock.lastCall![0].ordinaryMutationsBlocked).toBe(true);
  expect(probes.intervention.mock.lastCall![0].isOrderBlocked(reference)).toBe(
    true,
  );
  expect(
    screen.getByRole("button", { name: "Descartar Composición pendiente" }),
  ).toBeDisabled();
  const before = pendingReads;
  const prepSequence = probes.preparation.mock.lastCall![0].refreshSequence;
  const deliverySequence = probes.delivery.mock.lastCall![0].refreshSequence;
  await user.click(
    screen.getByRole("button", {
      name: "Reintentar misma cancelación completa",
    }),
  );
  await screen.findByText("Pedido completamente cancelado");
  await waitFor(() => expect(pendingReads).toBeGreaterThan(before));
  await waitFor(() =>
    expect(
      screen.queryByText(/Existe una Composición pendiente autoritativa/),
    ).not.toBeInTheDocument(),
  );
  expect(probes.preparation.mock.lastCall![0].isOrderBlocked(reference)).toBe(
    true,
  );
  expect(probes.delivery.mock.lastCall![0].ordinaryMutationsBlocked).toBe(true);
  expect(probes.intervention.mock.lastCall![0].isOrderBlocked(reference)).toBe(
    true,
  );
  expect(probes.preparation.mock.lastCall![0].refreshSequence).toBeGreaterThan(
    prepSequence,
  );
  expect(probes.delivery.mock.lastCall![0].refreshSequence).toBeGreaterThan(
    deliverySequence,
  );
  expect(posts).toBe(2);
});
