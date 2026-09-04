export interface InventoryConfigurationItem {
  itemId: string;
  operationalName: string;
  operationalUnit: string;
}

export interface InventoryOperationalItem extends InventoryConfigurationItem {
  currentRegisteredQuantity: string | null;
  quantityEstablished: boolean;
  hasNegativeBalanceInconsistency: boolean;
  asOfMovementRevision: number;
}

export interface CreateInventoryItemRequest {
  operationalName: string;
  operationalUnit: string;
}

export interface CreatedInventoryItem extends InventoryConfigurationItem {
  currentRegisteredQuantity: string | null;
  movementRevision: number;
}

export interface InventoryCountObservation {
  countObservationId: string;
  itemId: string;
  observedQuantity: string;
  observedMovementRevision: number;
  observedOperationalUnit: string;
  observedAt: string;
}

export type InventoryReconciliationOutcome = "reconciled" | "no_discrepancy";

export interface InventoryReconciliationResult {
  itemId: string;
  countObservationId: string;
  outcome: InventoryReconciliationOutcome;
  movementId: string | null;
  occurredAt: string | null;
  previousRegisteredQuantity: string | null;
  observedQuantity: string;
  difference: string | null;
  resultingRegisteredQuantity: string;
  movementRevision: number;
}

export interface InventoryRecordedMovement {
  movementId: string;
  itemId: string;
  nature: Exclude<InventoryMovementNature, "reconciliation">;
  quantity: string;
  previousRegisteredQuantity: string;
  resultingRegisteredQuantity: string;
  movementRevision: number;
  occurredAt: string;
}

export type InventoryMovementNature =
  "entry" | "manual_exit" | "waste" | "reconciliation";

export interface InventoryMovementReconciliation {
  observedQuantity: string;
  difference: string | null;
  establishedQuantity: boolean;
}

export interface InventoryMovement {
  movementId: string;
  movementRevision: number;
  nature: InventoryMovementNature;
  quantity: string;
  signedEffect: string | null;
  previousRegisteredQuantity: string | null;
  resultingRegisteredQuantity: string;
  occurredAt: string;
  actorIdentityId: string;
  actorOperationalName: string;
  reconciliation: InventoryMovementReconciliation | null;
}

export interface InventoryMovementHistory {
  itemId: string;
  operationalName: string;
  operationalUnit: string;
  movements: InventoryMovement[];
  nextBeforeRevision: number | null;
}

export interface InventoryProblemDetails {
  title?: string;
  status?: number;
  detail?: string;
  code?: string;
}

export class InventoryProblemError extends Error {
  readonly status: number;
  readonly problem: InventoryProblemDetails;

  constructor(status: number, problem: InventoryProblemDetails = {}) {
    super("The Inventory request was rejected.");
    this.name = "InventoryProblemError";
    this.status = status;
    this.problem = problem;
  }
}

export class InventoryNetworkError extends Error {
  constructor(options?: ErrorOptions) {
    super("The Inventory request did not receive a response.", options);
    this.name = "InventoryNetworkError";
  }
}

function isRecord(value: unknown): value is Record<string, unknown> {
  return typeof value === "object" && value !== null;
}

function isNonNegativeInteger(value: unknown): value is number {
  return typeof value === "number" && Number.isInteger(value) && value >= 0;
}

function isPositiveInteger(value: unknown): value is number {
  return typeof value === "number" && Number.isInteger(value) && value > 0;
}

function isNullableString(value: unknown): value is string | null {
  return typeof value === "string" || value === null;
}

async function send(
  request: RequestInfo | URL,
  init?: RequestInit,
): Promise<Response> {
  try {
    return await fetch(request, init);
  } catch (error) {
    throw new InventoryNetworkError({ cause: error });
  }
}

async function readProblem(
  response: Response,
): Promise<InventoryProblemDetails> {
  const contentType = response.headers.get("content-type") ?? "";
  if (!contentType.includes("application/problem+json")) {
    return { status: response.status };
  }

  const payload: unknown = await response.json();
  return isRecord(payload)
    ? (payload as InventoryProblemDetails)
    : { status: response.status };
}

async function requireSuccess(response: Response): Promise<void> {
  if (!response.ok) {
    throw new InventoryProblemError(
      response.status,
      await readProblem(response),
    );
  }
}

function parseConfigurationItem(value: unknown): InventoryConfigurationItem {
  if (
    !isRecord(value) ||
    typeof value.itemId !== "string" ||
    typeof value.operationalName !== "string" ||
    typeof value.operationalUnit !== "string"
  ) {
    throw new Error("The Inventory configuration item was not interpretable.");
  }

  return {
    itemId: value.itemId,
    operationalName: value.operationalName,
    operationalUnit: value.operationalUnit,
  };
}

function parseOperationalItem(value: unknown): InventoryOperationalItem {
  const base = parseConfigurationItem(value);
  if (
    !isRecord(value) ||
    !isNullableString(value.currentRegisteredQuantity) ||
    typeof value.quantityEstablished !== "boolean" ||
    typeof value.hasNegativeBalanceInconsistency !== "boolean" ||
    !isNonNegativeInteger(value.asOfMovementRevision) ||
    (value.quantityEstablished
      ? typeof value.currentRegisteredQuantity !== "string"
      : value.currentRegisteredQuantity !== null)
  ) {
    throw new Error("The Inventory operational item was not interpretable.");
  }

  return {
    ...base,
    currentRegisteredQuantity: value.currentRegisteredQuantity,
    quantityEstablished: value.quantityEstablished,
    hasNegativeBalanceInconsistency: value.hasNegativeBalanceInconsistency,
    asOfMovementRevision: value.asOfMovementRevision,
  };
}

function parseCreatedItem(value: unknown): CreatedInventoryItem {
  const base = parseConfigurationItem(value);
  if (
    !isRecord(value) ||
    !isNullableString(value.currentRegisteredQuantity) ||
    !isNonNegativeInteger(value.movementRevision)
  ) {
    throw new Error("The created Inventory item was not interpretable.");
  }

  return {
    ...base,
    currentRegisteredQuantity: value.currentRegisteredQuantity,
    movementRevision: value.movementRevision,
  };
}

function parseCountObservation(value: unknown): InventoryCountObservation {
  if (
    !isRecord(value) ||
    typeof value.countObservationId !== "string" ||
    typeof value.itemId !== "string" ||
    typeof value.observedQuantity !== "string" ||
    !isNonNegativeInteger(value.observedMovementRevision) ||
    typeof value.observedOperationalUnit !== "string" ||
    typeof value.observedAt !== "string"
  ) {
    throw new Error("The Inventory Count observation was not interpretable.");
  }

  return {
    countObservationId: value.countObservationId,
    itemId: value.itemId,
    observedQuantity: value.observedQuantity,
    observedMovementRevision: value.observedMovementRevision,
    observedOperationalUnit: value.observedOperationalUnit,
    observedAt: value.observedAt,
  };
}

function parseReconciliationResult(
  value: unknown,
): InventoryReconciliationResult {
  if (
    !isRecord(value) ||
    typeof value.itemId !== "string" ||
    typeof value.countObservationId !== "string" ||
    !["reconciled", "no_discrepancy"].includes(String(value.outcome)) ||
    !isNullableString(value.movementId) ||
    !isNullableString(value.occurredAt) ||
    !isNullableString(value.previousRegisteredQuantity) ||
    typeof value.observedQuantity !== "string" ||
    !isNullableString(value.difference) ||
    typeof value.resultingRegisteredQuantity !== "string" ||
    !isNonNegativeInteger(value.movementRevision)
  ) {
    throw new Error("The Inventory Reconciliation was not interpretable.");
  }

  const outcome = value.outcome as InventoryReconciliationOutcome;
  if (
    (outcome === "no_discrepancy" &&
      (value.movementId !== null ||
        value.occurredAt !== null ||
        typeof value.previousRegisteredQuantity !== "string" ||
        typeof value.difference !== "string")) ||
    (outcome === "reconciled" &&
      (typeof value.movementId !== "string" ||
        typeof value.occurredAt !== "string" ||
        (value.previousRegisteredQuantity === null
          ? value.difference !== null
          : typeof value.difference !== "string")))
  ) {
    throw new Error("The Inventory Reconciliation was not interpretable.");
  }

  return {
    itemId: value.itemId,
    countObservationId: value.countObservationId,
    outcome,
    movementId: value.movementId,
    occurredAt: value.occurredAt,
    previousRegisteredQuantity: value.previousRegisteredQuantity,
    observedQuantity: value.observedQuantity,
    difference: value.difference,
    resultingRegisteredQuantity: value.resultingRegisteredQuantity,
    movementRevision: value.movementRevision,
  };
}

function parseRecordedMovement(value: unknown): InventoryRecordedMovement {
  const nature = isRecord(value)
    ? (
        {
          Entry: "entry",
          ManualExit: "manual_exit",
          Waste: "waste",
        } as const
      )[String(value.nature) as "Entry" | "ManualExit" | "Waste"]
    : undefined;
  if (
    !isRecord(value) ||
    typeof value.movementId !== "string" ||
    typeof value.itemId !== "string" ||
    nature === undefined ||
    typeof value.quantity !== "string" ||
    typeof value.previousRegisteredQuantity !== "string" ||
    typeof value.resultingRegisteredQuantity !== "string" ||
    !isPositiveInteger(value.movementRevision) ||
    typeof value.occurredAt !== "string"
  ) {
    throw new Error("The Inventory Movement was not interpretable.");
  }

  return {
    movementId: value.movementId,
    itemId: value.itemId,
    nature,
    quantity: value.quantity,
    previousRegisteredQuantity: value.previousRegisteredQuantity,
    resultingRegisteredQuantity: value.resultingRegisteredQuantity,
    movementRevision: value.movementRevision,
    occurredAt: value.occurredAt,
  };
}

function parseReconciliation(
  value: unknown,
): InventoryMovementReconciliation | null {
  if (value === null) return null;
  if (
    !isRecord(value) ||
    typeof value.observedQuantity !== "string" ||
    !isNullableString(value.difference) ||
    typeof value.establishedQuantity !== "boolean"
  ) {
    throw new Error("The Inventory reconciliation was not interpretable.");
  }

  return {
    observedQuantity: value.observedQuantity,
    difference: value.difference,
    establishedQuantity: value.establishedQuantity,
  };
}

function parseMovement(value: unknown): InventoryMovement {
  if (
    !isRecord(value) ||
    typeof value.movementId !== "string" ||
    !isPositiveInteger(value.movementRevision) ||
    !["entry", "manual_exit", "waste", "reconciliation"].includes(
      String(value.nature),
    ) ||
    typeof value.quantity !== "string" ||
    !isNullableString(value.signedEffect) ||
    !isNullableString(value.previousRegisteredQuantity) ||
    typeof value.resultingRegisteredQuantity !== "string" ||
    typeof value.occurredAt !== "string" ||
    typeof value.actorIdentityId !== "string" ||
    typeof value.actorOperationalName !== "string" ||
    !(value.reconciliation === null || isRecord(value.reconciliation))
  ) {
    throw new Error("The Inventory movement was not interpretable.");
  }

  const nature = value.nature as InventoryMovementNature;
  const reconciliation = parseReconciliation(value.reconciliation);
  if (
    (nature === "reconciliation" && reconciliation === null) ||
    (nature !== "reconciliation" && reconciliation !== null) ||
    (reconciliation?.establishedQuantity === true &&
      (value.previousRegisteredQuantity !== null ||
        value.signedEffect !== null ||
        reconciliation.difference !== null)) ||
    (reconciliation?.establishedQuantity === false &&
      (typeof value.previousRegisteredQuantity !== "string" ||
        typeof value.signedEffect !== "string" ||
        typeof reconciliation.difference !== "string")) ||
    (reconciliation === null &&
      (typeof value.previousRegisteredQuantity !== "string" ||
        typeof value.signedEffect !== "string"))
  ) {
    throw new Error("The Inventory movement was not interpretable.");
  }

  return {
    movementId: value.movementId,
    movementRevision: value.movementRevision,
    nature,
    quantity: value.quantity,
    signedEffect: value.signedEffect,
    previousRegisteredQuantity: value.previousRegisteredQuantity,
    resultingRegisteredQuantity: value.resultingRegisteredQuantity,
    occurredAt: value.occurredAt,
    actorIdentityId: value.actorIdentityId,
    actorOperationalName: value.actorOperationalName,
    reconciliation,
  };
}

function parseArray<T>(
  value: unknown,
  parser: (item: unknown) => T,
  description: string,
): T[] {
  if (!Array.isArray(value)) {
    throw new Error(`The Inventory ${description} was not interpretable.`);
  }
  return value.map(parser);
}

export async function listInventoryConfigurationItems(): Promise<
  InventoryConfigurationItem[]
> {
  const response = await send("/api/inventory/configuration/items", {
    credentials: "same-origin",
  });
  await requireSuccess(response);
  return parseArray(
    (await response.json()) as unknown,
    parseConfigurationItem,
    "configuration list",
  );
}

export async function createInventoryItem(
  request: CreateInventoryItemRequest,
  idempotencyKey: string,
  antiforgeryToken: string,
): Promise<CreatedInventoryItem> {
  const response = await send("/api/inventory/items", {
    method: "POST",
    credentials: "same-origin",
    headers: {
      "Content-Type": "application/json",
      "Idempotency-Key": idempotencyKey,
      "X-NexoBar-CSRF": antiforgeryToken,
    },
    body: JSON.stringify(request),
  });
  await requireSuccess(response);
  return parseCreatedItem((await response.json()) as unknown);
}

export async function listInventoryOperationalItems(): Promise<
  InventoryOperationalItem[]
> {
  const response = await send("/api/inventory/operations/items", {
    credentials: "same-origin",
  });
  await requireSuccess(response);
  return parseArray(
    (await response.json()) as unknown,
    parseOperationalItem,
    "operational list",
  );
}

function mutationHeaders(
  idempotencyKey: string,
  antiforgeryToken: string,
): HeadersInit {
  return {
    "Content-Type": "application/json",
    "Idempotency-Key": idempotencyKey,
    "X-NexoBar-CSRF": antiforgeryToken,
  };
}

export async function recordInventoryCount(
  itemId: string,
  request: { observedQuantity: string },
  idempotencyKey: string,
  antiforgeryToken: string,
): Promise<InventoryCountObservation> {
  const response = await send(
    `/api/inventory/items/${encodeURIComponent(itemId)}/counts`,
    {
      method: "POST",
      credentials: "same-origin",
      headers: mutationHeaders(idempotencyKey, antiforgeryToken),
      body: JSON.stringify(request),
    },
  );
  await requireSuccess(response);
  return parseCountObservation((await response.json()) as unknown);
}

export async function reconcileInventoryCount(
  itemId: string,
  request: { countObservationId: string },
  idempotencyKey: string,
  antiforgeryToken: string,
): Promise<InventoryReconciliationResult> {
  const response = await send(
    `/api/inventory/items/${encodeURIComponent(itemId)}/reconcile`,
    {
      method: "POST",
      credentials: "same-origin",
      headers: mutationHeaders(idempotencyKey, antiforgeryToken),
      body: JSON.stringify(request),
    },
  );
  await requireSuccess(response);
  return parseReconciliationResult((await response.json()) as unknown);
}

async function recordInventoryMovement(
  itemId: string,
  route: "entries" | "manual-exits" | "waste",
  request: { quantity: string },
  idempotencyKey: string,
  antiforgeryToken: string,
): Promise<InventoryRecordedMovement> {
  const response = await send(
    `/api/inventory/items/${encodeURIComponent(itemId)}/${route}`,
    {
      method: "POST",
      credentials: "same-origin",
      headers: mutationHeaders(idempotencyKey, antiforgeryToken),
      body: JSON.stringify(request),
    },
  );
  await requireSuccess(response);
  return parseRecordedMovement((await response.json()) as unknown);
}

export function recordInventoryEntry(
  itemId: string,
  request: { quantity: string },
  idempotencyKey: string,
  antiforgeryToken: string,
): Promise<InventoryRecordedMovement> {
  return recordInventoryMovement(
    itemId,
    "entries",
    request,
    idempotencyKey,
    antiforgeryToken,
  );
}

export function recordManualInventoryExit(
  itemId: string,
  request: { quantity: string },
  idempotencyKey: string,
  antiforgeryToken: string,
): Promise<InventoryRecordedMovement> {
  return recordInventoryMovement(
    itemId,
    "manual-exits",
    request,
    idempotencyKey,
    antiforgeryToken,
  );
}

export function recordInventoryWaste(
  itemId: string,
  request: { quantity: string },
  idempotencyKey: string,
  antiforgeryToken: string,
): Promise<InventoryRecordedMovement> {
  return recordInventoryMovement(
    itemId,
    "waste",
    request,
    idempotencyKey,
    antiforgeryToken,
  );
}

export async function getInventoryMovementHistory(
  itemId: string,
  options: { beforeRevision?: number; limit?: number } = {},
): Promise<InventoryMovementHistory> {
  const query = new URLSearchParams();
  if (options.beforeRevision !== undefined) {
    query.set("beforeRevision", String(options.beforeRevision));
  }
  if (options.limit !== undefined) query.set("limit", String(options.limit));
  const suffix = query.size === 0 ? "" : `?${query.toString()}`;
  const response = await send(
    `/api/inventory/items/${encodeURIComponent(itemId)}/movements${suffix}`,
    { credentials: "same-origin" },
  );
  await requireSuccess(response);

  const payload: unknown = await response.json();
  if (
    !isRecord(payload) ||
    typeof payload.itemId !== "string" ||
    typeof payload.operationalName !== "string" ||
    typeof payload.operationalUnit !== "string" ||
    !Array.isArray(payload.movements) ||
    !(
      payload.nextBeforeRevision === null ||
      isPositiveInteger(payload.nextBeforeRevision)
    )
  ) {
    throw new Error("The Inventory movement history was not interpretable.");
  }

  return {
    itemId: payload.itemId,
    operationalName: payload.operationalName,
    operationalUnit: payload.operationalUnit,
    movements: payload.movements.map(parseMovement),
    nextBeforeRevision: payload.nextBeforeRevision,
  };
}
