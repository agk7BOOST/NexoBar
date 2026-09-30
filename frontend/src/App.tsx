import { useCallback, useEffect, useRef, useState } from "react";
import { CatalogPanel } from "./catalog/CatalogPanel.tsx";
import { GeneralConfigurationPanel } from "./generalConfiguration/GeneralConfigurationPanel.tsx";
import {
  OrderLookup,
  type RequestedOrderLookup,
} from "./orderOperations/OrderLookup.tsx";
import {
  OrderWorkflow,
  type OrderTargetRequest,
} from "./orderOperations/OrderWorkflow.tsx";
import { LoginPanel } from "./identity/LoginPanel.tsx";
import { SessionBar } from "./identity/SessionBar.tsx";
import {
  discardAntiforgeryToken,
  getCurrentIdentity,
  hasResponsibility,
  SessionProblemError,
  type CurrentIdentity,
} from "./identity/sessionClient.ts";
import { PreparationPanel } from "./preparation/PreparationPanel.tsx";
import { DeliveryPanel } from "./delivery/DeliveryPanel.tsx";
import { InventoryPanel } from "./inventory/InventoryPanel.tsx";
import {
  isOrderCompletelyCancelled,
  type OrderResponse,
} from "./orderOperations/orderOperationsClient.ts";
import { OperationalInterventionPanel } from "./orderOperations/OperationalInterventionPanel.tsx";
import { NotificationSseProvider } from "./notifications/NotificationSseProvider.tsx";
import { ProductAvailabilityInterventionPanel } from "./availability/ProductAvailabilityInterventionPanel.tsx";

type AuthState =
  | { status: "loading" }
  | { status: "unauthenticated" }
  | { status: "error" }
  | { status: "authenticated"; identity: CurrentIdentity };

type Workspace =
  "orders" | "preparation" | "products" | "inventory" | "configuration";

interface InterventionTargetRequest {
  incorporationId: string;
  contentOrdinal: number;
  sequence: number;
}

const workspaceLabels: Record<Workspace, string> = {
  orders: "Pedidos",
  preparation: "Preparación",
  products: "Productos",
  inventory: "Inventario",
  configuration: "Configuración",
};

const workspaceDescriptions: Record<Workspace, string> = {
  orders: "Composición, consulta, entrega y finalización de pedidos.",
  preparation: "Trabajo por destino habilitado.",
  products: "Catálogo y disponibilidad de productos.",
  inventory: "Estado, movimientos y configuración de inventario.",
  configuration: "Identidades, contextos y destinos de preparación.",
};

function App() {
  const [selectedWorkspace, setSelectedWorkspace] = useState<Workspace | null>(
    null,
  );
  const [activeOperationalReference, setActiveOperationalReference] = useState<
    string | null
  >(null);
  const [activeOrderId, setActiveOrderId] = useState<string | null>(null);
  const [requestedLookup, setRequestedLookup] =
    useState<RequestedOrderLookup>();
  const [requestedTarget, setRequestedTarget] = useState<OrderTargetRequest>();
  const [requestedIntervention, setRequestedIntervention] =
    useState<InterventionTargetRequest>();
  const [orderNavigation, setOrderNavigation] = useState<{
    section: "composition" | "delivery";
    sequence: number;
  }>();
  const [orderContexts, setOrderContexts] = useState<Record<string, string>>(
    {},
  );
  const [deliveryOperationalReference, setDeliveryOperationalReference] =
    useState<string | null>(null);
  const [authState, setAuthState] = useState<AuthState>({ status: "loading" });
  const [sessionReadRevision, setSessionReadRevision] = useState(0);
  const [loginReason, setLoginReason] = useState<string>();
  const identityGeneration = useRef(0);
  const [identityLifecycle, setIdentityLifecycle] = useState(0);
  const [terminalOrders, setTerminalOrders] = useState<Record<string, boolean>>(
    {},
  );
  const [deliveryBusy, setDeliveryBusy] = useState<Record<string, boolean>>({});
  const [preparationBusy, setPreparationBusy] = useState<string[]>([]);
  const [preparationRefresh, setPreparationRefresh] = useState(0);
  const [deliveryRefresh, setDeliveryRefresh] = useState(0);
  const [endingRefresh, setEndingRefresh] = useState(0);
  const rememberDeliveryBusy = useCallback(
    (reference: string, busy: boolean) => {
      setDeliveryBusy((current) => ({ ...current, [reference]: busy }));
    },
    [],
  );
  const refreshAfterPreparation = useCallback(
    () => setDeliveryRefresh((current) => current + 1),
    [],
  );
  const [endingOrders, setEndingOrders] = useState<Record<string, boolean>>({});
  const rememberOrderState = useCallback((order: OrderResponse) => {
    setOrderContexts((current) => ({
      ...current,
      [order.operationalReference]: order.context,
    }));
    setTerminalOrders((current) => ({
      ...current,
      [order.operationalReference]:
        order.isFrozen || order.isClosed || isOrderCompletelyCancelled(order),
    }));
  }, []);
  const rememberEndingBusy = useCallback((reference: string, busy: boolean) => {
    setEndingOrders((current) => ({ ...current, [reference]: busy }));
    if (!busy) {
      setEndingRefresh((current) => current + 1);
      setPreparationRefresh((current) => current + 1);
      setDeliveryRefresh((current) => current + 1);
    }
  }, []);

  useEffect(() => {
    let isCurrent = true;
    const generation = identityGeneration.current;
    void getCurrentIdentity().then(
      (identity) => {
        if (isCurrent && identityGeneration.current === generation) {
          setAuthState({ status: "authenticated", identity });
        }
      },
      (error: unknown) => {
        if (!isCurrent || identityGeneration.current !== generation) return;
        if (error instanceof SessionProblemError && error.status === 401) {
          discardAntiforgeryToken();
          setAuthState({ status: "unauthenticated" });
        } else {
          setAuthState({ status: "error" });
        }
      },
    );
    return () => {
      isCurrent = false;
    };
  }, [sessionReadRevision]);

  const returnToLogin = useCallback(() => {
    setLoginReason(undefined);
    identityGeneration.current += 1;
    setIdentityLifecycle((current) => current + 1);
    discardAntiforgeryToken();
    setAuthState({ status: "unauthenticated" });
    setActiveOperationalReference(null);
    setActiveOrderId(null);
    setDeliveryOperationalReference(null);
    setRequestedTarget(undefined);
    setRequestedIntervention(undefined);
    setOrderNavigation(undefined);
    setOrderContexts({});
    setEndingOrders({});
    setDeliveryBusy({});
    setPreparationBusy([]);
    setSelectedWorkspace(null);
  }, []);
  const returnAfterAccessChanged = useCallback(() => {
    returnToLogin();
    setLoginReason("Tu acceso fue actualizado. Volvé a ingresar.");
  }, [returnToLogin]);

  const setAuthenticatedIdentity = useCallback((identity: CurrentIdentity) => {
    identityGeneration.current += 1;
    setIdentityLifecycle((current) => current + 1);
    setAuthState({ status: "authenticated", identity });
    setSelectedWorkspace(null);
  }, []);

  const refreshCurrentIdentity = useCallback(async () => {
    const generation = identityGeneration.current;
    try {
      const identity = await getCurrentIdentity();
      if (identityGeneration.current === generation) {
        setAuthState({ status: "authenticated", identity });
      }
    } catch (error) {
      if (error instanceof SessionProblemError && error.status === 401) {
        returnToLogin();
      }
    }
  }, [returnToLogin]);

  const handleForbidden = useCallback(() => {
    void refreshCurrentIdentity();
  }, [refreshCurrentIdentity]);

  function activateOrder(operationalReference: string) {
    setActiveOperationalReference(operationalReference);
    setActiveOrderId(null);
    setRequestedTarget(undefined);
  }

  function startNewOrder() {
    setActiveOperationalReference(null);
    setActiveOrderId(null);
    setRequestedTarget(undefined);
  }

  const retireActiveOrder = useCallback((operationalReference: string) => {
    setActiveOperationalReference((current) =>
      current === operationalReference ? null : current,
    );
    setActiveOrderId(null);
    setDeliveryOperationalReference((current) =>
      current === operationalReference ? null : current,
    );
    setRequestedTarget(undefined);
  }, []);
  const rememberActiveOrderId = useCallback(
    (_operationalReference: string, orderId: string) =>
      setActiveOrderId(orderId),
    [],
  );

  const requestOrderRefresh = useCallback((operationalReference: string) => {
    setPreparationRefresh((current) => current + 1);
    setRequestedLookup((current) => ({
      operationalReference,
      sequence: (current?.sequence ?? 0) + 1,
    }));
  }, []);

  function requestContinueOrder(operationalReference: string) {
    setRequestedTarget((current) => ({
      operationalReference,
      sequence: (current?.sequence ?? 0) + 1,
    }));
    setOrderNavigation((current) => ({
      section: "composition",
      sequence: (current?.sequence ?? 0) + 1,
    }));
  }

  function requestDelivery(operationalReference: string) {
    setDeliveryOperationalReference(operationalReference);
    setOrderNavigation((current) => ({
      section: "delivery",
      sequence: (current?.sequence ?? 0) + 1,
    }));
  }

  function requestIntervention(
    incorporationId: string,
    contentOrdinal: number,
  ) {
    if (!canInterveneAvailability) return;
    setRequestedIntervention((current) => ({
      incorporationId,
      contentOrdinal,
      sequence: (current?.sequence ?? 0) + 1,
    }));
    setSelectedWorkspace("preparation");
  }

  const identity =
    authState.status === "authenticated" ? authState.identity : null;
  const canConfigureCatalog =
    identity !== null && hasResponsibility(identity, "CatalogConfiguration");
  const canConfigureGeneral =
    identity !== null && hasResponsibility(identity, "GeneralConfiguration");
  const canComposeOrders =
    identity !== null &&
    hasResponsibility(identity, "OrderOperationsAndBasicClosure");
  const canInterveneAvailability =
    identity !== null && hasResponsibility(identity, "OperationalIntervention");
  const canPrepare =
    identity !== null && hasResponsibility(identity, "Preparation");
  const canOperateInventory =
    identity !== null && hasResponsibility(identity, "InventoryOperation");
  const canConfigureInventory =
    identity !== null && hasResponsibility(identity, "InventoryConfiguration");
  const interventionOnly = canInterveneAvailability && !canPrepare;
  const preparationLabel = interventionOnly
    ? "Intervención en preparación"
    : workspaceLabels.preparation;
  const labelForWorkspace = (workspace: Workspace) =>
    workspace === "preparation" ? preparationLabel : workspaceLabels[workspace];
  const workspaces: Workspace[] = [
    ...(canComposeOrders ? (["orders"] as const) : []),
    ...(canPrepare ? (["preparation"] as const) : []),
    ...(canConfigureCatalog || canInterveneAvailability
      ? (["products"] as const)
      : []),
    ...(interventionOnly ? (["preparation"] as const) : []),
    ...(canOperateInventory || canConfigureInventory
      ? (["inventory"] as const)
      : []),
    ...(canConfigureGeneral ? (["configuration"] as const) : []),
  ];
  const activeWorkspace =
    selectedWorkspace !== null && workspaces.includes(selectedWorkspace)
      ? selectedWorkspace
      : workspaces[0];
  const workspaceHeading = useRef<HTMLHeadingElement>(null);
  const previousWorkspace = useRef<Workspace | undefined>(undefined);
  useEffect(() => {
    if (
      previousWorkspace.current &&
      previousWorkspace.current !== activeWorkspace
    ) {
      workspaceHeading.current?.focus();
      workspaceHeading.current?.scrollIntoView?.({ block: "start" });
    }
    previousWorkspace.current = activeWorkspace;
  }, [activeWorkspace]);
  useEffect(() => {
    if (activeWorkspace !== "orders" || !orderNavigation) return;
    const target = document.getElementById(
      orderNavigation.section === "composition"
        ? "composition-title"
        : "delivery-heading",
    );
    target?.focus();
    target?.scrollIntoView?.({ block: "start" });
  }, [activeWorkspace, orderNavigation]);
  useEffect(() => {
    if (activeWorkspace !== "preparation" || !requestedIntervention) return;
    const target = document.getElementById("intervention-heading");
    target?.focus();
    target?.scrollIntoView?.({ block: "start" });
  }, [activeWorkspace, requestedIntervention]);
  const canRequestUnavailableProductException =
    canComposeOrders &&
    identity !== null &&
    hasResponsibility(identity, "OperationalIntervention");

  return (
    <NotificationSseProvider
      key={`sse-session:${authState.status === "authenticated" ? authState.identity.identityId : "anonymous"}`}
      identityId={
        authState.status === "authenticated"
          ? authState.identity.identityId
          : null
      }
    >
      <main
        className={
          authState.status === "authenticated" ? "app-shell" : "auth-shell"
        }
      >
        {authState.status !== "authenticated" && (
          <header className="auth-header">
            <span className="brand-mark" aria-hidden="true">
              N
            </span>
            <p className="eyebrow">NexoBar · Operación</p>
            <h1>Un espacio claro para cada tarea.</h1>
            <p>Ingresá con tu usuario y contraseña.</p>
          </header>
        )}
        {authState.status === "loading" && (
          <p role="status">Cargando sesión…</p>
        )}
        {authState.status === "unauthenticated" && (
          <LoginPanel
            reason={loginReason}
            onAuthenticated={setAuthenticatedIdentity}
          />
        )}
        {authState.status === "error" && (
          <section className="panel">
            <p role="alert">No pudimos comprobar tu sesión.</p>
            <button
              type="button"
              onClick={() => {
                setAuthState({ status: "loading" });
                setSessionReadRevision((value) => value + 1);
              }}
            >
              Reintentar
            </button>
          </section>
        )}
        {authState.status === "authenticated" && (
          <>
            <a className="skip-link" href="#workspace-title">
              Ir al contenido
            </a>
            <div className="app-sidebar">
              <div className="app-brand">
                <span className="brand-mark" aria-hidden="true">
                  N
                </span>
                <span>
                  <strong>NexoBar</strong>
                  <small>Operación</small>
                </span>
              </div>
              <nav className="workspace-nav" aria-label="Espacios de trabajo">
                {workspaces.map((workspace) => (
                  <button
                    key={workspace}
                    type="button"
                    className="workspace-nav-item"
                    aria-current={
                      activeWorkspace === workspace ? "page" : undefined
                    }
                    onClick={() => setSelectedWorkspace(workspace)}
                  >
                    {labelForWorkspace(workspace)}
                  </button>
                ))}
              </nav>
              <p className="sidebar-caption">Estado vigente desde NexoBar</p>
            </div>
            <div className="app-main">
              <header className="app-topbar">
                <span className="topbar-context">Espacio de trabajo</span>
                <SessionBar
                  identity={authState.identity}
                  onLoggedOut={returnToLogin}
                />
              </header>
              {activeWorkspace ? (
                <div className="workspace-content">
                  <header className="workspace-header">
                    <p className="eyebrow">
                      Operación · {labelForWorkspace(activeWorkspace)}
                    </p>
                    <h1
                      id="workspace-title"
                      ref={workspaceHeading}
                      tabIndex={-1}
                    >
                      {labelForWorkspace(activeWorkspace)}
                    </h1>
                    <p>
                      {activeWorkspace === "preparation" && interventionOnly
                        ? "Intervenciones puntuales sobre trabajo de preparación."
                        : activeWorkspace === "preparation" &&
                            canInterveneAvailability
                          ? "Trabajo por destino e intervenciones operacionales."
                          : workspaceDescriptions[activeWorkspace]}
                    </p>
                  </header>
                  <div
                    className="workspace"
                    hidden={activeWorkspace !== "preparation"}
                  >
                    {canInterveneAvailability && (
                      <OperationalInterventionPanel
                        key={authState.identity.identityId}
                        requestedTarget={requestedIntervention}
                        onUnauthorized={returnToLogin}
                        isOrderBlocked={(reference) =>
                          terminalOrders[reference] === true ||
                          endingOrders[reference] === true
                        }
                      />
                    )}
                    {canPrepare && (
                      <PreparationPanel
                        onIntervene={
                          canInterveneAvailability
                            ? requestIntervention
                            : undefined
                        }
                        refreshSequence={preparationRefresh}
                        onBusyOrdersChange={setPreparationBusy}
                        onWorkChanged={refreshAfterPreparation}
                        onUnauthorized={returnToLogin}
                        isOrderBlocked={(reference) =>
                          terminalOrders[reference] === true ||
                          endingOrders[reference] === true ||
                          deliveryBusy[reference] === true
                        }
                      />
                    )}
                  </div>
                  <div
                    className="workspace"
                    hidden={activeWorkspace !== "inventory"}
                  >
                    {(canOperateInventory || canConfigureInventory) && (
                      <InventoryPanel
                        onUnauthorized={returnToLogin}
                        canOperate={canOperateInventory}
                        canConfigure={canConfigureInventory}
                      />
                    )}
                  </div>
                  <div
                    className="workspace"
                    hidden={activeWorkspace !== "products"}
                  >
                    {canInterveneAvailability && (
                      <ProductAvailabilityInterventionPanel
                        key={`availability-intervention:${identity.identityId}:${identityLifecycle}`}
                        onUnauthorized={returnToLogin}
                        onForbidden={handleForbidden}
                      />
                    )}
                    {canConfigureCatalog && (
                      <CatalogPanel
                        key={`admin-catalog:${identity.identityId}:${identityLifecycle}:${identity.responsibilities.join(",")}`}
                        onUnauthorized={returnToLogin}
                      />
                    )}
                  </div>
                  <div
                    className="workspace"
                    hidden={activeWorkspace !== "orders"}
                  >
                    {canComposeOrders && (
                      <OrderWorkflow
                        key={`operational-products:${identity.identityId}:${identityLifecycle}`}
                        endingRefreshSequence={endingRefresh}
                        activeOperationalReference={activeOperationalReference}
                        activeOrderContext={
                          activeOperationalReference === null
                            ? undefined
                            : orderContexts[activeOperationalReference]
                        }
                        activeOrderId={activeOrderId}
                        requestedTarget={requestedTarget}
                        onActivateOrder={activateOrder}
                        onActiveOrderId={rememberActiveOrderId}
                        onStartNewOrder={startNewOrder}
                        onOrderChanged={requestOrderRefresh}
                        onActiveOrderRetired={retireActiveOrder}
                        onUnauthorized={returnToLogin}
                        canRequestUnavailableProductException={
                          canRequestUnavailableProductException
                        }
                        ordinaryMutationsBlocked={
                          activeOperationalReference !== null &&
                          (terminalOrders[activeOperationalReference] ===
                            true ||
                            endingOrders[activeOperationalReference] === true ||
                            deliveryBusy[activeOperationalReference] === true ||
                            preparationBusy.includes(
                              activeOperationalReference,
                            ))
                        }
                      />
                    )}
                    {canComposeOrders && (
                      <OrderLookup
                        key={identity.identityId}
                        requestedLookup={requestedLookup}
                        canChangeOrderContext={canComposeOrders}
                        canViewTerminalHistory={canComposeOrders}
                        activeOperationalReference={activeOperationalReference}
                        activeOrderId={activeOrderId}
                        onContinueOrder={requestContinueOrder}
                        onOpenDelivery={requestDelivery}
                        identityId={identity.identityId}
                        onUnauthorized={returnToLogin}
                        onOrderState={rememberOrderState}
                        onEndingBusy={rememberEndingBusy}
                        onActiveOrderRetired={retireActiveOrder}
                        isOrderMutationBusy={(reference) =>
                          deliveryBusy[reference] === true ||
                          preparationBusy.includes(reference)
                        }
                      />
                    )}
                    {canComposeOrders && (
                      <DeliveryPanel
                        onIntervene={
                          canInterveneAvailability
                            ? requestIntervention
                            : undefined
                        }
                        refreshSequence={deliveryRefresh}
                        onBusyChange={rememberDeliveryBusy}
                        operationalReference={deliveryOperationalReference}
                        onUnauthorized={returnToLogin}
                        ordinaryMutationsBlocked={
                          deliveryOperationalReference !== null &&
                          (terminalOrders[deliveryOperationalReference] ===
                            true ||
                            endingOrders[deliveryOperationalReference] ===
                              true ||
                            preparationBusy.includes(
                              deliveryOperationalReference,
                            ))
                        }
                        onOrderChanged={requestOrderRefresh}
                        onOrderRetired={retireActiveOrder}
                      />
                    )}
                  </div>
                  <div
                    className="workspace"
                    hidden={activeWorkspace !== "configuration"}
                  >
                    {canConfigureGeneral && (
                      <GeneralConfigurationPanel
                        onOwnAccessChanged={returnAfterAccessChanged}
                        key={`admin-general:${identity.identityId}:${identityLifecycle}:${identity.responsibilities.join(",")}`}
                        currentIdentityId={identity.identityId}
                        onCurrentIdentityChanged={refreshCurrentIdentity}
                        onUnauthorized={returnToLogin}
                        onForbidden={handleForbidden}
                      />
                    )}
                  </div>
                </div>
              ) : (
                <div className="workspace-content">
                  <h1 id="workspace-title" ref={workspaceHeading} tabIndex={-1}>
                    Sin espacios asignados
                  </h1>
                  <p className="notice">
                    No tenés tareas habilitadas para usar NexoBar. Pedí a quien
                    administra NexoBar que configure tus responsabilidades.
                  </p>
                </div>
              )}
            </div>
          </>
        )}
      </main>
    </NotificationSseProvider>
  );
}

export default App;
