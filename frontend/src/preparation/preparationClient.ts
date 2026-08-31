export interface PreparationWork {
  workId: string;
  preparationResponsibilityId: string;
  operationalReference: string;
  context: string;
  incorporationId: string;
  incorporationOrdinal: number;
  productId: string;
  productOperationalName: string;
  instruction: string | null;
  totalQuantity: number;
  pendingQuantity: number;
  inPreparationQuantity: number;
  readyQuantity: number;
  confirmedAt: string;
}

export interface PreparationCommandResult {
  workId: string;
  historyId: string;
  occurredAt: string;
  totalQuantity: number;
  pendingQuantity: number;
  inPreparationQuantity: number;
  readyQuantity: number;
}

export interface PreparationProblemDetails {
  title?: string;
  status?: number;
  detail?: string;
  code?: string;
}

export interface PreparationQuantityCommand {
  workId: string;
  quantity: number;
  idempotencyKey: string;
  antiforgeryToken: string;
}

export class PreparationProblemError extends Error {
  readonly status: number;
  readonly problem: PreparationProblemDetails;

  constructor(status: number, problem: PreparationProblemDetails = {}) {
    super("The Preparation request was rejected.");
    this.name = "PreparationProblemError";
    this.status = status;
    this.problem = problem;
  }
}

function isRecord(value: unknown): value is Record<string, unknown> {
  return typeof value === "object" && value !== null;
}

async function readProblem(
  response: Response,
): Promise<PreparationProblemDetails> {
  const contentType = response.headers.get("content-type") ?? "";
  if (!contentType.includes("application/problem+json")) {
    throw new Error("The Preparation error response was not interpretable.");
  }

  const payload: unknown = await response.json();
  if (!isRecord(payload)) {
    throw new Error("The Preparation error response was not interpretable.");
  }

  return payload as PreparationProblemDetails;
}

async function requireSuccess(response: Response): Promise<void> {
  if (!response.ok) {
    throw new PreparationProblemError(
      response.status,
      await readProblem(response),
    );
  }
}

function isQuantity(value: unknown): value is number {
  return typeof value === "number" && Number.isInteger(value) && value >= 0;
}

async function readCommandResult(
  response: Response,
): Promise<PreparationCommandResult> {
  const payload: unknown = await response.json();
  if (
    !isRecord(payload) ||
    typeof payload.workId !== "string" ||
    typeof payload.historyId !== "string" ||
    typeof payload.occurredAt !== "string" ||
    !isQuantity(payload.totalQuantity) ||
    !isQuantity(payload.pendingQuantity) ||
    !isQuantity(payload.inPreparationQuantity) ||
    !isQuantity(payload.readyQuantity)
  ) {
    throw new Error("The Preparation success response was not interpretable.");
  }

  return payload as unknown as PreparationCommandResult;
}

async function sendQuantityCommand(
  endpoint: "start" | "ready",
  command: PreparationQuantityCommand,
): Promise<PreparationCommandResult> {
  const response = await fetch(
    `/api/order-operations/preparation/work/${encodeURIComponent(command.workId)}/${endpoint}`,
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
  await requireSuccess(response);
  return readCommandResult(response);
}

export async function listPreparationWork(
  preparationResponsibilityId: string,
): Promise<PreparationWork[]> {
  const query = new URLSearchParams({ preparationResponsibilityId });
  const response = await fetch(
    `/api/order-operations/preparation/work?${query.toString()}`,
    { credentials: "same-origin" },
  );
  if (!response.ok) {
    throw new PreparationProblemError(
      response.status,
      await readProblem(response),
    );
  }

  return (await response.json()) as PreparationWork[];
}

export function startPreparationQuantity(
  command: PreparationQuantityCommand,
): Promise<PreparationCommandResult> {
  return sendQuantityCommand("start", command);
}

export function markPreparationQuantityReady(
  command: PreparationQuantityCommand,
): Promise<PreparationCommandResult> {
  return sendQuantityCommand("ready", command);
}
