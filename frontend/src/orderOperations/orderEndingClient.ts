import {
  OrderOperationsNetworkError,
  OrderOperationsProblemError,
} from "./orderOperationsClient.ts";

export type OrderEndingKind = "simple" | "external" | "close";

export interface OrderEndingIntent {
  readonly kind: OrderEndingKind;
  readonly endpoint: string;
  readonly body: string | undefined;
  readonly idempotencyKey: string;
}

export function createOrderEndingIntent(
  reference: string,
  kind: OrderEndingKind,
  medium: string,
): OrderEndingIntent {
  const target = encodeURIComponent(reference);
  return Object.freeze({
    kind,
    endpoint:
      kind === "close"
        ? `/api/orders/${target}/close`
        : `/api/order-operations/orders/${target}/${kind === "simple" ? "liquidate-simple" : "record-external-collection"}`,
    body:
      kind === "simple"
        ? JSON.stringify({ declaredPaymentMedium: medium.trim() })
        : undefined,
    idempotencyKey: crypto.randomUUID(),
  });
}

export async function sendOrderEndingIntent(
  intent: OrderEndingIntent,
  antiforgeryToken: string,
): Promise<string | null> {
  let response: Response;
  try {
    response = await fetch(intent.endpoint, {
      method: "POST",
      credentials: "same-origin",
      headers: {
        "Idempotency-Key": intent.idempotencyKey,
        "X-NexoBar-CSRF": antiforgeryToken,
        ...(intent.body === undefined
          ? {}
          : { "Content-Type": "application/json" }),
      },
      ...(intent.body === undefined ? {} : { body: intent.body }),
    });
  } catch (error) {
    throw new OrderOperationsNetworkError({ cause: error });
  }
  if (response.status === 408 || response.status >= 500) {
    throw new OrderOperationsNetworkError();
  }
  if (!response.ok) {
    let problem = {};
    try {
      if (
        response.headers
          .get("content-type")
          ?.includes("application/problem+json")
      ) {
        problem = await response.json();
      }
    } catch {
      /* HTTP status remains authoritative even with malformed Problem Details. */
    }
    throw new OrderOperationsProblemError({
      ...problem,
      status: response.status,
    });
  }
  if (intent.kind === "close") return null;
  const result: unknown = await response.json();
  if (
    typeof result !== "object" ||
    result === null ||
    !("occurredAt" in result) ||
    typeof result.occurredAt !== "string"
  ) {
    throw new OrderOperationsNetworkError();
  }
  return result.occurredAt;
}
