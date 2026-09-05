export interface FirstConfirmationItemRequest {
  productId: string;
  quantity: number;
  instruction: string | null;
}

export interface FirstConfirmationRequest {
  context: string;
  items: FirstConfirmationItemRequest[];
}

export interface ConfirmedItem {
  productId: string;
  quantity: number;
  appliedPrice: string;
  instruction: string | null;
}

export interface FirstIncorporation {
  id: string;
  confirmedAt: string;
  items: ConfirmedItem[];
}

export interface FirstConfirmationResponse {
  operationalReference: string;
  context: string;
  firstIncorporation: FirstIncorporation;
}

export interface SubsequentConfirmationItemRequest {
  productId: string;
  quantity: number;
  instruction: string | null;
}

export interface SubsequentConfirmationRequest {
  pendingCompositionId: string;
  items: SubsequentConfirmationItemRequest[];
}

export interface PendingComposition {
  pendingCompositionId: string;
  createdAt: string;
  createdByIdentityId: string;
}

export interface CurrentPendingComposition {
  orderId: string;
  pendingComposition: PendingComposition | null;
}

export interface PendingCompositionCommand {
  orderId: string;
  pendingCompositionId?: string;
  idempotencyKey: string;
  antiforgeryToken: string;
}

export interface SubsequentIncorporation {
  id: string;
  ordinal: number;
  confirmedAt: string;
  items: ConfirmedItem[];
}

export interface SubsequentConfirmationResponse {
  operationalReference: string;
  incorporation: SubsequentIncorporation;
}

export interface OrderItem {
  productId: string;
  quantity: number;
  appliedPrice: string;
  instruction: string | null;
}

export interface OrderIncorporation {
  id: string;
  ordinal: number;
  confirmedAt: string;
  items: OrderItem[];
}

export interface OrderResponse {
  operationalReference: string;
  context: string;
  incorporations: OrderIncorporation[];
  functionalAmount: string;
  isLiquidationEligible: boolean;
  liquidationBlockers: string[];
  isLiquidated: boolean;
  isFrozen: boolean;
  liquidatedAmount: string | null;
  liquidationMode: string | null;
  declaredPaymentMedium: string | null;
  isClosureEligible: boolean;
  isClosed: boolean;
  closedAt: string | null;
}

export interface OrderOperationsProblemDetails {
  type?: string;
  title?: string;
  status?: number;
  detail?: string;
  instance?: string;
  code?: string;
  productId?: string;
}

export class OrderOperationsProblemError extends Error {
  readonly problem: OrderOperationsProblemDetails;

  constructor(problem: OrderOperationsProblemDetails) {
    super("Order Operations rejected the request with Problem Details.");
    this.name = "OrderOperationsProblemError";
    this.problem = problem;
  }
}

export class OrderOperationsNetworkError extends Error {
  constructor(options?: ErrorOptions) {
    super("The Order Operations request did not receive a response.", options);
    this.name = "OrderOperationsNetworkError";
  }
}

export class OrderLookupNetworkError extends Error {
  constructor(options?: ErrorOptions) {
    super("The Order lookup request did not receive a response.", options);
    this.name = "OrderLookupNetworkError";
  }
}

async function readProblem(
  response: Response,
): Promise<OrderOperationsProblemDetails> {
  const contentType = response.headers.get("content-type") ?? "";

  if (contentType.includes("application/problem+json")) {
    return {
      ...((await response.json()) as OrderOperationsProblemDetails),
      status: response.status,
    };
  }

  return { status: response.status };
}

async function requireOrderOperationsSuccess(
  response: Response,
): Promise<void> {
  if (response.ok) {
    return;
  }

  if (response.status >= 500) {
    throw new OrderOperationsNetworkError();
  }

  throw new OrderOperationsProblemError(await readProblem(response));
}

export async function confirmFirst(
  request: FirstConfirmationRequest,
  idempotencyKey: string,
  antiforgeryToken: string,
): Promise<FirstConfirmationResponse> {
  let response: Response;

  try {
    response = await fetch("/api/order-operations/first-confirmations", {
      method: "POST",
      credentials: "same-origin",
      headers: {
        "Content-Type": "application/json",
        "Idempotency-Key": idempotencyKey,
        "X-NexoBar-CSRF": antiforgeryToken,
      },
      body: JSON.stringify(request),
    });
  } catch (error) {
    throw new OrderOperationsNetworkError({ cause: error });
  }

  await requireOrderOperationsSuccess(response);

  return (await response.json()) as FirstConfirmationResponse;
}

export async function confirmSubsequent(
  operationalReference: string,
  request: SubsequentConfirmationRequest,
  idempotencyKey: string,
  antiforgeryToken: string,
): Promise<SubsequentConfirmationResponse> {
  let response: Response;

  try {
    response = await fetch(
      `/api/order-operations/orders/${encodeURIComponent(operationalReference)}/confirmations`,
      {
        method: "POST",
        credentials: "same-origin",
        headers: {
          "Content-Type": "application/json",
          "Idempotency-Key": idempotencyKey,
          "X-NexoBar-CSRF": antiforgeryToken,
        },
        body: JSON.stringify(request),
      },
    );
  } catch (error) {
    throw new OrderOperationsNetworkError({ cause: error });
  }

  await requireOrderOperationsSuccess(response);

  return (await response.json()) as SubsequentConfirmationResponse;
}

export async function getOrder(
  operationalReference: string,
): Promise<OrderResponse> {
  let response: Response;

  try {
    response = await fetch(
      `/api/order-operations/orders/${encodeURIComponent(operationalReference)}`,
    );
  } catch (error) {
    throw new OrderLookupNetworkError({ cause: error });
  }

  if (!response.ok) {
    throw new OrderOperationsProblemError(await readProblem(response));
  }

  return (await response.json()) as OrderResponse;
}

export async function getPendingComposition(
  orderId: string,
): Promise<CurrentPendingComposition> {
  let response: Response;
  try {
    response = await fetch(
      `/api/orders/${encodeURIComponent(orderId)}/pending-composition`,
      { credentials: "same-origin" },
    );
  } catch (error) {
    throw new OrderOperationsNetworkError({ cause: error });
  }

  await requireOrderOperationsSuccess(response);
  return (await response.json()) as CurrentPendingComposition;
}

export async function startPendingComposition(
  command: PendingCompositionCommand,
): Promise<PendingComposition> {
  let response: Response;
  try {
    response = await fetch(
      `/api/orders/${encodeURIComponent(command.orderId)}/pending-composition`,
      {
        method: "POST",
        credentials: "same-origin",
        headers: {
          "Idempotency-Key": command.idempotencyKey,
          "X-NexoBar-CSRF": command.antiforgeryToken,
        },
      },
    );
  } catch (error) {
    throw new OrderOperationsNetworkError({ cause: error });
  }

  await requireOrderOperationsSuccess(response);
  return (await response.json()) as PendingComposition;
}

export async function discardPendingComposition(
  command: PendingCompositionCommand & { pendingCompositionId: string },
): Promise<void> {
  let response: Response;
  try {
    response = await fetch(
      `/api/orders/${encodeURIComponent(command.orderId)}/pending-composition/${encodeURIComponent(command.pendingCompositionId)}/discard`,
      {
        method: "POST",
        credentials: "same-origin",
        headers: {
          "Idempotency-Key": command.idempotencyKey,
          "X-NexoBar-CSRF": command.antiforgeryToken,
        },
      },
    );
  } catch (error) {
    throw new OrderOperationsNetworkError({ cause: error });
  }

  await requireOrderOperationsSuccess(response);
}
