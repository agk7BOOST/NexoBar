import {
  OrderOperationsNetworkError,
  OrderOperationsProblemError,
  type OrderOperationsProblemDetails,
} from "./orderOperationsClient.ts";
import type { OrderEndingIntent } from "./orderEndingClient.ts";

export interface InterventionLookup {
  readonly incorporationId: string;
  readonly contentOrdinal: number;
}

export interface InterventionTarget extends InterventionLookup {
  orderId: string;
  workId: string;
  productId: string;
  productOperationalName: string;
  instruction: string | null;
  confirmedQuantity: number;
  removedByCorrectionQuantity: number;
  cancelledQuantity: number;
  fulfillmentQuantity: number;
  pendingQuantity: number;
  inPreparationQuantity: number;
  readyQuantity: number;
  totalQuantity: number;
  deliveredQuantity: number;
  isFrozen: boolean;
  intervenableInPreparationQuantity: number;
  intervenableReadyQuantity: number;
}

export type InterventionStage = "in-preparation" | "ready";

// Keep the existing frozen endpoint/body/key intent shape, with the exact target
// and the token used on the first send retained for manual retry.
export interface InterventionIntent
  extends Pick<OrderEndingIntent, "endpoint" | "idempotencyKey">,
    InterventionLookup {
  readonly body: string;
  readonly workId: string;
  readonly stage: InterventionStage;
  readonly quantity: number;
  readonly antiforgeryToken: string;
}

export function createInterventionIntent(
  target: InterventionTarget,
  stage: InterventionStage,
  quantity: number,
  antiforgeryToken: string,
): InterventionIntent {
  return Object.freeze({
    incorporationId: target.incorporationId,
    contentOrdinal: target.contentOrdinal,
    workId: target.workId,
    stage,
    quantity,
    endpoint: `/api/order-operations/intervention/work/${encodeURIComponent(target.workId)}/${stage}`,
    body: JSON.stringify({ quantity }),
    idempotencyKey: crypto.randomUUID(),
    antiforgeryToken,
  });
}

async function readProblem(response: Response): Promise<OrderOperationsProblemDetails> {
  let problem: OrderOperationsProblemDetails = {};
  try {
    if (response.headers.get("content-type")?.includes("application/problem+json")) {
      problem = await response.json() as OrderOperationsProblemDetails;
    }
  } catch { /* Preserve the authoritative HTTP status. */ }
  return { ...problem, status: response.status };
}

function isTarget(value: unknown, lookup: InterventionLookup): value is InterventionTarget {
  if (typeof value !== "object" || value === null) return false;
  const target = value as InterventionTarget;
  const quantities = [target.confirmedQuantity, target.removedByCorrectionQuantity,
    target.cancelledQuantity, target.fulfillmentQuantity, target.pendingQuantity,
    target.inPreparationQuantity, target.readyQuantity, target.totalQuantity,
    target.deliveredQuantity, target.intervenableInPreparationQuantity, target.intervenableReadyQuantity];
  return target.incorporationId === lookup.incorporationId && target.contentOrdinal === lookup.contentOrdinal &&
    typeof target.orderId === "string" && typeof target.workId === "string" &&
    typeof target.productId === "string" && typeof target.productOperationalName === "string" &&
    (target.instruction === null || typeof target.instruction === "string") && typeof target.isFrozen === "boolean" &&
    quantities.every(q => Number.isInteger(q) && q >= 0 && q <= 2147483647) &&
    target.confirmedQuantity - target.removedByCorrectionQuantity - target.cancelledQuantity === target.fulfillmentQuantity &&
    target.totalQuantity === target.fulfillmentQuantity &&
    target.pendingQuantity + target.inPreparationQuantity + target.readyQuantity === target.totalQuantity &&
    target.deliveredQuantity <= target.readyQuantity;
}

export async function getInterventionTarget(lookup: InterventionLookup): Promise<InterventionTarget> {
  let response: Response;
  try {
    response = await fetch(
      `/api/order-operations/intervention/incorporations/${encodeURIComponent(lookup.incorporationId)}/contents/${lookup.contentOrdinal}`,
      { credentials: "same-origin" },
    );
  } catch (cause) { throw new OrderOperationsNetworkError({ cause }); }
  if (!response.ok) throw new OrderOperationsProblemError(await readProblem(response));
  const target: unknown = await response.json();
  if (!isTarget(target, lookup)) throw new OrderOperationsNetworkError();
  return target;
}

export async function sendInterventionIntent(intent: InterventionIntent): Promise<void> {
  let response: Response;
  try {
    response = await fetch(intent.endpoint, {
      method: "POST",
      credentials: "same-origin",
      headers: {
        "Content-Type": "application/json",
        "Idempotency-Key": intent.idempotencyKey,
        "X-NexoBar-CSRF": intent.antiforgeryToken,
      },
      body: intent.body,
    });
  } catch (cause) { throw new OrderOperationsNetworkError({ cause }); }
  if (!response.ok) {
    const problem = await readProblem(response);
    if (response.status === 408 || (response.status >= 500 && problem.code !== "order_operations.intervention.state_inconsistent")) {
      throw new OrderOperationsNetworkError();
    }
    throw new OrderOperationsProblemError(problem);
  }
  // A successful HTTP result is followed by the narrow authoritative read.
  // Never apply the command's historical result to current State.
}
