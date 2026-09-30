export type ConfigurationEntity = "contexts" | "preparation-responsibilities";
export type LifecycleAction = "rename" | "retire" | "reactivate" | "delete";
export interface LifecycleState {
  id: string;
  operationalName: string;
  isActive: boolean;
}
export interface LifecycleRequest {
  expectedCurrentOperationalName: string;
  expectedIsActive: boolean;
  newOperationalName?: string;
}
export class ConfigurationLifecycleError extends Error {
  readonly status: number;
  readonly code?: string;
  readonly detail?: string;
  constructor(status: number, code?: string, detail?: string) {
    super("Configuration lifecycle request failed.");
    this.status = status;
    this.code = code;
    this.detail = detail;
  }
}
export async function executeConfigurationLifecycle(
  entity: ConfigurationEntity,
  id: string,
  action: LifecycleAction,
  request: LifecycleRequest,
  key: string,
  token: string,
): Promise<LifecycleState & { isDeleted: boolean }> {
  const suffix =
    action === "delete"
      ? ""
      : `/${action === "rename" ? "operational-name-changes" : action}`;
  const response = await fetch(
    `/api/operational-configuration/${entity}/${encodeURIComponent(id)}${suffix}`,
    {
      method: action === "delete" ? "DELETE" : "POST",
      credentials: "same-origin",
      headers: {
        "Content-Type": "application/json",
        "Idempotency-Key": key,
        "X-NexoBar-CSRF": token,
      },
      body: JSON.stringify(request),
    },
  );
  if (!response.ok) {
    let problem: { code?: string; detail?: string } = {};
    try {
      problem = (await response.json()) as typeof problem;
    } catch {
      /* retain HTTP status */
    }
    throw new ConfigurationLifecycleError(
      response.status,
      problem.code,
      problem.detail,
    );
  }
  return (await response.json()) as LifecycleState & { isDeleted: boolean };
}
