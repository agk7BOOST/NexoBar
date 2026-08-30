export interface FirstConfirmationItemRequest {
  productId: string;
  quantity: number;
}

export interface FirstConfirmationRequest {
  context: string;
  items: FirstConfirmationItemRequest[];
}

export interface ConfirmedItem {
  productId: string;
  quantity: number;
  appliedPrice: string;
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
}

export interface SubsequentConfirmationRequest {
  items: SubsequentConfirmationItemRequest[];
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
    return (await response.json()) as OrderOperationsProblemDetails;
  }

  return { status: response.status };
}

export async function confirmFirst(
  request: FirstConfirmationRequest,
  idempotencyKey: string,
): Promise<FirstConfirmationResponse> {
  let response: Response;

  try {
    response = await fetch("/api/order-operations/first-confirmations", {
      method: "POST",
      headers: {
        "Content-Type": "application/json",
        "Idempotency-Key": idempotencyKey,
      },
      body: JSON.stringify(request),
    });
  } catch (error) {
    throw new OrderOperationsNetworkError({ cause: error });
  }

  if (!response.ok) {
    throw new OrderOperationsProblemError(await readProblem(response));
  }

  return (await response.json()) as FirstConfirmationResponse;
}

export async function confirmSubsequent(
  operationalReference: string,
  request: SubsequentConfirmationRequest,
  idempotencyKey: string,
): Promise<SubsequentConfirmationResponse> {
  let response: Response;

  try {
    response = await fetch(
      `/api/order-operations/orders/${encodeURIComponent(operationalReference)}/confirmations`,
      {
        method: "POST",
        headers: {
          "Content-Type": "application/json",
          "Idempotency-Key": idempotencyKey,
        },
        body: JSON.stringify(request),
      },
    );
  } catch (error) {
    throw new OrderOperationsNetworkError({ cause: error });
  }

  if (!response.ok) {
    throw new OrderOperationsProblemError(await readProblem(response));
  }

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
