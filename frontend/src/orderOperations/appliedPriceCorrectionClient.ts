import { OrderOperationsNetworkError, OrderOperationsProblemError } from "./orderOperationsClient.ts";
import type { OrderEndingIntent } from "./orderEndingClient.ts";

export interface PriceTarget {
  readonly orderId: string;
  readonly incorporationId: string;
  readonly contentOrdinal: number;
}
export interface AppliedPriceEvaluation extends PriceTarget {
  appliedPrice: string;
  effectiveAppliedPrice: string;
  currentCatalogPrice: string | null;
  isCorrectionAvailable: boolean;
  blockers: string[];
}
export interface AppliedPriceIntent extends PriceTarget, Pick<OrderEndingIntent, "endpoint" | "body" | "idempotencyKey"> {
  readonly antiforgeryToken: string;
}
const path = (target: PriceTarget) =>
  `/api/order-operations/orders/${encodeURIComponent(target.orderId)}/incorporations/${encodeURIComponent(target.incorporationId)}/contents/${target.contentOrdinal}`;

export function createAppliedPriceIntent(target: PriceTarget, antiforgeryToken: string): AppliedPriceIntent {
  return Object.freeze({
    orderId: target.orderId, incorporationId: target.incorporationId, contentOrdinal: target.contentOrdinal,
    endpoint: `${path(target)}/apply-current-catalog-price`, body: "{}",
    idempotencyKey: crypto.randomUUID(), antiforgeryToken,
  });
}
async function requireSuccess(response: Response) {
  if (response.status === 408 || response.status >= 500) throw new OrderOperationsNetworkError();
  if (response.ok) return;
  let problem = {};
  try {
    if (response.headers.get("content-type")?.includes("application/problem+json")) problem = await response.json();
  } catch { /* Preserve the authoritative HTTP status. */ }
  throw new OrderOperationsProblemError({ ...problem, status: response.status });
}
function matches(value: PriceTarget, target: PriceTarget) {
  return value?.orderId === target.orderId && value.incorporationId === target.incorporationId && value.contentOrdinal === target.contentOrdinal;
}
export async function evaluateAppliedPrice(target: PriceTarget): Promise<AppliedPriceEvaluation> {
  const response = await fetch(`${path(target)}/applied-price-correction`, { credentials: "same-origin" });
  await requireSuccess(response);
  const value = await response.json() as AppliedPriceEvaluation;
  if (!matches(value, target) || typeof value.appliedPrice !== "string" || typeof value.effectiveAppliedPrice !== "string" ||
    !(value.currentCatalogPrice === null || typeof value.currentCatalogPrice === "string") ||
    typeof value.isCorrectionAvailable !== "boolean" || !Array.isArray(value.blockers) || !value.blockers.every(x => typeof x === "string"))
    throw new OrderOperationsNetworkError();
  return value;
}
export async function sendAppliedPriceIntent(intent: AppliedPriceIntent): Promise<void> {
  const response = await fetch(intent.endpoint, {
    method: "POST", credentials: "same-origin", body: intent.body,
    headers: { "Content-Type": "application/json", "Idempotency-Key": intent.idempotencyKey, "X-NexoBar-CSRF": intent.antiforgeryToken },
  });
  await requireSuccess(response);
  const value = await response.json();
  if (!matches(value, intent) || typeof value.historyId !== "string" || typeof value.occurredAt !== "string" ||
    typeof value.previousEffectiveAppliedPrice !== "string" || typeof value.resultingEffectiveAppliedPrice !== "string")
    throw new OrderOperationsNetworkError();
}
export const priceBlockers: Record<string, string> = {
  no_correction_to_apply: "No hay una corrección de precio pendiente para aplicar.",
  order_frozen: "El Pedido está congelado por su Liquidación.",
  order_closed: "El Pedido está cerrado.",
  order_completely_cancelled: "El Pedido está completamente cancelado.",
  product_not_current: "El Producto no tiene un precio vigente válido disponible en Catálogo.",
};
