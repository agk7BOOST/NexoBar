export interface OrderDeliveryContent {
  confirmedQuantity?: number;
  removedByCorrectionQuantity?: number;
  currentFulfillmentQuantity?: number;
  incorporationId: string;
  incorporationOrdinal: number;
  contentOrdinal: number;
  productId: string;
  productOperationalName: string;
  instruction: string | null;
  totalQuantity: number;
  requiresPreparationAtConfirmation: boolean;
  readyQuantity: number | null;
  deliveredQuantity: number;
  deliverableQuantity: number;
  remainingQuantity: number;
}

export async function correctContentQuantity(
  command: CorrectDeliveryCommand,
): Promise<{ incorporationId: string; contentOrdinal: number }> {
  const response = await fetch(
    `/api/order-operations/orders/${encodeURIComponent(command.orderId)}/incorporations/${encodeURIComponent(command.incorporationId)}/contents/${command.contentOrdinal}/correct-content-quantity`,
    {
      method: "POST",
      credentials: "same-origin",
      headers: {
        "Content-Type": "application/json",
        "Idempotency-Key": command.idempotencyKey,
        "X-NexoBar-CSRF": command.antiforgeryToken,
      },
      body: JSON.stringify({ quantity: command.quantity }),
    },
  );
  if (!response.ok) {
    if ([401, 403, 408].includes(response.status) || response.status >= 500)
      throw new DeliveryProblemError(response.status);
    throw new DeliveryProblemError(
      response.status,
      await readProblem(response),
    );
  }
  const value: unknown = await response.json();
  if (
    !isRecord(value) ||
    value.orderId !== command.orderId ||
    value.incorporationId !== command.incorporationId ||
    value.contentOrdinal !== command.contentOrdinal ||
    value.correctedQuantity !== command.quantity ||
    !isNonNegativeInteger(value.confirmedQuantity) ||
    !isNonNegativeInteger(value.resultingRemovedByCorrectionQuantity) ||
    !isNonNegativeInteger(value.resultingFulfillmentQuantity)
  )
    throw new Error("Content Correction response was not interpretable.");
  return {
    incorporationId: command.incorporationId,
    contentOrdinal: command.contentOrdinal,
  };
}

export interface OrderDelivery {
  orderId: string;
  operationalReference: string;
  currentContext: string;
  contents: OrderDeliveryContent[];
}

export interface DeliverQuantityResult {
  incorporationId: string;
  contentOrdinal: number;
  historyId: string;
  occurredAt: string;
  deliveredQuantity: number;
}

export interface DeliveryProblemDetails {
  title?: string;
  status?: number;
  detail?: string;
  code?: string;
}

export interface DeliverQuantityCommand {
  incorporationId: string;
  contentOrdinal: number;
  quantity: number;
  idempotencyKey: string;
  antiforgeryToken: string;
}

export class DeliveryProblemError extends Error {
  readonly status: number;
  readonly problem: DeliveryProblemDetails;

  constructor(status: number, problem: DeliveryProblemDetails = {}) {
    super("The Delivery request was rejected.");
    this.name = "DeliveryProblemError";
    this.status = status;
    this.problem = problem;
  }
}

function isRecord(value: unknown): value is Record<string, unknown> {
  return typeof value === "object" && value !== null;
}

function isNonNegativeInteger(value: unknown): value is number {
  return typeof value === "number" && Number.isInteger(value) && value >= 0;
}

async function readProblem(
  response: Response,
): Promise<DeliveryProblemDetails> {
  const contentType = response.headers.get("content-type") ?? "";
  if (!contentType.includes("application/problem+json")) {
    throw new Error("The Delivery error response was not interpretable.");
  }

  const payload: unknown = await response.json();
  if (!isRecord(payload)) {
    throw new Error("The Delivery error response was not interpretable.");
  }

  return payload as DeliveryProblemDetails;
}

function parseContent(value: unknown): OrderDeliveryContent {
  if (
    !isRecord(value) ||
    typeof value.incorporationId !== "string" ||
    !isNonNegativeInteger(value.incorporationOrdinal) ||
    !isNonNegativeInteger(value.contentOrdinal) ||
    typeof value.productId !== "string" ||
    typeof value.productOperationalName !== "string" ||
    !(typeof value.instruction === "string" || value.instruction === null) ||
    !isNonNegativeInteger(value.totalQuantity) ||
    typeof value.requiresPreparationAtConfirmation !== "boolean" ||
    !(
      value.readyQuantity === null || isNonNegativeInteger(value.readyQuantity)
    ) ||
    !isNonNegativeInteger(value.deliveredQuantity) ||
    !isNonNegativeInteger(value.deliverableQuantity) ||
    !isNonNegativeInteger(value.remainingQuantity)
  ) {
    throw new Error("The Delivery read model was not interpretable.");
  }

  return value as unknown as OrderDeliveryContent;
}

async function readOrderDelivery(response: Response): Promise<OrderDelivery> {
  const payload: unknown = await response.json();
  if (
    !isRecord(payload) ||
    typeof payload.orderId !== "string" ||
    typeof payload.operationalReference !== "string" ||
    typeof payload.currentContext !== "string" ||
    !Array.isArray(payload.contents)
  ) {
    throw new Error("The Delivery read model was not interpretable.");
  }

  return {
    orderId: payload.orderId,
    operationalReference: payload.operationalReference,
    currentContext: payload.currentContext,
    contents: payload.contents.map(parseContent),
  };
}

async function readDeliverQuantityResult(
  response: Response,
): Promise<DeliverQuantityResult> {
  const payload: unknown = await response.json();
  if (
    !isRecord(payload) ||
    typeof payload.incorporationId !== "string" ||
    !isNonNegativeInteger(payload.contentOrdinal) ||
    typeof payload.historyId !== "string" ||
    typeof payload.occurredAt !== "string" ||
    !isNonNegativeInteger(payload.deliveredQuantity)
  ) {
    throw new Error("The Delivery success response was not interpretable.");
  }

  return payload as unknown as DeliverQuantityResult;
}

export async function getOrderDelivery(
  operationalReference: string,
): Promise<OrderDelivery> {
  const response = await fetch(
    `/api/order-operations/orders/${encodeURIComponent(operationalReference)}/delivery`,
    { credentials: "same-origin" },
  );
  if (!response.ok) {
    throw new DeliveryProblemError(
      response.status,
      await readProblem(response),
    );
  }

  return readOrderDelivery(response);
}

export async function deliverQuantity(
  command: DeliverQuantityCommand,
): Promise<DeliverQuantityResult> {
  const response = await fetch(
    `/api/order-operations/incorporations/${encodeURIComponent(command.incorporationId)}/contents/${encodeURIComponent(String(command.contentOrdinal))}/deliver`,
    {
      method: "POST",
      credentials: "same-origin",
      headers: {
        "Content-Type": "application/json",
        "Idempotency-Key": command.idempotencyKey,
        "X-NexoBar-CSRF": command.antiforgeryToken,
      },
      body: JSON.stringify({ quantity: command.quantity }),
    },
  );
  if (!response.ok) {
    throw new DeliveryProblemError(
      response.status,
      await readProblem(response),
    );
  }

  return readDeliverQuantityResult(response);
}

export interface CorrectDeliveryCommand extends DeliverQuantityCommand {
  orderId: string;
}
export interface CorrectDeliveryResult {
  orderId: string;
  incorporationId: string;
  contentOrdinal: number;
  historyId: string;
  correctedQuantity: number;
  previousDeliveredQuantity: number;
  resultingDeliveredQuantity: number;
  occurredAt: string;
}
export async function correctDelivery(
  command: CorrectDeliveryCommand,
): Promise<CorrectDeliveryResult> {
  const response = await fetch(
    `/api/order-operations/orders/${encodeURIComponent(command.orderId)}/incorporations/${encodeURIComponent(command.incorporationId)}/contents/${encodeURIComponent(String(command.contentOrdinal))}/correct-delivery`,
    {
      method: "POST",
      credentials: "same-origin",
      headers: {
        "Content-Type": "application/json",
        "Idempotency-Key": command.idempotencyKey,
        "X-NexoBar-CSRF": command.antiforgeryToken,
      },
      body: JSON.stringify({ quantity: command.quantity }),
    },
  );
  if (!response.ok) {
    if (response.status === 401 || response.status === 403)
      throw new DeliveryProblemError(response.status);
    throw new DeliveryProblemError(
      response.status,
      await readProblem(response),
    );
  }
  const payload: unknown = await response.json();
  if (
    !isRecord(payload) ||
    payload.orderId !== command.orderId ||
    payload.incorporationId !== command.incorporationId ||
    payload.contentOrdinal !== command.contentOrdinal ||
    typeof payload.historyId !== "string" ||
    typeof payload.occurredAt !== "string" ||
    payload.correctedQuantity !== command.quantity ||
    !isNonNegativeInteger(payload.previousDeliveredQuantity) ||
    !isNonNegativeInteger(payload.resultingDeliveredQuantity)
  ) {
    throw new Error("The Delivery Correction response was not interpretable.");
  }
  return payload as unknown as CorrectDeliveryResult;
}
