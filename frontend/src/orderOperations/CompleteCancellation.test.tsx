import { render, screen, waitFor } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { beforeEach, expect, it, vi } from "vitest";
import { discardAntiforgeryToken } from "../identity/sessionClient.ts";
import { OrderLookup } from "./OrderLookup.tsx";
import type { OrderResponse } from "./orderOperationsClient.ts";
import type { CompleteCancellationEvaluation } from "./completeCancellationClient.ts";

const reference = "01991e32-2a00-7000-8000-000000000001";
const endpoint = `/api/orders/${reference}/complete-cancellation`;
const fetchMock = vi.fn<typeof fetch>();
const mutation = vi.fn<typeof fetch>();
const unauthorized = vi.fn();
const state = vi.fn();
const busy = vi.fn();
let order: OrderResponse;
let evaluation: CompleteCancellationEvaluation;
let failRead = false;
let evaluationStatus = 200;
const json = (value: unknown, status = 200) =>
  new Response(JSON.stringify(value), {
    status,
    headers: { "Content-Type": "application/json" },
  });
const problem = (status: number, code = "forbidden") =>
  new Response(
    JSON.stringify({ code: `order_operations.complete_cancellation.${code}` }),
    { status, headers: { "Content-Type": "application/problem+json" } },
  );
function finish() {
  order = {
    ...order,
    isLiquidationEligible: false,
    liquidationBlockers: ["order_completely_cancelled"],
  };
  evaluation = {
    ...evaluation,
    isEligible: false,
    isTerminal: true,
    isCompletelyCancelled: true,
    hasPendingComposition: false,
    remainingFulfillmentQuantity: 0,
    requiresOperationalIntervention: false,
    cancellationId: "cancel",
    cancelledAt: "2026-09-11T10:00:00Z",
    blockers: ["already_completely_cancelled"],
    consequences: [],
  };
}
function result() {
  return json({
    orderId: reference,
    cancellationId: "cancel",
    occurredAt: "2026-09-11T10:00:00Z",
    isCompletelyCancelled: true,
    pendingCompositionDiscarded: true,
    consequences: [],
  });
}
async function open(identityId: string | undefined = "base-only") {
  const user = userEvent.setup();
  render(
    <OrderLookup
      products={[]}
      activeOperationalReference={null}
      onContinueOrder={vi.fn()}
      identityId={identityId}
      onUnauthorized={unauthorized}
      onOrderState={state}
      onEndingBusy={busy}
    />,
  );
  await user.type(screen.getByLabelText("Referencia operacional"), reference);
  await user.click(screen.getByRole("button", { name: "Buscar Pedido" }));
  await waitFor(() =>
    expect(screen.getByRole("button", { name: "Buscar Pedido" })).toBeEnabled(),
  );
  return user;
}
async function confirm(user: ReturnType<typeof userEvent.setup>) {
  await user.click(
    screen.getByRole("button", { name: "Cancelar pedido completo" }),
  );
  await user.click(
    screen.getByRole("button", { name: "Confirmar cancelación completa" }),
  );
}
beforeEach(() => {
  vi.clearAllMocks();
  mutation.mockReset();
  discardAntiforgeryToken();
  failRead = false;
  evaluationStatus = 200;
  order = {
    operationalReference: reference,
    context: "Mesa",
    incorporations: [
      { id: "inc", ordinal: 1, confirmedAt: "2026-09-10T00:00:00Z", items: [] },
    ],
    functionalAmount: "0",
    isLiquidationEligible: true,
    liquidationBlockers: [],
    isLiquidated: false,
    isFrozen: false,
    liquidatedAmount: null,
    liquidationMode: null,
    declaredPaymentMedium: null,
    isClosureEligible: false,
    isClosed: false,
    closedAt: null,
  };
  evaluation = {
    orderId: reference,
    isTerminal: false,
    isCompletelyCancelled: false,
    cancellationId: null,
    cancelledAt: null,
    hasEffectiveDelivery: false,
    hasPendingComposition: false,
    remainingFulfillmentQuantity: 4,
    requiresOperationalIntervention: false,
    isEligible: true,
    blockers: [],
    consequences: [
      {
        incorporationId: "inc",
        contentOrdinal: 1,
        directOrPendingQuantity: 4,
        inPreparationQuantity: 0,
        readyQuantity: 0,
        resultingFulfillmentQuantity: 0,
      },
    ],
  };
  fetchMock.mockImplementation(async (url, init) => {
    if (url === "/api/security/antiforgery")
      return json({ requestToken: "csrf-original" });
    if (init?.method === "POST") return mutation(url, init);
    if (failRead) throw new TypeError("offline read");
    if (url === endpoint)
      return evaluationStatus === 200
        ? json(evaluation)
        : problem(evaluationStatus);
    return json(order);
  });
  vi.stubGlobal("fetch", fetchMock);
});

it("offers authoritative Direct/Pending-only cancellation with base authority and a distinct confirmation", async () => {
  const user = await open();
  expect(screen.getByRole("button", { name: "Liquidar" })).toBeEnabled();
  expect(
    screen.getByRole("button", { name: "Cancelar pedido completo" }),
  ).toBeEnabled();
  expect(screen.getByText(/OrderOperationsAndBasicClosure/)).toBeVisible();
  expect(
    screen.queryByRole("region", { name: "Confirmar cancelación completa" }),
  ).not.toBeInTheDocument();
  await user.click(
    screen.getByRole("button", { name: "Cancelar pedido completo" }),
  );
  const summary = screen.getByRole("region", {
    name: "Confirmar cancelación completa",
  });
  expect(summary).toHaveTextContent(
    "Toda la obligación de cumplimiento actual restante dejará de ser requerida",
  );
  expect(summary).toHaveTextContent("terminará excepcionalmente");
  expect(summary).toHaveTextContent("No se creará Liquidación ni Cierre");
  expect(summary).toHaveTextContent("Obligación restante informada: 4");
  expect(summary).not.toHaveTextContent(
    /refund|reembolso|desperdicio|inventario|error de preparación|PreparationEnablement/i,
  );
  expect(mutation).not.toHaveBeenCalled();
  expect(fetchMock.mock.calls.map(([url]) => url)).not.toContain(
    "/api/identity-sessions/current/preparation-destinations",
  );
});

it("shows the effective Delivery blocker without automatic correction", async () => {
  evaluation = {
    ...evaluation,
    isEligible: false,
    hasEffectiveDelivery: true,
    blockers: ["effective_delivery"],
  };
  await open();
  expect(
    screen.getByText(/Hay cantidad efectivamente entregada/),
  ).toBeVisible();
  expect(
    screen.getByRole("button", { name: "Cancelar pedido completo" }),
  ).toBeDisabled();
  expect(
    screen.queryByRole("button", { name: /corregir entrega/i }),
  ).not.toBeInTheDocument();
  expect(mutation).not.toHaveBeenCalled();
});

it("uses current evaluation after historical Delivery was corrected to effective zero", async () => {
  evaluation.hasEffectiveDelivery = false;
  await open();
  expect(
    screen.getByRole("button", { name: "Cancelar pedido completo" }),
  ).toBeEnabled();
});

it("discloses PendingComposition discard without a separate Discard command", async () => {
  evaluation.hasPendingComposition = true;
  const user = await open();
  await user.click(
    screen.getByRole("button", { name: "Cancelar pedido completo" }),
  );
  expect(
    screen.getByText(/Composición pendiente será descartada/),
  ).toBeVisible();
  expect(
    screen.queryByRole("button", { name: /descartar/i }),
  ).not.toBeInTheDocument();
  expect(mutation).not.toHaveBeenCalled();
});

it.each(["inPreparationQuantity", "readyQuantity"] as const)(
  "represents authoritative intervention for real %s work without Preparation authority",
  async (stage) => {
    evaluation.requiresOperationalIntervention = true;
    evaluation.consequences[0][stage] = 2;
    const user = await open();
    await user.click(
      screen.getByRole("button", { name: "Cancelar pedido completo" }),
    );
    const surface = screen.getByRole("region", {
      name: "Cancelación completa excepcional",
    });
    expect(surface).toHaveTextContent("OperationalIntervention");
    expect(surface).toHaveTextContent(
      "trabajo realizado permanecerá registrado en la Historia",
    );
    expect(surface).not.toHaveTextContent(
      /PreparationEnablement|Responsibility.Preparation|desperdicio|reembolso/,
    );
  },
);

it("does not derive intervention eligibility from local quantities", async () => {
  evaluation.requiresOperationalIntervention = true;
  evaluation.consequences = [];
  await open();
  expect(screen.getByText(/La evaluación requiere además/)).toBeVisible();
});

it("refreshes authoritative Order/evaluation and shows terminal cancellation without synthetic ending state", async () => {
  evaluation.hasPendingComposition = true;
  mutation.mockImplementation(async () => {
    finish();
    return result();
  });
  const user = await open();
  await confirm(user);
  expect(
    await screen.findByText("Pedido completamente cancelado"),
  ).toBeVisible();
  expect(
    screen.getByText(/Composición pendiente fue descartada/),
  ).toBeVisible();
  expect(
    screen.queryByRole("button", {
      name: /Liquidar|Cerrar Pedido|Continuar este Pedido|Cancelar pedido completo/,
    }),
  ).not.toBeInTheDocument();
  expect(
    screen.queryByText(
      "Pedido congelado. Las operaciones ordinarias ya no están disponibles.",
    ),
  ).not.toBeInTheDocument();
  expect(
    screen.getByRole("article", { name: "Incorporación 1" }),
  ).toBeVisible();
  expect(state).toHaveBeenLastCalledWith(order);
  expect(fetchMock.mock.calls.filter(([url]) => url === endpoint)).toHaveLength(
    3,
  );
  expect(busy).toHaveBeenLastCalledWith(reference, false);
  const [url, init] = mutation.mock.calls[0];
  expect(url).toBe(endpoint);
  expect(init).not.toHaveProperty("body");
  expect(new Headers(init?.headers).get("Idempotency-Key")).toMatch(
    /^[\da-f]{8}-[\da-f]{4}-4[\da-f]{3}-[89ab][\da-f]{3}-[\da-f]{12}$/,
  );
});

it.each(["already_completely_cancelled", "already_closed", "order_frozen"])(
  "shows terminal evaluation %s",
  async (blocker) => {
    evaluation.isTerminal = true;
    evaluation.isEligible = false;
    evaluation.blockers = [blocker];
    if (blocker === "already_completely_cancelled") finish();
    await open();
    expect(
      screen.queryByRole("button", { name: "Cancelar pedido completo" }),
    ).not.toBeInTheDocument();
  },
);

it("allows all F already zero when the evaluation permits a meaningful terminal command", async () => {
  evaluation.remainingFulfillmentQuantity = 0;
  evaluation.consequences = [];
  mutation.mockImplementation(async () => {
    finish();
    return result();
  });
  const user = await open();
  await confirm(user);
  expect(
    await screen.findByText("Pedido completamente cancelado"),
  ).toBeVisible();
  expect(mutation).toHaveBeenCalledOnce();
});

it.each(["network", "408", "500", "malformed"])(
  "preserves exact intent including CSRF across %s uncertainty and blocks terminal conflicts",
  async (failure) => {
    mutation
      .mockImplementationOnce(async () => {
        if (failure === "network") throw new TypeError("offline");
        return failure === "malformed" ? json({}) : problem(Number(failure));
      })
      .mockImplementationOnce(async () => {
        finish();
        return result();
      });
    const user = await open();
    await confirm(user);
    await screen.findByText(/Resultado incierto/);
    expect(screen.getByRole("button", { name: "Liquidar" })).toBeDisabled();
    expect(
      screen.getByRole("button", {
        name: "Registrar cobro gestionado externamente",
      }),
    ).toBeDisabled();
    expect(
      screen.getByRole("button", { name: "Cancelar pedido completo" }),
    ).toBeDisabled();
    expect(
      screen.getByRole("button", { name: "Buscar Pedido" }),
    ).toBeDisabled();
    expect(
      screen.getByRole("button", { name: "Continuar este Pedido" }),
    ).toBeDisabled();
    discardAntiforgeryToken();
    await user.click(
      screen.getByRole("button", {
        name: "Reintentar misma cancelación completa",
      }),
    );
    await screen.findByText("Pedido completamente cancelado");
    expect(mutation.mock.calls[1]).toEqual(mutation.mock.calls[0]);
    expect(
      fetchMock.mock.calls.filter(
        ([url]) => url === "/api/security/antiforgery",
      ),
    ).toHaveLength(1);
  },
);

it.each([403, 404, 409])(
  "refreshes authoritative eligibility after HTTP %s",
  async (status) => {
    mutation.mockImplementation(async () => {
      evaluation = {
        ...evaluation,
        isEligible: false,
        hasEffectiveDelivery: true,
        blockers: ["effective_delivery"],
      };
      return problem(
        status,
        status === 403
          ? "operational_intervention_required"
          : "effective_delivery",
      );
    });
    const user = await open();
    await confirm(user);
    await waitFor(() =>
      expect(busy).toHaveBeenLastCalledWith(reference, false),
    );
    expect(
      screen.getByRole("button", { name: "Cancelar pedido completo" }),
    ).toBeDisabled();
    expect(
      screen.queryByRole("region", { name: "Confirmar cancelación completa" }),
    ).not.toBeInTheDocument();
    expect(unauthorized).not.toHaveBeenCalled();
    if (status === 403)
      expect(
        screen.getByText(/Identity necesita además autorización/),
      ).toBeVisible();
  },
);

it("uses the existing 401 session reset", async () => {
  mutation.mockResolvedValue(problem(401));
  const user = await open();
  await confirm(user);
  await waitFor(() => expect(unauthorized).toHaveBeenCalledOnce());
  expect(
    screen.queryByRole("button", { name: /Reintentar misma/ }),
  ).not.toBeInTheDocument();
});

it.each([401, 403, 404])(
  "does not offer cancellation when evaluation returns %s",
  async (status) => {
    evaluationStatus = status;
    await open();
    expect(
      screen.queryByRole("button", { name: "Cancelar pedido completo" }),
    ).not.toBeInTheDocument();
    if (status === 401) expect(unauthorized).toHaveBeenCalledOnce();
    expect(mutation).not.toHaveBeenCalled();
  },
);

it("keeps mutations locked when refresh fails and never manufactures terminal state from the command", async () => {
  mutation.mockImplementation(async () => {
    failRead = true;
    return result();
  });
  const user = await open();
  await confirm(user);
  await screen.findByText(/No se pudo actualizar el Pedido/);
  expect(
    screen.queryByText("Pedido completamente cancelado"),
  ).not.toBeInTheDocument();
  expect(screen.getByRole("button", { name: "Liquidar" })).toBeDisabled();
  failRead = false;
  finish();
  await user.click(
    screen.getByRole("button", { name: "Actualizar Estado del Pedido" }),
  );
  await screen.findByText("Pedido completamente cancelado");
  expect(mutation).toHaveBeenCalledOnce();
});

it("blocks cancellation while a Liquidation intent is uncertain", async () => {
  mutation.mockRejectedValue(new TypeError("offline"));
  const user = await open();
  await user.type(screen.getByLabelText("Medio de pago declarado"), "Efectivo");
  await user.click(screen.getByRole("button", { name: "Liquidar" }));
  await screen.findByText(/Resultado incierto/);
  expect(
    screen.getByRole("button", { name: "Cancelar pedido completo" }),
  ).toBeDisabled();
});

it("invalidates a previously opened confirmation when a refreshed evaluation blocks cancellation", async () => {
  const user = await open();
  await user.click(
    screen.getByRole("button", { name: "Cancelar pedido completo" }),
  );
  evaluation = {
    ...evaluation,
    isEligible: false,
    blockers: ["effective_delivery"],
    hasEffectiveDelivery: true,
  };
  await user.click(screen.getByRole("button", { name: "Buscar Pedido" }));
  await waitFor(() =>
    expect(
      screen.getByRole("button", { name: "Cancelar pedido completo" }),
    ).toBeDisabled(),
  );
  expect(
    screen.queryByRole("button", { name: "Confirmar cancelación completa" }),
  ).not.toBeInTheDocument();
  expect(mutation).not.toHaveBeenCalled();
});
