import { type FormEvent, useEffect, useRef, useState } from "react";
import {
  activateIdentity,
  assignResponsibility,
  createIdentity,
  deactivateIdentity,
  FUNCTIONAL_RESPONSIBILITIES,
  grantPreparationEnablement,
  IdentityAdministrationNetworkError,
  IdentityAdministrationProblemError,
  listAdministrativeIdentities,
  listPreparationResponsibilities,
  renameIdentity,
  revokeResponsibility,
  revokePreparationEnablement,
  setLocalCredential,
  type AdministrativeIdentity,
  type CreateIdentityRequest,
  type FunctionalResponsibility,
  type IdentityAdministrationProblemDetails,
  type PreparationResponsibility,
  type RenameIdentityRequest,
  type SetLocalCredentialRequest,
} from "./identityAdministrationClient.ts";
import {
  getAntiforgeryToken,
  SessionProblemError,
} from "../identity/sessionClient.ts";

type Notice =
  | { kind: "success"; message: string }
  | { kind: "functional-error"; message: string }
  | { kind: "uncertain"; message: string };

interface CreateIdentityIntention {
  request: CreateIdentityRequest;
  idempotencyKey: string;
  antiforgeryToken: string;
}

interface RenameIdentityIntention {
  identityId: string;
  currentOperationalName: string;
  request: RenameIdentityRequest;
  idempotencyKey: string;
  antiforgeryToken: string;
}

interface RenameEditor {
  identityId: string;
  operationalName: string;
}

type IdentityMutationKind =
  | "activate"
  | "deactivate"
  | "assign"
  | "revoke";

interface IdentityMutationIntention {
  kind: IdentityMutationKind;
  identityId: string;
  operationalName: string;
  responsibility?: FunctionalResponsibility;
  idempotencyKey: string;
  antiforgeryToken: string;
}

type EnablementMutationKind = "grant" | "revoke";

interface EnablementMutationIntention {
  kind: EnablementMutationKind;
  identityId: string;
  operationalName: string;
  preparationResponsibility: PreparationResponsibility;
  idempotencyKey: string;
  antiforgeryToken: string;
}

interface CredentialEditor {
  identityId: string;
  hasLocalCredential: boolean;
  changeLoginIdentifier: boolean;
  loginIdentifier: string;
  secret: string;
}

interface CredentialIntention {
  identityId: string;
  request: SetLocalCredentialRequest;
  idempotencyKey: string;
  antiforgeryToken: string;
}

interface GeneralConfigurationPanelProps {
  currentIdentityId: string;
  onCurrentIdentityChanged: () => Promise<void>;
  onUnauthorized: () => void;
  onForbidden: () => void;
}

function messageForProblem(
  problem: IdentityAdministrationProblemDetails,
  action: "create" | "rename" | IdentityMutationKind,
): string {
  if (problem.code === "identities_and_capabilities.idempotency_conflict") {
    return "La clave de idempotencia pertenece a otra intención. Descartá la intención pendiente e iniciá una nueva.";
  }
  if (problem.code === "identities_and_capabilities.invalid_request") {
    return "Ingresá un nombre operacional válido.";
  }
  if (problem.code === "identities_and_capabilities.identity_not_found") {
    return "La Identity ya no existe. Actualizá el listado.";
  }
  if (
    problem.code ===
    "identities_and_capabilities.last_general_configuration_path"
  ) {
    return "Debe permanecer al menos una vía administrativa utilizable.";
  }
  if (problem.code === "identities_and_capabilities.responsibility_code_invalid") {
    return "La responsabilidad indicada no es válida.";
  }
  return action === "create"
    ? "No se pudo crear la Identity. Revisá los datos e intentá nuevamente."
    : action === "rename"
      ? "No se pudo cambiar el nombre operacional. Revisá los datos e intentá nuevamente."
      : "No se pudo actualizar la Identity. Revisá los datos e intentá nuevamente.";
}

function mutationLabel(intention: IdentityMutationIntention): string {
  switch (intention.kind) {
    case "activate":
      return `Activación de ${intention.operationalName}`;
    case "deactivate":
      return `Desactivación de ${intention.operationalName}`;
    case "assign":
      return `Asignación de ${intention.responsibility} a ${intention.operationalName}`;
    case "revoke":
      return `Revocación de ${intention.responsibility} a ${intention.operationalName}`;
  }
}

function enablementMutationLabel(
  intention: EnablementMutationIntention,
): string {
  return `${intention.kind === "grant" ? "Asignación" : "Revocación"} de habilitación ${intention.preparationResponsibility.operationalName} a ${intention.operationalName}`;
}

function sortIdentities(
  identities: AdministrativeIdentity[],
): AdministrativeIdentity[] {
  return [...identities].sort(
    (left, right) =>
      left.operationalName.localeCompare(right.operationalName) ||
      left.identityId.localeCompare(right.identityId),
  );
}

function reconcileIdentity(
  identities: AdministrativeIdentity[],
  response: AdministrativeIdentity,
): AdministrativeIdentity[] {
  const index = identities.findIndex(
    (identity) => identity.identityId === response.identityId,
  );
  const reconciled =
    index === -1
      ? [...identities, response]
      : identities.map((identity) =>
          identity.identityId === response.identityId ? response : identity,
        );
  return sortIdentities(reconciled);
}

export function GeneralConfigurationPanel({
  currentIdentityId,
  onCurrentIdentityChanged,
  onUnauthorized,
  onForbidden,
}: GeneralConfigurationPanelProps) {
  const [identities, setIdentities] = useState<AdministrativeIdentity[]>([]);
  const [isLoading, setIsLoading] = useState(true);
  const [loadError, setLoadError] = useState<string | null>(null);
  const [isForbidden, setIsForbidden] = useState(false);
  const readGeneration = useRef(0);
  const preparationResponsibilityReadGeneration = useRef(0);
  const [preparationResponsibilities, setPreparationResponsibilities] =
    useState<PreparationResponsibility[]>([]);
  const [isPreparationResponsibilitiesLoading, setIsPreparationResponsibilitiesLoading] =
    useState(true);
  const [preparationResponsibilitiesError, setPreparationResponsibilitiesError] =
    useState<string | null>(null);
  const [creationName, setCreationName] = useState("");
  const [isCreating, setIsCreating] = useState(false);
  const [creationNotice, setCreationNotice] = useState<Notice | null>(null);
  const [uncertainCreation, setUncertainCreation] =
    useState<CreateIdentityIntention | null>(null);
  const [renameEditor, setRenameEditor] = useState<RenameEditor | null>(null);
  const [isRenaming, setIsRenaming] = useState(false);
  const [renameNotice, setRenameNotice] = useState<Notice | null>(null);
  const [uncertainRename, setUncertainRename] =
    useState<RenameIdentityIntention | null>(null);
  const [isMutatingIdentity, setIsMutatingIdentity] = useState(false);
  const [mutationNotice, setMutationNotice] = useState<Notice | null>(null);
  const [uncertainMutation, setUncertainMutation] =
    useState<IdentityMutationIntention | null>(null);
  const [isMutatingEnablement, setIsMutatingEnablement] = useState(false);
  const [enablementNotice, setEnablementNotice] = useState<Notice | null>(null);
  const [uncertainEnablementMutation, setUncertainEnablementMutation] =
    useState<EnablementMutationIntention | null>(null);
  const [credentialEditor, setCredentialEditor] = useState<CredentialEditor | null>(null);
  const [isSettingCredential, setIsSettingCredential] = useState(false);
  const [credentialNotice, setCredentialNotice] = useState<Notice | null>(null);
  const [uncertainCredential, setUncertainCredential] =
    useState<CredentialIntention | null>(null);

  function retireForbiddenState() {
    readGeneration.current += 1;
    preparationResponsibilityReadGeneration.current += 1;
    setIdentities([]);
    setPreparationResponsibilities([]);
    setIsForbidden(true);
    onForbidden();
  }

  async function reloadIdentities() {
    const generation = ++readGeneration.current;
    setIsLoading(true);
    setLoadError(null);
    try {
      const loaded = await listAdministrativeIdentities();
      if (generation === readGeneration.current) {
        setIdentities(sortIdentities(loaded));
      }
    } catch (error) {
      if (generation !== readGeneration.current) return;
      if (error instanceof IdentityAdministrationProblemError) {
        if (error.problem.status === 401) {
          onUnauthorized();
          return;
        }
        if (error.problem.status === 403) {
          retireForbiddenState();
          return;
        }
      }
      setLoadError("No se pudo cargar el listado de Identities.");
    } finally {
      if (generation === readGeneration.current) {
        setIsLoading(false);
      }
    }
  }

  async function reloadPreparationResponsibilities() {
    const generation = ++preparationResponsibilityReadGeneration.current;
    setIsPreparationResponsibilitiesLoading(true);
    setPreparationResponsibilitiesError(null);
    try {
      const loaded = await listPreparationResponsibilities();
      if (generation === preparationResponsibilityReadGeneration.current) {
        setPreparationResponsibilities(loaded);
      }
    } catch (error) {
      if (generation !== preparationResponsibilityReadGeneration.current) return;
      if (error instanceof IdentityAdministrationProblemError) {
        if (error.problem.status === 401) {
          onUnauthorized();
          return;
        }
        if (error.problem.status === 403) {
          retireForbiddenState();
          return;
        }
      }
      setPreparationResponsibilitiesError(
        "No se pudo cargar el listado de responsabilidades de preparación.",
      );
    } finally {
      if (generation === preparationResponsibilityReadGeneration.current) {
        setIsPreparationResponsibilitiesLoading(false);
      }
    }
  }

  function reloadAdministrativeState() {
    void reloadIdentities();
    void reloadPreparationResponsibilities();
  }

  useEffect(() => {
    reloadAdministrativeState();
    return () => {
      readGeneration.current += 1;
      preparationResponsibilityReadGeneration.current += 1;
    };
  }, []);

  async function prepareMutation(
    setNotice: (notice: Notice) => void,
  ): Promise<string | null> {
    try {
      return await getAntiforgeryToken();
    } catch (error) {
      if (error instanceof SessionProblemError && error.status === 401) {
        onUnauthorized();
      }
      setNotice({
        kind: "functional-error",
        message: "No se pudo preparar la operación administrativa segura.",
      });
      return null;
    }
  }

  async function submitCreation(intention: CreateIdentityIntention) {
    setCreationNotice(null);
    setIsCreating(true);
    const formMatchesIntention = creationName === intention.request.operationalName;
    try {
      const created = await createIdentity(
        intention.request,
        intention.idempotencyKey,
        intention.antiforgeryToken,
      );
      setIdentities((current) => reconcileIdentity(current, created));
      setUncertainCreation(null);
      if (formMatchesIntention) setCreationName("");
      setCreationNotice({ kind: "success", message: "Identity creada correctamente." });
    } catch (error) {
      if (error instanceof IdentityAdministrationProblemError) {
        if (error.problem.status === 401) {
          onUnauthorized();
          return;
        }
        if (error.problem.status === 403) {
          retireForbiddenState();
          return;
        }
        setUncertainCreation(null);
        setCreationNotice({
          kind: "functional-error",
          message: messageForProblem(error.problem, "create"),
        });
        return;
      }
      setUncertainCreation(intention);
      setCreationNotice({
        kind: "uncertain",
        message:
          error instanceof IdentityAdministrationNetworkError
            ? "Resultado no confirmado: se perdió la comunicación y no sabemos si la Identity fue creada."
            : "Resultado no confirmado: no fue posible confirmar la respuesta del servidor.",
      });
    } finally {
      setIsCreating(false);
    }
  }

  async function handleCreate(event: FormEvent<HTMLFormElement>) {
    event.preventDefault();
    if (uncertainCreation !== null) return;
    const antiforgeryToken = await prepareMutation(setCreationNotice);
    if (antiforgeryToken === null) return;
    await submitCreation({
      request: { operationalName: creationName },
      idempotencyKey: crypto.randomUUID(),
      antiforgeryToken,
    });
  }

  async function submitRename(intention: RenameIdentityIntention) {
    setRenameNotice(null);
    setIsRenaming(true);
    const formMatchesIntention =
      renameEditor?.identityId === intention.identityId &&
      renameEditor.operationalName === intention.request.operationalName;
    try {
      const renamed = await renameIdentity(
        intention.identityId,
        intention.request,
        intention.idempotencyKey,
        intention.antiforgeryToken,
      );
      setIdentities((current) => reconcileIdentity(current, renamed));
      setUncertainRename(null);
      if (formMatchesIntention) setRenameEditor(null);
      setRenameNotice({
        kind: "success",
        message: `Nombre operacional de ${intention.currentOperationalName} actualizado correctamente.`,
      });
    } catch (error) {
      if (error instanceof IdentityAdministrationProblemError) {
        if (error.problem.status === 401) {
          onUnauthorized();
          return;
        }
        if (error.problem.status === 403) {
          retireForbiddenState();
          return;
        }
        setUncertainRename(null);
        setRenameNotice({
          kind: "functional-error",
          message: messageForProblem(error.problem, "rename"),
        });
        return;
      }
      setUncertainRename(intention);
      setRenameNotice({
        kind: "uncertain",
        message:
          error instanceof IdentityAdministrationNetworkError
            ? "Resultado no confirmado: se perdió la comunicación y no sabemos si el nombre fue actualizado."
            : "Resultado no confirmado: no fue posible confirmar la respuesta del servidor.",
      });
    } finally {
      setIsRenaming(false);
    }
  }

  async function submitIdentityMutation(
    intention: IdentityMutationIntention,
  ) {
    setMutationNotice(null);
    setIsMutatingIdentity(true);
    try {
      const response =
        intention.kind === "activate"
          ? await activateIdentity(
              intention.identityId,
              intention.idempotencyKey,
              intention.antiforgeryToken,
            )
          : intention.kind === "deactivate"
            ? await deactivateIdentity(
                intention.identityId,
                intention.idempotencyKey,
                intention.antiforgeryToken,
              )
            : intention.kind === "assign"
              ? await assignResponsibility(
                  intention.identityId,
                  intention.responsibility!,
                  intention.idempotencyKey,
                  intention.antiforgeryToken,
                )
              : await revokeResponsibility(
                  intention.identityId,
                  intention.responsibility!,
                  intention.idempotencyKey,
                  intention.antiforgeryToken,
                );
      setIdentities((current) => reconcileIdentity(current, response));
      setUncertainMutation(null);
      setMutationNotice({
        kind: "success",
        message: `${mutationLabel(intention)} realizada correctamente.`,
      });
      if (intention.identityId === currentIdentityId) {
        await onCurrentIdentityChanged();
      }
    } catch (error) {
      if (error instanceof IdentityAdministrationProblemError) {
        if (error.problem.status === 401) {
          onUnauthorized();
          return;
        }
        if (error.problem.status === 403) {
          retireForbiddenState();
          return;
        }
        setUncertainMutation(null);
        setMutationNotice({
          kind: "functional-error",
          message: messageForProblem(error.problem, intention.kind),
        });
        return;
      }
      setUncertainMutation(intention);
      setMutationNotice({
        kind: "uncertain",
        message:
          error instanceof IdentityAdministrationNetworkError
            ? "Resultado no confirmado: se perdió la comunicación y no sabemos si se actualizó la Identity."
            : "Resultado no confirmado: no fue posible confirmar la respuesta del servidor.",
      });
    } finally {
      setIsMutatingIdentity(false);
    }
  }

  async function startIdentityMutation(
    kind: IdentityMutationKind,
    identity: AdministrativeIdentity,
    responsibility?: FunctionalResponsibility,
  ) {
    if (isMutatingIdentity || uncertainMutation !== null) return;
    const antiforgeryToken = await prepareMutation(setMutationNotice);
    if (antiforgeryToken === null) return;
    await submitIdentityMutation({
      kind,
      identityId: identity.identityId,
      operationalName: identity.operationalName,
      responsibility,
      idempotencyKey: crypto.randomUUID(),
      antiforgeryToken,
    });
  }

  async function submitEnablementMutation(
    intention: EnablementMutationIntention,
  ) {
    setEnablementNotice(null);
    setIsMutatingEnablement(true);
    try {
      const response =
        intention.kind === "grant"
          ? await grantPreparationEnablement(
              intention.identityId,
              intention.preparationResponsibility.id,
              intention.idempotencyKey,
              intention.antiforgeryToken,
            )
          : await revokePreparationEnablement(
              intention.identityId,
              intention.preparationResponsibility.id,
              intention.idempotencyKey,
              intention.antiforgeryToken,
            );
      setIdentities((current) => reconcileIdentity(current, response));
      setUncertainEnablementMutation(null);
      setEnablementNotice({
        kind: "success",
        message: `${enablementMutationLabel(intention)} realizada correctamente.`,
      });
    } catch (error) {
      if (error instanceof IdentityAdministrationProblemError) {
        if (error.problem.status === 401) {
          onUnauthorized();
          return;
        }
        if (error.problem.status === 403) {
          retireForbiddenState();
          return;
        }
        setUncertainEnablementMutation(null);
        setEnablementNotice({
          kind: "functional-error",
          message:
            error.problem.status === 400
              ? "La responsabilidad de preparación indicada no es válida."
              : messageForProblem(error.problem, "assign"),
        });
        return;
      }
      setUncertainEnablementMutation(intention);
      setEnablementNotice({
        kind: "uncertain",
        message:
          error instanceof IdentityAdministrationNetworkError
            ? "Resultado no confirmado: se perdió la comunicación y no sabemos si se actualizó la habilitación de preparación."
            : "Resultado no confirmado: no fue posible confirmar la respuesta del servidor.",
      });
    } finally {
      setIsMutatingEnablement(false);
    }
  }

  async function startEnablementMutation(
    kind: EnablementMutationKind,
    identity: AdministrativeIdentity,
    preparationResponsibility: PreparationResponsibility,
  ) {
    if (isMutatingEnablement || uncertainEnablementMutation !== null) return;
    const antiforgeryToken = await prepareMutation(setEnablementNotice);
    if (antiforgeryToken === null) return;
    await submitEnablementMutation({
      kind,
      identityId: identity.identityId,
      operationalName: identity.operationalName,
      preparationResponsibility,
      idempotencyKey: crypto.randomUUID(),
      antiforgeryToken,
    });
  }

  function clearCredentialEditor() {
    setCredentialEditor(null);
    setUncertainCredential(null);
  }

  async function submitCredential(intention: CredentialIntention) {
    setCredentialNotice(null);
    setIsSettingCredential(true);
    try {
      const response = await setLocalCredential(
        intention.identityId,
        intention.request,
        intention.idempotencyKey,
        intention.antiforgeryToken,
      );
      setIdentities((current) => reconcileIdentity(current, response));
      clearCredentialEditor();
      setCredentialNotice({ kind: "success", message: "Credencial actualizada correctamente." });
      if (intention.identityId === currentIdentityId) {
        onUnauthorized();
      }
    } catch (error) {
      if (error instanceof IdentityAdministrationProblemError) {
        if (error.problem.status === 401) {
          onUnauthorized();
          return;
        }
        if (error.problem.status === 403) {
          retireForbiddenState();
          return;
        }
        setUncertainCredential(null);
        setCredentialNotice({
          kind: "functional-error",
          message:
            error.problem.code === "identities_and_capabilities.idempotency_conflict"
              ? messageForProblem(error.problem, "rename")
              : error.problem.status === 409
                ? "El identificador de acceso ya está en uso."
                : "Revisá el identificador de acceso y la clave secreta.",
        });
        return;
      }
      setUncertainCredential(intention);
      setCredentialNotice({
        kind: "uncertain",
        message: "Resultado no confirmado: no sabemos si la credencial fue actualizada.",
      });
    } finally {
      setIsSettingCredential(false);
    }
  }

  async function handleCredential(event: FormEvent<HTMLFormElement>) {
    event.preventDefault();
    if (credentialEditor === null || uncertainCredential !== null) return;
    if (!credentialEditor.hasLocalCredential && !credentialEditor.loginIdentifier.trim()) {
      setCredentialNotice({ kind: "functional-error", message: "Ingresá un identificador de acceso." });
      return;
    }
    const antiforgeryToken = await prepareMutation(setCredentialNotice);
    if (antiforgeryToken === null) return;
    const request: SetLocalCredentialRequest = { secret: credentialEditor.secret };
    if (!credentialEditor.hasLocalCredential || credentialEditor.changeLoginIdentifier) {
      request.loginIdentifier = credentialEditor.loginIdentifier;
    }
    await submitCredential({
      identityId: credentialEditor.identityId,
      request,
      idempotencyKey: crypto.randomUUID(),
      antiforgeryToken,
    });
  }

  async function handleRename(event: FormEvent<HTMLFormElement>) {
    event.preventDefault();
    if (renameEditor === null || uncertainRename !== null) return;
    const target = identities.find(
      (identity) => identity.identityId === renameEditor.identityId,
    );
    if (target === undefined) return;
    const antiforgeryToken = await prepareMutation(setRenameNotice);
    if (antiforgeryToken === null) return;
    await submitRename({
      identityId: target.identityId,
      currentOperationalName: target.operationalName,
      request: { operationalName: renameEditor.operationalName },
      idempotencyKey: crypto.randomUUID(),
      antiforgeryToken,
    });
  }

  if (isForbidden) return null;

  return (
    <section className="panel" aria-labelledby="general-configuration-title">
      <h2 id="general-configuration-title">Configuración general</h2>
      <form onSubmit={(event) => void handleCreate(event)}>
        <label htmlFor="identity-operational-name">Nombre operacional</label>
        <input
          id="identity-operational-name"
          name="operationalName"
          value={creationName}
          onChange={(event) => setCreationName(event.target.value)}
          disabled={isCreating}
          required
        />
        <button
          type="submit"
          disabled={isCreating || uncertainCreation !== null}
        >
          {isCreating
            ? "Creando…"
            : uncertainCreation
              ? "Hay una intención pendiente"
              : "Crear Identity"}
        </button>
      </form>

      {creationNotice && (
        <p className={`notice notice--${creationNotice.kind}`} role="status">
          {creationNotice.message}
        </p>
      )}
      {uncertainCreation && (
        <div
          className="uncertain-intention"
          role="region"
          aria-label="Creación de Identity con resultado no confirmado"
        >
          <h3>Creación pendiente de confirmación</h3>
          <p>{uncertainCreation.request.operationalName}</p>
          <p>El reintento usa exactamente estos datos y la misma intención.</p>
          <div className="intention-actions">
            <button
              type="button"
              onClick={() => void submitCreation(uncertainCreation)}
              disabled={isCreating}
            >
              Reintentar misma intención
            </button>
            <button
              className="secondary-button"
              type="button"
              onClick={() => setUncertainCreation(null)}
              disabled={isCreating}
            >
              Descartar e iniciar nueva
            </button>
          </div>
        </div>
      )}

      <div className="section-heading">
        <h3>Identities</h3>
        <button
          className="secondary-button"
          type="button"
          onClick={reloadAdministrativeState}
          disabled={isLoading || isPreparationResponsibilitiesLoading}
        >
          Actualizar
        </button>
      </div>
      {renameNotice && (
        <p className={`notice notice--${renameNotice.kind}`} role="status">
          {renameNotice.message}
        </p>
      )}
      {mutationNotice && (
        <p className={`notice notice--${mutationNotice.kind}`} role="status">
          {mutationNotice.message}
        </p>
      )}
      {enablementNotice && (
        <p className={`notice notice--${enablementNotice.kind}`} role="status">
          {enablementNotice.message}
        </p>
      )}
      {isLoading && <p>Cargando Identities…</p>}
      {!isLoading && loadError && <p role="alert">{loadError}</p>}
      {isPreparationResponsibilitiesLoading && (
        <p>Cargando responsabilidades de preparación…</p>
      )}
      {!isPreparationResponsibilitiesLoading && preparationResponsibilitiesError && (
        <p role="alert">{preparationResponsibilitiesError}</p>
      )}
      {!isLoading && !loadError && identities.length === 0 && (
        <p>No hay Identities.</p>
      )}
      {!isLoading && !loadError && identities.length > 0 && (
        <div className="table-scroll">
          <table>
            <thead>
              <tr>
                <th scope="col">Nombre operacional</th>
                <th scope="col">Estado</th>
                <th scope="col">Credencial local</th>
                <th scope="col">Identificador de acceso</th>
                <th scope="col">Responsabilidades</th>
                <th scope="col">Habilitaciones de preparación</th>
                <th scope="col">Acciones</th>
              </tr>
            </thead>
            <tbody>
              {identities.map((identity) => {
                const unresolvedEnablements =
                  isPreparationResponsibilitiesLoading
                    ? []
                    : identity.preparationEnablements.filter(
                        (enablementId) =>
                          !preparationResponsibilities.some(
                            (responsibility) => responsibility.id === enablementId,
                          ),
                      );
                return (
                  <tr key={identity.identityId}>
                  <td>{identity.operationalName}</td>
                  <td>{identity.isActive ? "Activa" : "Inactiva"}</td>
                  <td>
                    {identity.hasLocalCredential
                      ? "Configurada"
                      : "No configurada"}
                  </td>
                  <td>{identity.loginIdentifier ?? ""}</td>
                  <td>
                    <ul aria-label={`Responsabilidades de ${identity.operationalName}`}>
                      {FUNCTIONAL_RESPONSIBILITIES.map((responsibility) => {
                        const isAssigned = identity.responsibilities.includes(
                          responsibility,
                        );
                        return (
                          <li key={responsibility}>
                            <span>
                              {responsibility}: {isAssigned ? "Asignada" : "No asignada"}
                            </span>{" "}
                            <button
                              className="secondary-button"
                              type="button"
                              onClick={() =>
                                void startIdentityMutation(
                                  isAssigned ? "revoke" : "assign",
                                  identity,
                                  responsibility,
                                )
                              }
                              disabled={
                                isMutatingIdentity || uncertainMutation !== null
                              }
                              aria-label={`${isAssigned ? "Revocar" : "Asignar"} ${responsibility} ${isAssigned ? "a" : "a"} ${identity.operationalName}`}
                            >
                              {isAssigned ? "Revocar" : "Asignar"}
                            </button>
                          </li>
                        );
                      })}
                    </ul>
                  </td>
                  <td>
                    <p>Habilitaciones de preparación</p>
                    <ul
                      aria-label={`Habilitaciones de preparación de ${identity.operationalName}`}
                    >
                      {preparationResponsibilities.map((responsibility) => {
                        const isEnabled = identity.preparationEnablements.includes(
                          responsibility.id,
                        );
                        return (
                          <li key={responsibility.id}>
                            <span>
                              {responsibility.operationalName}: {isEnabled ? "Habilitada" : "No habilitada"}
                            </span>{" "}
                            <button
                              className="secondary-button"
                              type="button"
                              onClick={() =>
                                void startEnablementMutation(
                                  isEnabled ? "revoke" : "grant",
                                  identity,
                                  responsibility,
                                )
                              }
                              disabled={
                                isMutatingEnablement ||
                                uncertainEnablementMutation !== null
                              }
                              aria-label={`${isEnabled ? "Revocar habilitación" : "Otorgar habilitación"} ${responsibility.operationalName} a ${identity.operationalName}`}
                            >
                              {isEnabled ? "Revocar habilitación" : "Otorgar habilitación"}
                            </button>
                          </li>
                        );
                      })}
                      {unresolvedEnablements.map((enablementId) => (
                        <li key={enablementId}>
                          <span>Responsabilidad de preparación desconocida</span>{" "}
                          <small>Id: {enablementId}</small>
                        </li>
                      ))}
                    </ul>
                  </td>
                  <td>
                    <button
                      className="secondary-button"
                      type="button"
                      onClick={() =>
                        void startIdentityMutation(
                          identity.isActive ? "deactivate" : "activate",
                          identity,
                        )
                      }
                      disabled={isMutatingIdentity || uncertainMutation !== null}
                      aria-label={`${identity.isActive ? "Desactivar" : "Activar"} ${identity.operationalName}`}
                    >
                      {identity.isActive ? "Desactivar" : "Activar"}
                    </button>
                    <button
                      className="secondary-button"
                      type="button"
                      onClick={() => {
                        if (!isSettingCredential && uncertainCredential === null) {
                          setCredentialEditor({
                            identityId: identity.identityId,
                            hasLocalCredential: identity.hasLocalCredential,
                            changeLoginIdentifier: !identity.hasLocalCredential,
                            loginIdentifier: "",
                            secret: "",
                          });
                          setCredentialNotice(null);
                        }
                      }}
                      disabled={isSettingCredential || uncertainCredential !== null}
                      aria-label={`Configurar credencial de ${identity.operationalName}`}
                    >
                      {identity.hasLocalCredential ? "Reemplazar credencial" : "Configurar credencial"}
                    </button>
                    <button
                      className="secondary-button"
                      type="button"
                      onClick={() => {
                        if (uncertainRename === null && !isRenaming) {
                          setRenameEditor({
                            identityId: identity.identityId,
                            operationalName: identity.operationalName,
                          });
                          setRenameNotice(null);
                        }
                      }}
                      disabled={isRenaming || uncertainRename !== null}
                      aria-label={`Cambiar nombre de ${identity.operationalName}`}
                    >
                      Cambiar nombre
                    </button>
                  </td>
                  </tr>
                );
              })}
            </tbody>
          </table>
        </div>
      )}

      {renameEditor && uncertainRename === null && (
        <form
          onSubmit={(event) => void handleRename(event)}
          aria-label="Cambiar nombre operacional"
        >
          <label htmlFor="renamed-identity-operational-name">
            Nuevo nombre operacional
          </label>
          <input
            id="renamed-identity-operational-name"
            name="operationalName"
            value={renameEditor.operationalName}
            onChange={(event) =>
              setRenameEditor((current) =>
                current === null
                  ? null
                  : { ...current, operationalName: event.target.value },
              )
            }
            disabled={isRenaming}
            required
          />
          <button type="submit" disabled={isRenaming}>
            {isRenaming ? "Cambiando…" : "Confirmar cambio de nombre"}
          </button>
          <button
            className="secondary-button"
            type="button"
            onClick={() => setRenameEditor(null)}
            disabled={isRenaming}
          >
            Cancelar
          </button>
        </form>
      )}

      {credentialNotice && (
        <p className={`notice notice--${credentialNotice.kind}`} role="status">
          {credentialNotice.message}
        </p>
      )}
      {credentialEditor && uncertainCredential === null && (
        <form onSubmit={(event) => void handleCredential(event)} aria-label="Configurar credencial local">
          <h3>Credencial local</h3>
          {(!credentialEditor.hasLocalCredential || credentialEditor.changeLoginIdentifier) && (
            <>
              <label htmlFor="credential-login-identifier">Identificador de acceso</label>
              <input
                id="credential-login-identifier"
                value={credentialEditor.loginIdentifier}
                onChange={(event) => setCredentialEditor((current) => current && { ...current, loginIdentifier: event.target.value })}
                disabled={isSettingCredential}
              />
            </>
          )}
          {credentialEditor.hasLocalCredential && !credentialEditor.changeLoginIdentifier && (
            <button type="button" className="secondary-button" onClick={() => setCredentialEditor((current) => current && { ...current, changeLoginIdentifier: true })}>
              Cambiar identificador de acceso
            </button>
          )}
          <label htmlFor="credential-secret">Nueva clave secreta</label>
          <input id="credential-secret" type="password" value={credentialEditor.secret} onChange={(event) => setCredentialEditor((current) => current && { ...current, secret: event.target.value })} disabled={isSettingCredential} required />
          <button type="submit" disabled={isSettingCredential}>{isSettingCredential ? "Actualizando…" : "Guardar credencial"}</button>
          <button type="button" className="secondary-button" onClick={clearCredentialEditor} disabled={isSettingCredential}>Cancelar</button>
        </form>
      )}
      {uncertainCredential && (
        <div className="uncertain-intention" role="region" aria-label="Credencial con resultado no confirmado">
          <h3>Credencial pendiente de confirmación</h3>
          <button type="button" onClick={() => void submitCredential(uncertainCredential)} disabled={isSettingCredential}>Reintentar misma intención</button>
          <button type="button" className="secondary-button" onClick={clearCredentialEditor} disabled={isSettingCredential}>Descartar e iniciar nueva</button>
        </div>
      )}

      {uncertainRename && (
        <div
          className="uncertain-intention"
          role="region"
          aria-label="Cambio de nombre con resultado no confirmado"
        >
          <h3>Cambio de nombre pendiente de confirmación</h3>
          <p>{uncertainRename.request.operationalName}</p>
          <p>El reintento usa exactamente estos datos y la misma intención.</p>
          <div className="intention-actions">
            <button
              type="button"
              onClick={() => void submitRename(uncertainRename)}
              disabled={isRenaming}
            >
              Reintentar mismo cambio de nombre
            </button>
            <button
              className="secondary-button"
              type="button"
              onClick={() => {
                setUncertainRename(null);
                setRenameEditor(null);
              }}
              disabled={isRenaming}
            >
              Descartar e iniciar nuevo
            </button>
          </div>
        </div>
      )}

      {uncertainMutation && (
        <div
          className="uncertain-intention"
          role="region"
          aria-label="Actualización de Identity con resultado no confirmado"
        >
          <h3>Actualización pendiente de confirmación</h3>
          <p>{mutationLabel(uncertainMutation)}</p>
          <p>El reintento usa exactamente estos datos y la misma intención.</p>
          <div className="intention-actions">
            <button
              type="button"
              onClick={() => void submitIdentityMutation(uncertainMutation)}
              disabled={isMutatingIdentity}
            >
              Reintentar misma intención
            </button>
            <button
              className="secondary-button"
              type="button"
              onClick={() => setUncertainMutation(null)}
              disabled={isMutatingIdentity}
            >
              Descartar e iniciar nueva
            </button>
          </div>
        </div>
      )}

      {uncertainEnablementMutation && (
        <div
          className="uncertain-intention"
          role="region"
          aria-label="Habilitación de preparación con resultado no confirmado"
        >
          <h3>Habilitación de preparación pendiente de confirmación</h3>
          <p>{enablementMutationLabel(uncertainEnablementMutation)}</p>
          <p>El reintento usa exactamente estos datos y la misma intención.</p>
          <div className="intention-actions">
            <button
              type="button"
              onClick={() =>
                void submitEnablementMutation(uncertainEnablementMutation)
              }
              disabled={isMutatingEnablement}
            >
              Reintentar misma intención
            </button>
            <button
              className="secondary-button"
              type="button"
              onClick={() => setUncertainEnablementMutation(null)}
              disabled={isMutatingEnablement}
            >
              Descartar e iniciar nueva
            </button>
          </div>
        </div>
      )}
    </section>
  );
}
