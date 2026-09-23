import { OrderLookupNetworkError, OrderOperationsProblemError } from "./orderOperationsClient.ts";
import type { OrderOperationsProblemDetails } from "./orderOperationsClient.ts";

export interface HistoryFact { actorIdentityId: string; occurredAtUtc: string }
export interface TerminalOrderHistory {
  orderId: string; operationalReference: string;
  termination: { type: "Closure" | "CompleteCancellation"; occurredAt: string; actorIdentityId: string; pendingCompositionDiscarded: boolean };
  finalContextId: string; finalContextOperationalName: string;
  contextChanges: { sequence: number; previousContextOperationalName: string; newContextOperationalName: string; actorIdentityId: string; occurredAtUtc: string }[];
  incorporations: { id: string; ordinal: number; confirmedAtUtc: string; actorIdentityId: string | null; confirmedContextId: string; confirmedContext: string; contents: TerminalHistoryContent[] }[];
  liquidation: { mode: string; functionalAmount: string; declaredPaymentMedium: string | null; occurredAt: string; actorIdentityId: string } | null;
  closure: { actorIdentityId: string; occurredAtUtc: string } | null;
  completeCancellation: { actorIdentityId: string; occurredAtUtc: string; pendingCompositionDiscarded: boolean; consequences: { incorporationId: string; contentOrdinal: number; directOrPendingQuantity: number; inPreparationQuantity: number; readyQuantity: number; resultingFulfillmentQuantity: number }[] } | null;
}
export interface TerminalHistoryContent {
  contentOrdinal: number; productId: string; productOperationalNameSnapshot: string | null; originalConfirmedQuantity: number;
  appliedPrice: string; instruction: string | null; requiresPreparation: boolean; preparationResponsibilityId: string | null; unavailableProductExceptionApplied: boolean;
  effectiveAppliedPrice: string; priceCorrections: { previousPrice: string; resultingPrice: string; actorIdentityId: string; occurredAtUtc: string }[];
  corrections: { type: string; previousQuantity: number; resultingQuantity: number; actorIdentityId: string; occurredAtUtc: string }[];
  cancellations: { type: string; previousQuantity: number; resultingQuantity: number; actorIdentityId: string; occurredAtUtc: string }[];
  removedByCorrectionQuantity: number; cancelledQuantity: number;
  deliveries: { type: string; quantity: number; resultingDeliveredQuantity: number; actorIdentityId: string; occurredAtUtc: string }[];
  deliveryCorrections: { previousDeliveredQuantity: number; resultingDeliveredQuantity: number; actorIdentityId: string; occurredAtUtc: string }[];
  effectiveDeliveredQuantity: number;
  preparationHistory: { type: string; quantity: number; resultingTotalQuantity: number; resultingPendingQuantity: number; resultingInPreparationQuantity: number; resultingReadyQuantity: number; actorIdentityId: string; occurredAtUtc: string }[];
}
export class TerminalHistoryContractError extends Error {}
function obj(value: unknown, path: string): Record<string, unknown> {
  if (typeof value !== "object" || value === null || Array.isArray(value)) throw new TerminalHistoryContractError(`Invalid ${path}`);
  return value as Record<string, unknown>;
}
function check(value: unknown, path = "history"): asserts value is TerminalOrderHistory {
  const h = obj(value, path);
  const str = (v: unknown) => typeof v === "string";
  const list = (v: unknown, p: string) => { if (!Array.isArray(v)) throw new TerminalHistoryContractError(`Invalid ${p}`); };
  for (const key of ["orderId", "operationalReference", "finalContextId", "finalContextOperationalName"]) if (!str(h[key])) throw new TerminalHistoryContractError(`Invalid ${key}`);
  const term = obj(h.termination, "termination"); if (!(["Closure", "CompleteCancellation"].includes(String(term.type))) || !str(term.actorIdentityId) || !str(term.occurredAt)) throw new TerminalHistoryContractError("Invalid termination");
  list(h.contextChanges, "contextChanges"); list(h.incorporations, "incorporations");
  for (const [i, raw] of (h.incorporations as unknown[]).entries()) {
    const inc = obj(raw, `incorporations.${i}`); list(inc.contents, `incorporations.${i}.contents`);
    for (const [j, rawContent] of (inc.contents as unknown[]).entries()) {
      const c = obj(rawContent, `contents.${j}`);
      if (!(typeof c.productOperationalNameSnapshot === "string" || c.productOperationalNameSnapshot === null)) throw new TerminalHistoryContractError("Invalid productOperationalNameSnapshot");
      for (const k of ["priceCorrections", "corrections", "cancellations", "deliveries", "deliveryCorrections", "preparationHistory"]) list(c[k], k);
    }
  }
  for (const key of ["closure", "completeCancellation", "liquidation"]) if (!(h[key] === null || (typeof h[key] === "object" && !Array.isArray(h[key])))) throw new TerminalHistoryContractError(`Invalid ${key}`);
  const requireFields = (record: Record<string, unknown>, fields: string[], path: string) => { for (const field of fields) if (!(field in record)) throw new TerminalHistoryContractError(`Missing ${path}.${field}`); };
  requireFields(h, ["orderId", "operationalReference", "termination", "finalContextId", "finalContextOperationalName", "contextChanges", "incorporations", "liquidation", "closure", "completeCancellation"], path);
  for (const [i, raw] of (h.contextChanges as unknown[]).entries()) requireFields(obj(raw, `contextChanges.${i}`), ["sequence", "previousContextId", "previousContextOperationalName", "newContextId", "newContextOperationalName", "actorIdentityId", "occurredAtUtc"], `contextChanges.${i}`);
  for (const [i, raw] of (h.incorporations as unknown[]).entries()) {
    const p = `incorporations.${i}`; const inc = obj(raw, p);
    requireFields(inc, ["id", "ordinal", "confirmedAtUtc", "actorIdentityId", "confirmedContextId", "confirmedContext", "contents"], p);
    for (const [j, rawContent] of (inc.contents as unknown[]).entries()) {
      const q = `${p}.contents.${j}`; const c = obj(rawContent, q);
      requireFields(c, ["contentOrdinal", "productId", "originalConfirmedQuantity", "appliedPrice", "instruction", "requiresPreparation", "preparationResponsibilityId", "unavailableProductExceptionApplied", "effectiveAppliedPrice", "priceCorrections", "corrections", "cancellations", "removedByCorrectionQuantity", "cancelledQuantity", "deliveries", "deliveryCorrections", "effectiveDeliveredQuantity", "preparationHistory"], q);
      for (const [k, fields] of Object.entries({ priceCorrections:["previousPrice","resultingPrice","actorIdentityId","occurredAtUtc"], corrections:["type","previousQuantity","resultingQuantity","actorIdentityId","occurredAtUtc"], cancellations:["type","previousQuantity","resultingQuantity","actorIdentityId","occurredAtUtc"], deliveries:["type","quantity","resultingDeliveredQuantity","actorIdentityId","occurredAtUtc"], deliveryCorrections:["previousDeliveredQuantity","resultingDeliveredQuantity","actorIdentityId","occurredAtUtc"], preparationHistory:["type","quantity","resultingTotalQuantity","resultingPendingQuantity","resultingInPreparationQuantity","resultingReadyQuantity","actorIdentityId","occurredAtUtc"] })) for (const [n, fact] of (c[k] as unknown[]).entries()) requireFields(obj(fact, `${q}.${k}.${n}`), fields, `${q}.${k}.${n}`);
    }
  }
  requireFields(term, ["type","id","occurredAt","actorIdentityId","pendingCompositionDiscarded"], "termination");
  if (h.liquidation !== null) requireFields(obj(h.liquidation,"liquidation"), ["mode","functionalAmount","declaredPaymentMedium","occurredAt","actorIdentityId"], "liquidation");
  if (h.closure !== null) requireFields(obj(h.closure,"closure"), ["actorIdentityId","occurredAtUtc"], "closure");
  if (h.completeCancellation !== null) { const c = obj(h.completeCancellation,"completeCancellation"); requireFields(c,["actorIdentityId","occurredAtUtc","pendingCompositionDiscarded","consequences"],"completeCancellation"); for (const [i, fact] of (c.consequences as unknown[]).entries()) requireFields(obj(fact,`consequences.${i}`),["incorporationId","contentOrdinal","directOrPendingQuantity","inPreparationQuantity","readyQuantity","resultingFulfillmentQuantity"],`consequences.${i}`); }
}
export async function getTerminalOrderHistory(reference: string): Promise<TerminalOrderHistory> {
  let response: Response;
  try { response = await fetch(`/api/order-operations/order-history/${encodeURIComponent(reference)}`, { credentials: "same-origin" }); }
  catch (error) { throw new OrderLookupNetworkError({ cause: error }); }
  if (!response.ok) {
    let problem: OrderOperationsProblemDetails = { status: response.status };
    if ((response.headers.get("content-type") ?? "").includes("application/problem+json")) problem = { ...await response.json() as OrderOperationsProblemDetails, status: response.status };
    throw new OrderOperationsProblemError(problem);
  }
  const payload: unknown = await response.json(); check(payload); return payload;
}
