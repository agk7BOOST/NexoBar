import { SensitiveActionDialog } from "../ui/SensitiveActionDialog.tsx";
import { ConfigurationLifecycleControls } from "./ConfigurationLifecycleControls.tsx";
import {
  type FormEvent,
  useCallback,
  useEffect,
  useRef,
  useState,
} from "react";
import {
  activateIdentity,
  assignResponsibility,
  createIdentity,
  createPreparationResponsibility,
  deactivateIdentity,
  deleteIdentity,
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
  type CreatePreparationResponsibilityRequest,
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
import { ContextConfigurationSection } from "./ContextConfigurationSection.tsx";

type Notice =
  | { kind: "success"; message: string }
  | { kind: "functional-error"; message: string }
  | { kind: "uncertain"; message: string };

interface CreateIdentityIntention {
  request: CreateIdentityRequest;
  idempotencyKey: string;
  antiforgeryToken: string;
}

interface CreatePreparationResponsibilityIntention {
  request: CreatePreparationResponsibilityRequest;
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
  "activate" | "deactivate" | "delete" | "assign" | "revoke";

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
  operationalName: string;
  hasLocalCredential: boolean;
  changeLoginIdentifier: boolean;
  currentLoginIdentifier: string;
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
  onOwnAccessChanged?: () => void;
  currentIdentityId: string;
  onCurrentIdentityChanged: () => Promise<void>;
  onUnauthorized: () => void;
  onForbidden: () => void;
}

const responsibilityLabels: Record<FunctionalResponsibility, string> = {
  OrderOperationsAndBasicClosure: "Pedidos y cierre básico",
  OperationalIntervention: "Intervención operacional",
  Preparation: "Preparación",
  CatalogConfiguration: "Configuración de productos",
  InventoryOperation: "Operación de inventario",
  InventoryConfiguration: "Configuración de inventario",
  GeneralConfiguration: "Configuración general",
};

function messageForProblem(
  problem: IdentityAdministrationProblemDetails,
  action: "create" | "rename" | IdentityMutationKind,
): string {
  if (problem.code === "identities_and_capabilities.idempotency_conflict") {
    return "Esta operación ya está asociada a otros datos. Revisá la operación pendiente antes de iniciar una nueva.";
  }
  if (problem.code === "identities_and_capabilities.invalid_request") {
    return "Ingresá un nombre operacional válido.";
  }
  if (problem.code === "identities_and_capabilities.identity_not_found") {
    return "La identidad ya no existe. Actualizá el listado.";
  }
  if (
    problem.code === "identities_and_capabilities.functional_history_exists"
  ) {
    return "Esta identidad debe conservarse porque tiene operaciones registradas a su nombre. Podés desactivarla si ya no debe operar; no se desactivó automáticamente.";
  }
  if (
    problem.code ===
    "identities_and_capabilities.last_general_configuration_path"
  ) {
    return action === "delete"
      ? "Debe permanecer otra vía ordinaria utilizable de Configuración general antes de eliminar esta identidad."
      : "Debe permanecer al menos una vía administrativa utilizable.";
  }
  if (
    problem.code === "identities_and_capabilities.responsibility_code_invalid"
  ) {
    return "La responsabilidad indicada no es válida.";
  }
  return action === "create"
    ? "No se pudo crear la identidad. Revisá los datos e intentá nuevamente."
    : action === "rename"
      ? "No se pudo cambiar el nombre operacional. Revisá los datos e intentá nuevamente."
      : "No se pudo actualizar la identidad. Revisá los datos e intentá nuevamente.";
}

function preparationResponsibilityCreationMessage(
  problem: IdentityAdministrationProblemDetails,
): string {
  if (problem.status === 409) {
    return "Ya existe un destino de preparación con ese nombre.";
  }
  if (problem.status === 400) {
    return "Ingresá un nombre operacional válido.";
  }
  return "No se pudo crear el destino de preparación. Revisá los datos e intentá nuevamente.";
}

function mutationLabel(intention: IdentityMutationIntention): string {
  switch (intention.kind) {
    case "activate":
      return `Activación de ${intention.operationalName}`;
    case "deactivate":
      return `Desactivación de ${intention.operationalName}`;
    case "delete":
      return `Eliminación definitiva de ${intention.operationalName}`;
    case "assign":
      return `Asignación de ${responsibilityLabels[intention.responsibility!]} a ${intention.operationalName}`;
    case "revoke":
      return `Revocación de ${responsibilityLabels[intention.responsibility!]} a ${intention.operationalName}`;
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
  onOwnAccessChanged,
  currentIdentityId,
  onCurrentIdentityChanged,
  onUnauthorized,
  onForbidden,
}: GeneralConfigurationPanelProps) {
  const [identities, setIdentities] = useState<AdministrativeIdentity[]>([]);
  const [selectedIdentityId, setSelectedIdentityId] = useState<string | null>(
    null,
  );
  const identityDetailRef = useRef<HTMLElement>(null);
  const [isLoading, setIsLoading] = useState(true);
  const [loadError, setLoadError] = useState<string | null>(null);
  const [isForbidden, setIsForbidden] = useState(false);
  const readGeneration = useRef(0);
  const [destinationLifecyclePending, setDestinationLifecyclePending] =
    useState(false);
  const preparationResponsibilityReadGeneration = useRef(0);
  const [preparationResponsibilities, setPreparationResponsibilities] =
    useState<PreparationResponsibility[]>([]);
  const [
    isPreparationResponsibilitiesLoading,
    setIsPreparationResponsibilitiesLoading,
  ] = useState(true);
  const [
    preparationResponsibilitiesError,
    setPreparationResponsibilitiesError,
  ] = useState<string | null>(null);
  const [preparationResponsibilityName, setPreparationResponsibilityName] =
    useState("");
  const [
    isCreatingPreparationResponsibility,
    setIsCreatingPreparationResponsibility,
  ] = useState(false);
  const [
    preparationResponsibilityCreationNotice,
    setPreparationResponsibilityCreationNotice,
  ] = useState<Notice | null>(null);
  const [
    uncertainPreparationResponsibilityCreation,
    setUncertainPreparationResponsibilityCreation,
  ] = useState<CreatePreparationResponsibilityIntention | null>(null);
  const [creationName, setCreationName] = useState("");
  const [isCreating, setIsCreating] = useState(false);
  const [creationNotice, setCreationNotice] = useState<Notice | null>(null);
  const [createdIdentityId, setCreatedIdentityId] = useState<string | null>(
    null,
  );
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
  const [deleteTarget, setDeleteTarget] =
    useState<AdministrativeIdentity | null>(null);
  const [isMutatingEnablement, setIsMutatingEnablement] = useState(false);
  const [enablementNotice, setEnablementNotice] = useState<Notice | null>(null);
  const [uncertainEnablementMutation, setUncertainEnablementMutation] =
    useState<EnablementMutationIntention | null>(null);
  const [deactivateTarget, setDeactivateTarget] =
    useState<AdministrativeIdentity | null>(null);
  const [credentialEditor, setCredentialEditor] =
    useState<CredentialEditor | null>(null);
  const [isSettingCredential, setIsSettingCredential] = useState(false);
  const [credentialNotice, setCredentialNotice] = useState<Notice | null>(null);
  const [uncertainCredential, setUncertainCredential] =
    useState<CredentialIntention | null>(null);

  const retireForbiddenState = useCallback(() => {
    readGeneration.current += 1;
    preparationResponsibilityReadGeneration.current += 1;
    setIdentities([]);
    setPreparationResponsibilities([]);
    setIsForbidden(true);
    onForbidden();
  }, [onForbidden]);

  const loadIdentities = useCallback(
    async (generation: number) => {
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
        setLoadError("No se pudo cargar el listado de identidades.");
      } finally {
        if (generation === readGeneration.current) {
          setIsLoading(false);
        }
      }
    },
    [onUnauthorized, retireForbiddenState],
  );

  const reloadIdentities = useCallback(() => {
    const generation = ++readGeneration.current;
    setIsLoading(true);
    setLoadError(null);
    return loadIdentities(generation);
  }, [loadIdentities]);

  const loadPreparationResponsibilities = useCallback(
    async (generation: number) => {
      try {
        const loaded = await listPreparationResponsibilities();
        if (generation === preparationResponsibilityReadGeneration.current) {
          setPreparationResponsibilities(loaded);
        }
        return generation === preparationResponsibilityReadGeneration.current;
      } catch (error) {
        if (generation !== preparationResponsibilityReadGeneration.current)
          return;
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
          "No se pudo cargar el listado de destinos de preparación.",
        );
        return false;
      } finally {
        if (generation === preparationResponsibilityReadGeneration.current) {
          setIsPreparationResponsibilitiesLoading(false);
        }
      }
    },
    [onUnauthorized, retireForbiddenState],
  );

  const reloadPreparationResponsibilities = useCallback(() => {
    const generation = ++preparationResponsibilityReadGeneration.current;
    setIsPreparationResponsibilitiesLoading(true);
    setPreparationResponsibilitiesError(null);
    return loadPreparationResponsibilities(generation);
  }, [loadPreparationResponsibilities]);

  const reloadAdministrativeState = useCallback(() => {
    void reloadIdentities();
    void reloadPreparationResponsibilities();
  }, [reloadIdentities, reloadPreparationResponsibilities]);

  useEffect(() => {
    void loadIdentities(++readGeneration.current);
    void loadPreparationResponsibilities(
      ++preparationResponsibilityReadGeneration.current,
    );
    return () => {
      readGeneration.current += 1;
      preparationResponsibilityReadGeneration.current += 1;
    };
  }, [loadIdentities, loadPreparationResponsibilities]);

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
    const formMatchesIntention =
      creationName === intention.request.operationalName;
    try {
      const created = await createIdentity(
        intention.request,
        intention.idempotencyKey,
        intention.antiforgeryToken,
      );
      setIdentities((current) => reconcileIdentity(current, created));
      setCreatedIdentityId(created.identityId);
      setSelectedIdentityId(created.identityId);
      setUncertainCreation(null);
      if (formMatchesIntention) setCreationName("");
      setCreationNotice({
        kind: "success",
        message: `Se creó la identidad “${created.operationalName}”.`,
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
            ? "Resultado no confirmado: se perdió la comunicación y no sabemos si la identidad fue creada."
            : "Resultado no confirmado: no fue posible confirmar la respuesta del servidor.",
      });
    } finally {
      setIsCreating(false);
    }
  }

  async function handleCreate(event: FormEvent<HTMLFormElement>) {
    event.preventDefault();
    if (uncertainCreation !== null) return;
    setCreatedIdentityId(null);
    const antiforgeryToken = await prepareMutation(setCreationNotice);
    if (antiforgeryToken === null) return;
    await submitCreation({
      request: { operationalName: creationName },
      idempotencyKey: crypto.randomUUID(),
      antiforgeryToken,
    });
  }

  async function submitPreparationResponsibilityCreation(
    intention: CreatePreparationResponsibilityIntention,
  ) {
    setPreparationResponsibilityCreationNotice(null);
    setIsCreatingPreparationResponsibility(true);
    const formMatchesIntention =
      preparationResponsibilityName === intention.request.operationalName;
    try {
      await createPreparationResponsibility(
        intention.request,
        intention.idempotencyKey,
        intention.antiforgeryToken,
      );
      setUncertainPreparationResponsibilityCreation(null);
      if (formMatchesIntention) {
        setPreparationResponsibilityName("");
      }
      const refreshed = await reloadPreparationResponsibilities();
      setPreparationResponsibilityCreationNotice({
        kind: "success",
        message: refreshed
          ? `Se creó el destino “${intention.request.operationalName}”.`
          : `Se creó el destino “${intention.request.operationalName}”, pero no pudimos actualizar la lista.`,
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
        setUncertainPreparationResponsibilityCreation(null);
        setPreparationResponsibilityCreationNotice({
          kind: "functional-error",
          message: preparationResponsibilityCreationMessage(error.problem),
        });
        return;
      }
      setUncertainPreparationResponsibilityCreation(intention);
      setPreparationResponsibilityCreationNotice({
        kind: "uncertain",
        message:
          error instanceof IdentityAdministrationNetworkError
            ? "Resultado no confirmado: se perdió la comunicación y no sabemos si el destino de preparación fue creado."
            : "Resultado no confirmado: no fue posible confirmar la respuesta del servidor.",
      });
    } finally {
      setIsCreatingPreparationResponsibility(false);
    }
  }

  async function handlePreparationResponsibilityCreate(
    event: FormEvent<HTMLFormElement>,
  ) {
    event.preventDefault();
    if (uncertainPreparationResponsibilityCreation !== null) return;
    if (preparationResponsibilityName.trim() === "") {
      setPreparationResponsibilityCreationNotice({
        kind: "functional-error",
        message: "Ingresá un nombre operacional válido.",
      });
      return;
    }
    const antiforgeryToken = await prepareMutation(
      setPreparationResponsibilityCreationNotice,
    );
    if (antiforgeryToken === null) return;
    await submitPreparationResponsibilityCreation({
      request: { operationalName: preparationResponsibilityName },
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
        message: `“${intention.currentOperationalName}” ahora se llama “${renamed.operationalName}”.`,
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

  async function submitIdentityMutation(intention: IdentityMutationIntention) {
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
            : intention.kind === "delete"
              ? await deleteIdentity(
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
      if (intention.kind === "delete") {
        setDeleteTarget(null);
        setSelectedIdentityId((current) =>
          current === intention.identityId ? null : current,
        );
        setIdentities((current) =>
          current.filter(
            (identity) => identity.identityId !== intention.identityId,
          ),
        );
      } else {
        setIdentities((current) => reconcileIdentity(current, response));
      }
      setUncertainMutation(null);
      setDeactivateTarget(null);
      setMutationNotice({
        kind: "success",
        message: `${mutationLabel(intention)} confirmada.`,
      });
      if (intention.identityId === currentIdentityId) {
        await onCurrentIdentityChanged();
      } else if (intention.kind === "delete") {
        await reloadIdentities();
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
            ? "Resultado no confirmado: se perdió la comunicación y no sabemos si se actualizó la identidad."
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
    setIsMutatingIdentity(true);
    const antiforgeryToken = await prepareMutation(setMutationNotice);
    if (antiforgeryToken === null) {
      setIsMutatingIdentity(false);
      return;
    }
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
        message: `${enablementMutationLabel(intention)} confirmada.`,
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
              ? "El destino de preparación indicado no es válido."
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

  const deactivateTargetId = deactivateTarget?.identityId;
  useEffect(() => {
    if (!deactivateTargetId) return;
    const opener = document.activeElement;
    document.getElementById("identity-deactivation-title")?.focus();
    return () => {
      queueMicrotask(() => {
        if (
          opener instanceof HTMLElement &&
          opener.isConnected &&
          !opener.closest("[hidden]")
        )
          opener.focus();
      });
    };
  }, [deactivateTargetId]);
  const renameTargetId = renameEditor?.identityId;
  const credentialTargetId = credentialEditor?.identityId;
  useEffect(() => {
    if (!renameTargetId) return;
    const opener = document.activeElement;
    const input = document.getElementById(
      "renamed-identity-operational-name",
    ) as HTMLInputElement | null;
    input?.focus();
    input?.select();
    return () => {
      queueMicrotask(() => {
        if (
          opener instanceof HTMLElement &&
          opener.isConnected &&
          !opener.closest("[hidden]")
        )
          opener.focus();
      });
    };
  }, [renameTargetId]);
  useEffect(() => {
    if (!credentialTargetId) return;
    const opener = document.activeElement;
    (
      document.getElementById("credential-login-identifier") ??
      document.getElementById("credential-secret")
    )?.focus();
    return () => {
      queueMicrotask(() => {
        if (
          opener instanceof HTMLElement &&
          opener.isConnected &&
          !opener.closest("[hidden]")
        )
          opener.focus();
      });
    };
  }, [credentialTargetId]);

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
      setCredentialNotice({
        kind: "success",
        message: `Se actualizó el acceso de “${response.operationalName}”.`,
      });
      if (intention.identityId === currentIdentityId) {
        (onOwnAccessChanged ?? onUnauthorized)();
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
            error.problem.code ===
            "identities_and_capabilities.idempotency_conflict"
              ? messageForProblem(error.problem, "rename")
              : error.problem.status === 409
                ? "El usuario de acceso ya está en uso."
                : "Revisá el usuario de acceso y la contraseña.",
        });
        return;
      }
      setUncertainCredential(intention);
      setCredentialNotice({
        kind: "uncertain",
        message:
          "No pudimos confirmar si se actualizó el acceso. Reintentá esta misma operación; no se duplicará.",
      });
    } finally {
      setIsSettingCredential(false);
    }
  }

  async function handleCredential(event: FormEvent<HTMLFormElement>) {
    event.preventDefault();
    if (credentialEditor === null || uncertainCredential !== null) return;
    if (
      !credentialEditor.hasLocalCredential &&
      !credentialEditor.loginIdentifier.trim()
    ) {
      setCredentialNotice({
        kind: "functional-error",
        message: "Ingresá un usuario de acceso.",
      });
      return;
    }
    const antiforgeryToken = await prepareMutation(setCredentialNotice);
    if (antiforgeryToken === null) return;
    const request: SetLocalCredentialRequest = {
      secret: credentialEditor.secret,
    };
    if (
      !credentialEditor.hasLocalCredential ||
      credentialEditor.changeLoginIdentifier
    ) {
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

  const createdIdentity =
    identities.find((identity) => identity.identityId === createdIdentityId) ??
    null;
  const selectedIdentity =
    identities.find((identity) => identity.identityId === selectedIdentityId) ??
    null;
  const identityEditing =
    renameEditor !== null ||
    credentialEditor !== null ||
    deleteTarget !== null ||
    uncertainRename !== null ||
    uncertainCredential !== null ||
    uncertainMutation !== null ||
    uncertainEnablementMutation !== null;

  function selectIdentity(identityId: string) {
    if (identityEditing && identityId !== selectedIdentityId) return;
    setSelectedIdentityId(identityId);
    window.requestAnimationFrame(() => {
      identityDetailRef.current?.focus();
      identityDetailRef.current?.scrollIntoView?.({ block: "start" });
    });
  }

  function returnToIdentityList() {
    if (identityEditing) return;
    const previousId = selectedIdentityId;
    setSelectedIdentityId(null);
    window.requestAnimationFrame(() => {
      document.getElementById(`manage-identity-${previousId}`)?.focus();
    });
  }

  return (
    <section
      className="panel general-configuration-panel"
      aria-labelledby="general-configuration-title"
    >
      <h2 id="general-configuration-title" tabIndex={-1}>
        Configuración general
      </h2>
      <form onSubmit={(event) => void handleCreate(event)}>
        <label htmlFor="identity-operational-name">Nombre</label>
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
              ? "Hay una operación pendiente de confirmar"
              : "Crear identidad"}
        </button>
      </form>

      {creationNotice && (
        <p
          className={`notice notice--${creationNotice.kind}`}
          role={creationNotice.kind === "functional-error" ? "alert" : "status"}
        >
          {creationNotice.message}
        </p>
      )}
      {createdIdentity && (
        <section
          className="created-identity-summary"
          aria-label={`Estado de ${createdIdentity.operationalName}`}
        >
          <h3>Identidad creada: {createdIdentity.operationalName}</h3>
          <p>
            {createdIdentity.isActive ? "Activa" : "Inactiva"} · Acceso{" "}
            {createdIdentity.hasLocalCredential
              ? "configurado"
              : "todavía sin configurar"}
            .
          </p>
          <p>
            {createdIdentity.responsibilities.length === 0
              ? "Todavía no tiene responsabilidades asignadas."
              : `Responsabilidades asignadas: ${createdIdentity.responsibilities.map((code) => responsibilityLabels[code as FunctionalResponsibility] ?? code).join(", ")}.`}
          </p>
          {createdIdentity.responsibilities.includes("Preparation") &&
            createdIdentity.preparationEnablements.length === 0 && (
              <p>
                Para trabajar en Preparación también necesita habilitación en un
                destino concreto.
              </p>
            )}
          <button
            type="button"
            className="secondary-button"
            onClick={() => selectIdentity(createdIdentity.identityId)}
          >
            Administrar esta identidad
          </button>
        </section>
      )}
      {uncertainCreation && (
        <div
          className="uncertain-intention"
          role="region"
          aria-label="Creación de identidad con resultado no confirmado"
        >
          <h3>Creación pendiente de confirmación</h3>
          <p>{uncertainCreation.request.operationalName}</p>
          <p>
            El reintento conserva los mismos datos sin duplicar la operación.
          </p>
          <div className="intention-actions">
            <button
              type="button"
              onClick={() => void submitCreation(uncertainCreation)}
              disabled={isCreating}
            >
              Reintentar esta operación
            </button>
            <button
              className="secondary-button"
              type="button"
              onClick={() => setUncertainCreation(null)}
              disabled={isCreating}
            >
              Dejar de reintentar
            </button>
            <p>
              Esto no deshace la operación. Su resultado sigue sin confirmarse.
            </p>
          </div>
        </div>
      )}

      <section aria-labelledby="preparation-responsibilities-title">
        <h3 id="preparation-responsibilities-title" tabIndex={-1}>
          Destinos de preparación
        </h3>
        <form
          onSubmit={(event) =>
            void handlePreparationResponsibilityCreate(event)
          }
        >
          <label htmlFor="preparation-responsibility-operational-name">
            Nombre del destino de preparación
          </label>
          <input
            id="preparation-responsibility-operational-name"
            name="operationalName"
            value={preparationResponsibilityName}
            onChange={(event) =>
              setPreparationResponsibilityName(event.target.value)
            }
            disabled={
              isCreatingPreparationResponsibility || destinationLifecyclePending
            }
            required
          />
          <button
            type="submit"
            disabled={
              destinationLifecyclePending ||
              isCreatingPreparationResponsibility ||
              uncertainPreparationResponsibilityCreation !== null
            }
          >
            {isCreatingPreparationResponsibility
              ? "Creando…"
              : uncertainPreparationResponsibilityCreation
                ? "Hay una operación pendiente de confirmar"
                : "Crear destino de preparación"}
          </button>
        </form>

        {preparationResponsibilityCreationNotice && (
          <p
            className={`notice notice--${preparationResponsibilityCreationNotice.kind}`}
            role={
              preparationResponsibilityCreationNotice.kind ===
              "functional-error"
                ? "alert"
                : "status"
            }
          >
            {preparationResponsibilityCreationNotice.message}
          </p>
        )}
        {uncertainPreparationResponsibilityCreation && (
          <div
            className="uncertain-intention"
            role="region"
            aria-label="Creación de destino de preparación con resultado no confirmado"
          >
            <h4>Creación pendiente de confirmación</h4>
            <p>
              {
                uncertainPreparationResponsibilityCreation.request
                  .operationalName
              }
            </p>
            <p>
              El reintento conserva los mismos datos sin duplicar la operación.
            </p>
            <div className="intention-actions">
              <button
                type="button"
                onClick={() =>
                  void submitPreparationResponsibilityCreation(
                    uncertainPreparationResponsibilityCreation,
                  )
                }
                disabled={
                  isCreatingPreparationResponsibility ||
                  destinationLifecyclePending
                }
              >
                Reintentar esta operación
              </button>
              <button
                className="secondary-button"
                type="button"
                onClick={() =>
                  setUncertainPreparationResponsibilityCreation(null)
                }
                disabled={
                  isCreatingPreparationResponsibility ||
                  destinationLifecyclePending
                }
              >
                Dejar de reintentar
              </button>
              <p>
                Esto no deshace la operación. Su resultado sigue sin
                confirmarse.
              </p>
            </div>
          </div>
        )}

        {isPreparationResponsibilitiesLoading && (
          <p>Cargando destinos de preparación…</p>
        )}
        {!isPreparationResponsibilitiesLoading &&
          preparationResponsibilitiesError && (
            <div role="alert">
              <p>{preparationResponsibilitiesError}</p>
              <button
                type="button"
                onClick={() => void reloadPreparationResponsibilities()}
              >
                Reintentar consulta
              </button>
            </div>
          )}
        {!isPreparationResponsibilitiesLoading &&
          !preparationResponsibilitiesError &&
          preparationResponsibilities.length === 0 && (
            <p>No hay destinos de preparación.</p>
          )}
        {preparationResponsibilities.length > 0 && (
          <ul aria-label="Listado de destinos de preparación">
            {preparationResponsibilities.map((responsibility) => (
              <li key={responsibility.id}>
                <span>{responsibility.operationalName}</span>
                <ConfigurationLifecycleControls
                  entity="preparation-responsibilities"
                  item={responsibility}
                  disabled={
                    isCreatingPreparationResponsibility ||
                    uncertainPreparationResponsibilityCreation !== null ||
                    destinationLifecyclePending
                  }
                  onPendingChange={setDestinationLifecyclePending}
                  onReload={reloadPreparationResponsibilities}
                  onResult={(message) =>
                    setPreparationResponsibilityCreationNotice({
                      kind: "success",
                      message,
                    })
                  }
                  onUnauthorized={onUnauthorized}
                  onForbidden={retireForbiddenState}
                />
              </li>
            ))}
          </ul>
        )}
      </section>

      <ContextConfigurationSection
        onUnauthorized={onUnauthorized}
        onForbidden={onForbidden}
      />

      <div className="section-heading">
        <h3>Identidades</h3>
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
        <p
          className={`notice notice--${renameNotice.kind}`}
          role={renameNotice.kind === "functional-error" ? "alert" : "status"}
        >
          {renameNotice.message}
        </p>
      )}
      {mutationNotice && (!deleteTarget || uncertainMutation !== null) && (
        <p
          className={`notice notice--${mutationNotice.kind}`}
          role={mutationNotice.kind === "functional-error" ? "alert" : "status"}
        >
          {mutationNotice.message}
        </p>
      )}
      {enablementNotice && (
        <p
          className={`notice notice--${enablementNotice.kind}`}
          role={
            enablementNotice.kind === "functional-error" ? "alert" : "status"
          }
        >
          {enablementNotice.message}
        </p>
      )}
      {isLoading && <p>Cargando identidades…</p>}
      {!isLoading && loadError && <p role="alert">{loadError}</p>}
      {!isLoading && !loadError && identities.length === 0 && (
        <p>No hay identidades.</p>
      )}
      {!isLoading && !loadError && identities.length > 0 && (
        <ul className="identity-list" aria-label="Listado de identidades">
          {identities.map((identity) => (
            <li
              key={identity.identityId}
              id={"identity-" + identity.identityId}
            >
              <div className="identity-list-summary">
                <strong>{identity.operationalName}</strong>
                <span>
                  {identity.isActive ? "Activa" : "Inactiva"} · Acceso{" "}
                  {identity.hasLocalCredential
                    ? "configurado"
                    : "sin configurar"}
                </span>
                <span>
                  {identity.responsibilities.length === 0
                    ? "Sin responsabilidades"
                    : identity.responsibilities
                        .map(
                          (code) =>
                            responsibilityLabels[
                              code as FunctionalResponsibility
                            ] ?? code,
                        )
                        .join(" · ")}
                </span>
              </div>
              <button
                id={"manage-identity-" + identity.identityId}
                type="button"
                className="secondary-button"
                aria-label={"Administrar " + identity.operationalName}
                disabled={
                  identityEditing && identity.identityId !== selectedIdentityId
                }
                onClick={() => selectIdentity(identity.identityId)}
              >
                Administrar
              </button>
            </li>
          ))}
        </ul>
      )}

      {selectedIdentity && (
        <section
          ref={identityDetailRef}
          className="identity-detail"
          tabIndex={-1}
          aria-label={
            "Administrar identidad " + selectedIdentity.operationalName
          }
        >
          <div className="section-heading">
            <div>
              <h3>{selectedIdentity.operationalName}</h3>
              <p>
                <span>{selectedIdentity.isActive ? "Activa" : "Inactiva"}</span>
                {" · Acceso "}
                <span>
                  {selectedIdentity.hasLocalCredential
                    ? "configurado"
                    : "sin configurar"}
                </span>
              </p>
              {selectedIdentity.loginIdentifier && (
                <p>Usuario de acceso: {selectedIdentity.loginIdentifier}</p>
              )}
            </div>
            <button
              type="button"
              className="secondary-button"
              disabled={identityEditing}
              onClick={returnToIdentityList}
            >
              Volver al listado
            </button>
          </div>

          <section className="identity-detail-group">
            <h4>Responsabilidades</h4>
            <ul
              className="identity-detail-actions"
              aria-label={
                "Responsabilidades de " + selectedIdentity.operationalName
              }
            >
              {FUNCTIONAL_RESPONSIBILITIES.map((responsibility) => {
                const isAssigned =
                  selectedIdentity.responsibilities.includes(responsibility);
                return (
                  <li key={responsibility}>
                    <span>
                      {`${responsibilityLabels[responsibility]}: ${isAssigned ? "Asignada" : "No asignada"}`}
                    </span>
                    <button
                      className="secondary-button"
                      type="button"
                      onClick={() =>
                        void startIdentityMutation(
                          isAssigned ? "revoke" : "assign",
                          selectedIdentity,
                          responsibility,
                        )
                      }
                      disabled={
                        isMutatingIdentity || uncertainMutation !== null
                      }
                      aria-label={
                        (isAssigned ? "Quitar " : "Asignar ") +
                        responsibilityLabels[responsibility] +
                        " a " +
                        selectedIdentity.operationalName
                      }
                    >
                      {isAssigned ? "Quitar" : "Asignar"}
                    </button>
                  </li>
                );
              })}
            </ul>
          </section>

          <section className="identity-detail-group">
            <h4>Destinos habilitados</h4>
            <p>
              Un destino de preparación es un lugar de trabajo, no un cargo ni
              una persona. Esta identidad puede atender únicamente los destinos
              habilitados cuando también tiene Preparación asignada.
            </p>
            {!selectedIdentity.responsibilities.includes("Preparation") && (
              <p>
                Asigná Preparación para habilitar destinos a esta identidad.
              </p>
            )}
            <ul
              className="identity-detail-actions"
              aria-label={
                "Destinos habilitados de " + selectedIdentity.operationalName
              }
            >
              {preparationResponsibilities
                .filter(
                  (responsibility) =>
                    selectedIdentity.responsibilities.includes("Preparation") ||
                    selectedIdentity.preparationEnablements.includes(
                      responsibility.id,
                    ),
                )
                .map((responsibility) => {
                  const isEnabled =
                    selectedIdentity.preparationEnablements.includes(
                      responsibility.id,
                    );
                  return (
                    <li key={responsibility.id}>
                      <span>
                        {`${responsibility.operationalName}: ${isEnabled ? "Habilitada" : "No habilitada"}`}
                      </span>
                      {!selectedIdentity.responsibilities.includes(
                        "Preparation",
                      ) && <small>No utilizable sin Preparación</small>}
                      <button
                        className="secondary-button"
                        type="button"
                        onClick={() =>
                          void startEnablementMutation(
                            isEnabled ? "revoke" : "grant",
                            selectedIdentity,
                            responsibility,
                          )
                        }
                        disabled={
                          isMutatingEnablement ||
                          uncertainEnablementMutation !== null
                        }
                        aria-label={
                          (isEnabled
                            ? "Quitar habilitación "
                            : "Habilitar destino ") +
                          responsibility.operationalName +
                          " a " +
                          selectedIdentity.operationalName
                        }
                      >
                        {isEnabled
                          ? "Quitar habilitación"
                          : "Habilitar destino"}
                      </button>
                    </li>
                  );
                })}
              {!isPreparationResponsibilitiesLoading &&
                selectedIdentity.preparationEnablements
                  .filter(
                    (id) =>
                      !preparationResponsibilities.some(
                        (responsibility) => responsibility.id === id,
                      ),
                  )
                  .map((id) => (
                    <li key={id}>
                      <span>Destino de preparación desconocido</span>
                      <details>
                        <summary>Identificador técnico</summary>
                        <span className="technical-reference">{id}</span>
                      </details>
                    </li>
                  ))}
            </ul>
          </section>

          <section className="identity-detail-group identity-sensitive-actions">
            <h4>Acceso y acciones sensibles</h4>
            <div className="identity-action-buttons">
              <button
                className="secondary-button"
                type="button"
                onClick={() =>
                  selectedIdentity.isActive
                    ? setDeactivateTarget(selectedIdentity)
                    : void startIdentityMutation("activate", selectedIdentity)
                }
                disabled={isMutatingIdentity || uncertainMutation !== null}
                aria-label={
                  (selectedIdentity.isActive ? "Desactivar " : "Activar ") +
                  selectedIdentity.operationalName
                }
              >
                {selectedIdentity.isActive ? "Desactivar" : "Activar"}
              </button>
              <button
                className="secondary-button"
                type="button"
                onClick={() => {
                  if (!isSettingCredential && uncertainCredential === null) {
                    setCredentialEditor({
                      identityId: selectedIdentity.identityId,
                      operationalName: selectedIdentity.operationalName,
                      hasLocalCredential: selectedIdentity.hasLocalCredential,
                      changeLoginIdentifier:
                        !selectedIdentity.hasLocalCredential,
                      currentLoginIdentifier:
                        selectedIdentity.loginIdentifier ?? "",
                      loginIdentifier: "",
                      secret: "",
                    });
                    setCredentialNotice(null);
                  }
                }}
                disabled={isSettingCredential || uncertainCredential !== null}
                aria-label={
                  (selectedIdentity.hasLocalCredential
                    ? "Cambiar acceso de "
                    : "Configurar acceso de ") +
                  selectedIdentity.operationalName
                }
              >
                {selectedIdentity.hasLocalCredential
                  ? "Cambiar acceso"
                  : "Configurar acceso"}
              </button>
              <button
                className="secondary-button"
                type="button"
                onClick={() => {
                  if (uncertainRename === null && !isRenaming) {
                    setRenameEditor({
                      identityId: selectedIdentity.identityId,
                      operationalName: selectedIdentity.operationalName,
                    });
                    setRenameNotice(null);
                  }
                }}
                disabled={isRenaming || uncertainRename !== null}
                aria-label={
                  "Cambiar nombre de " + selectedIdentity.operationalName
                }
              >
                Cambiar nombre
              </button>
              <button
                type="button"
                className="danger-button"
                onClick={() => setDeleteTarget(selectedIdentity)}
                disabled={isMutatingIdentity || uncertainMutation !== null}
                aria-label={
                  "Eliminar definitivamente " + selectedIdentity.operationalName
                }
              >
                Eliminar definitivamente
              </button>
            </div>
          </section>
        </section>
      )}

      {deactivateTarget && (
        <section
          aria-label={`Confirmar desactivación de ${deactivateTarget.operationalName}`}
          onKeyDown={(event) => {
            if (
              event.key === "Escape" &&
              !isMutatingIdentity &&
              !uncertainMutation
            )
              setDeactivateTarget(null);
          }}
        >
          <h3 id="identity-deactivation-title" tabIndex={-1}>
            Desactivar “{deactivateTarget.operationalName}”
          </h3>
          <p>
            No podrá operar y se cerrarán sus sesiones. Su identidad y las
            operaciones registradas permanecen.
          </p>
          {deactivateTarget.identityId === currentIdentityId && (
            <p>Estás desactivando tu propio acceso. Esta sesión se cerrará.</p>
          )}
          <button
            type="button"
            className="danger-button"
            disabled={isMutatingIdentity || uncertainMutation !== null}
            onClick={() =>
              void startIdentityMutation("deactivate", deactivateTarget)
            }
          >
            Confirmar desactivación
          </button>
          <button
            type="button"
            disabled={isMutatingIdentity || uncertainMutation !== null}
            onClick={() => setDeactivateTarget(null)}
          >
            Volver
          </button>
        </section>
      )}
      {deleteTarget && uncertainMutation === null && (
        <SensitiveActionDialog
          objectName={deleteTarget.operationalName}
          consequence="Se quitará esta identidad y su acceso de la configuración actual. Sólo puede eliminarse si no tiene operaciones cuya atribución deba conservarse."
          busy={isMutatingIdentity}
          fallbackFocusId="general-configuration-title"
          feedback={
            mutationNotice && (
              <p
                role={
                  mutationNotice.kind === "functional-error"
                    ? "alert"
                    : "status"
                }
              >
                {mutationNotice.message}
              </p>
            )
          }
          onConfirm={() => void startIdentityMutation("delete", deleteTarget)}
          onClose={() => setDeleteTarget(null)}
        />
      )}

      {renameEditor && uncertainRename === null && (
        <form
          onSubmit={(event) => void handleRename(event)}
          aria-label="Cambiar nombre"
          onKeyDown={(event) => {
            if (event.key === "Escape" && !isRenaming && !uncertainRename)
              setRenameEditor(null);
          }}
        >
          <label htmlFor="renamed-identity-operational-name">
            Nuevo nombre
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
        <p
          className={`notice notice--${credentialNotice.kind}`}
          role={
            credentialNotice.kind === "functional-error" ? "alert" : "status"
          }
        >
          {credentialNotice.message}
        </p>
      )}
      {credentialEditor && uncertainCredential === null && (
        <form
          onSubmit={(event) => void handleCredential(event)}
          onKeyDown={(event) => {
            if (
              event.key === "Escape" &&
              !isSettingCredential &&
              !uncertainCredential
            )
              clearCredentialEditor();
          }}
          aria-label={`${credentialEditor.hasLocalCredential ? "Cambiar" : "Configurar"} acceso de ${credentialEditor.operationalName}`}
        >
          <h3>
            {credentialEditor.hasLocalCredential ? "Cambiar" : "Configurar"}{" "}
            acceso de {credentialEditor.operationalName}
          </h3>
          {credentialEditor.hasLocalCredential && (
            <p>
              Usuario de acceso actual:{" "}
              {credentialEditor.currentLoginIdentifier || "no disponible"}
            </p>
          )}
          <p>
            {credentialEditor.hasLocalCredential
              ? "Al guardar, se revocarán todas las sesiones de esta persona. Tendrá que volver a ingresar."
              : "Esta persona necesitará este usuario y contraseña para ingresar."}
          </p>
          {(!credentialEditor.hasLocalCredential ||
            credentialEditor.changeLoginIdentifier) && (
            <>
              <label htmlFor="credential-login-identifier">
                Usuario de acceso
              </label>
              <input
                id="credential-login-identifier"
                autoComplete="off"
                value={credentialEditor.loginIdentifier}
                onChange={(event) =>
                  setCredentialEditor(
                    (current) =>
                      current && {
                        ...current,
                        loginIdentifier: event.target.value,
                      },
                  )
                }
                disabled={isSettingCredential}
              />
            </>
          )}
          {credentialEditor.hasLocalCredential &&
            !credentialEditor.changeLoginIdentifier && (
              <button
                type="button"
                className="secondary-button"
                onClick={() =>
                  setCredentialEditor(
                    (current) =>
                      current && { ...current, changeLoginIdentifier: true },
                  )
                }
              >
                Cambiar usuario de acceso
              </button>
            )}
          <label htmlFor="credential-secret">Nueva contraseña</label>
          <input
            id="credential-secret"
            autoComplete="new-password"
            type="password"
            value={credentialEditor.secret}
            onChange={(event) =>
              setCredentialEditor(
                (current) =>
                  current && { ...current, secret: event.target.value },
              )
            }
            disabled={isSettingCredential}
            required
          />
          <button type="submit" disabled={isSettingCredential}>
            {isSettingCredential ? "Actualizando…" : "Guardar acceso"}
          </button>
          <button
            type="button"
            className="secondary-button"
            onClick={clearCredentialEditor}
            disabled={isSettingCredential}
          >
            Cancelar
          </button>
        </form>
      )}
      {uncertainCredential && (
        <div
          className="uncertain-intention"
          role="region"
          aria-label="Credencial con resultado no confirmado"
        >
          <h3>Acceso pendiente de confirmación</h3>
          <p>
            Persona:{" "}
            {identities.find(
              (identity) =>
                identity.identityId === uncertainCredential.identityId,
            )?.operationalName ?? "nombre no disponible"}
          </p>
          <p>
            No pudimos confirmar si se guardó el acceso. Podés reintentar esta
            operación sin duplicarla.
          </p>
          <button
            type="button"
            onClick={() => void submitCredential(uncertainCredential)}
            disabled={isSettingCredential}
          >
            Reintentar esta operación
          </button>
          <button
            type="button"
            className="secondary-button"
            onClick={clearCredentialEditor}
            disabled={isSettingCredential}
          >
            Dejar de reintentar
          </button>
          <p>
            Esto no deshace la operación. Su resultado sigue sin confirmarse.
          </p>
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
          <p>
            El reintento conserva los mismos datos sin duplicar la operación.
          </p>
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
              Dejar de reintentar
            </button>
            <p>
              Esto no deshace la operación. Su resultado sigue sin confirmarse.
            </p>
          </div>
        </div>
      )}

      {uncertainMutation && (
        <div
          className="uncertain-intention"
          role="region"
          aria-label="Actualización de identidad con resultado no confirmado"
        >
          <h3>Actualización pendiente de confirmación</h3>
          <p>{mutationLabel(uncertainMutation)}</p>
          <p>
            El reintento conserva los mismos datos sin duplicar la operación.
          </p>
          <div className="intention-actions">
            <button
              type="button"
              onClick={() => void submitIdentityMutation(uncertainMutation)}
              disabled={isMutatingIdentity}
            >
              Reintentar esta operación
            </button>
            <button
              className="secondary-button"
              type="button"
              onClick={() => setUncertainMutation(null)}
              disabled={isMutatingIdentity}
            >
              Dejar de reintentar
            </button>
            <p>
              Esto no deshace la operación. Su resultado sigue sin confirmarse.
            </p>
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
          <p>
            El reintento conserva los mismos datos sin duplicar la operación.
          </p>
          <div className="intention-actions">
            <button
              type="button"
              onClick={() =>
                void submitEnablementMutation(uncertainEnablementMutation)
              }
              disabled={isMutatingEnablement}
            >
              Reintentar esta operación
            </button>
            <button
              className="secondary-button"
              type="button"
              onClick={() => setUncertainEnablementMutation(null)}
              disabled={isMutatingEnablement}
            >
              Dejar de reintentar
            </button>
            <p>
              Esto no deshace la operación. Su resultado sigue sin confirmarse.
            </p>
          </div>
        </div>
      )}
    </section>
  );
}
