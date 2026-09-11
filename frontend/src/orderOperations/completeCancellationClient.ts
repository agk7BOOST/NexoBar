import {
  OrderOperationsNetworkError,
  OrderOperationsProblemError,
} from "./orderOperationsClient.ts";
import type { OrderEndingIntent } from "./orderEndingClient.ts";

export interface CancellationConsequence {
  incorporationId: string;
  contentOrdinal: number;
  directOrPendingQuantity: number;
  inPreparationQuantity: number;
  readyQuantity: number;
  resultingFulfillmentQuantity: number;
}

export interface CompleteCancellationEvaluation {
  orderId: string;
  isTerminal: boolean;
  isCompletelyCancelled: boolean;
  cancellationId: string | null;
  cancelledAt: string | null;
  hasEffectiveDelivery: boolean;
  hasPendingComposition: boolean;
  remainingFulfillmentQuantity: number | null;
  requiresOperationalIntervention: boolean | null;
  isEligible: boolean;
  blockers: string[];
  consequences: CancellationConsequence[];
}

export interface CompleteCancellationIntent extends Pick<
  OrderEndingIntent,
  "endpoint" | "body" | "idempotencyKey"
> {
  readonly orderId: string;
  readonly antiforgeryToken: string;
}

const endpoint = (orderId: string) =>
  `/api/orders/${encodeURIComponent(orderId)}/complete-cancellation`;

export function createCompleteCancellationIntent(
  orderId: string,
  antiforgeryToken: string,
): CompleteCancellationIntent {
  return Object.freeze({
    orderId,
    endpoint: endpoint(orderId),
    body: undefined,
    idempotencyKey: crypto.randomUUID(),
    antiforgeryToken,
  });
}

async function requireSuccess(response: Response) {
  if (response.status === 408 || response.status >= 500)
    throw new OrderOperationsNetworkError();
  if (response.ok) return;
  let problem = {};
  try {
    if (
      response.headers.get("content-type")?.includes("application/problem+json")
    )
      problem = await response.json();
  } catch {
    /* HTTP status remains authoritative. */
  }
  throw new OrderOperationsProblemError({
    ...problem,
    status: response.status,
  });
}

export async function evaluateCompleteCancellation(
  orderId: string,
): Promise<CompleteCancellationEvaluation> {
  const response = await fetch(endpoint(orderId), {
    credentials: "same-origin",
  });
  await requireSuccess(response);
  const value = (await response.json()) as CompleteCancellationEvaluation;
  // Validate the read shape, never reconstruct the cancellation plan.
  if (
    value.orderId !== orderId ||
    ![
      value.isEligible,
      value.isTerminal,
      value.isCompletelyCancelled,
      value.hasEffectiveDelivery,
      value.hasPendingComposition,
    ].every((v) => typeof v === "boolean") ||
    !(
      value.requiresOperationalIntervention === null ||
      typeof value.requiresOperationalIntervention === "boolean"
    ) ||
    !(
      value.remainingFulfillmentQuantity === null ||
      Number.isSafeInteger(value.remainingFulfillmentQuantity)
    ) ||
    !Array.isArray(value.blockers) ||
    !value.blockers.every((v) => typeof v === "string") ||
    !Array.isArray(value.consequences) ||
    !value.consequences.every(
      (c) =>
        typeof c.incorporationId === "string" &&
        [
          c.contentOrdinal,
          c.directOrPendingQuantity,
          c.inPreparationQuantity,
          c.readyQuantity,
          c.resultingFulfillmentQuantity,
        ].every(Number.isSafeInteger),
    )
  ) {
    throw new OrderOperationsNetworkError();
  }
  return value;
}

export async function sendCompleteCancellationIntent(
  intent: CompleteCancellationIntent,
): Promise<{ pendingCompositionDiscarded: boolean }> {
  const response = await fetch(intent.endpoint, {
    method: "POST",
    credentials: "same-origin",
    headers: {
      "Idempotency-Key": intent.idempotencyKey,
      "X-NexoBar-CSRF": intent.antiforgeryToken,
    },
  });
  await requireSuccess(response);
  const value = await response.json();
  if (
    value.orderId !== intent.orderId ||
    value.isCompletelyCancelled !== true ||
    typeof value.cancellationId !== "string" ||
    typeof value.occurredAt !== "string" ||
    typeof value.pendingCompositionDiscarded !== "boolean" ||
    !Array.isArray(value.consequences)
  )
    throw new OrderOperationsNetworkError();
  return value;
}

export const cancellationBlockers: Record<string, string> = {
  effective_delivery:
    "Hay cantidad efectivamente entregada. No se puede cancelar el pedido completo.",
  already_completely_cancelled: "El pedido ya está completamente cancelado.",
  already_closed: "El pedido ya está cerrado.",
  order_frozen: "El pedido está congelado por su Liquidación.",
  state_inconsistent:
    "El Estado del pedido es inconsistente. No se puede cancelar el pedido completo.",
};
