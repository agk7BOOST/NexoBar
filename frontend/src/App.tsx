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
  | { status: "authenticated"; identity: CurrentIdentity };

function App() {
  const [activeOperationalReference, setActiveOperationalReference] = useState<
    string | null
  >(null);
  const [activeOrderId, setActiveOrderId] = useState<string | null>(null);
  const [requestedLookup, setRequestedLookup] =
    useState<RequestedOrderLookup>();
  const [requestedTarget, setRequestedTarget] = useState<OrderTargetRequest>();
  const [deliveryOperationalReference, setDeliveryOperationalReference] =
    useState<string | null>(null);
  const [authState, setAuthState] = useState<AuthState>({ status: "loading" });
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
        }
        setAuthState({ status: "unauthenticated" });
      },
    );
    return () => {
      isCurrent = false;
    };
  }, []);

  const returnToLogin = useCallback(() => {
    identityGeneration.current += 1;
    setIdentityLifecycle((current) => current + 1);
    discardAntiforgeryToken();
    setAuthState({ status: "unauthenticated" });
    setActiveOperationalReference(null);
    setActiveOrderId(null);
    setDeliveryOperationalReference(null);
    setRequestedTarget(undefined);
    setEndingOrders({});
    setDeliveryBusy({});
    setPreparationBusy([]);
  }, []);

  const setAuthenticatedIdentity = useCallback((identity: CurrentIdentity) => {
    identityGeneration.current += 1;
    setIdentityLifecycle((current) => current + 1);
    setAuthState({ status: "authenticated", identity });
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
      <main className="page-shell">
        <header className="page-header">
          <p className="eyebrow">NexoBar</p>
          <h1>Catálogo de productos</h1>
          <p>Alta, consulta y operación con productos vigentes.</p>
        </header>

        {authState.status === "loading" && <p>Cargando sesión…</p>}
        {authState.status === "unauthenticated" && (
          <LoginPanel onAuthenticated={setAuthenticatedIdentity} />
        )}
        {authState.status === "authenticated" && (
          <>
            <SessionBar
              identity={authState.identity}
              onLoggedOut={returnToLogin}
            />
            <OperationalInterventionPanel
              key={authState.identity.identityId}
              onUnauthorized={returnToLogin}
              isOrderBlocked={(reference) =>
                terminalOrders[reference] === true ||
                endingOrders[reference] === true
              }
            />
            <PreparationPanel
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
            <InventoryPanel onUnauthorized={returnToLogin} />
            {canInterveneAvailability && (
              <ProductAvailabilityInterventionPanel
                key={`availability-intervention:${identity.identityId}:${identityLifecycle}`}
                onUnauthorized={returnToLogin}
                onForbidden={() => void refreshCurrentIdentity()}
              />
            )}
            {canComposeOrders && (
              <OrderWorkflow
                key={`operational-products:${identity.identityId}:${identityLifecycle}`}
                endingRefreshSequence={endingRefresh}
                activeOperationalReference={activeOperationalReference}
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
                  (terminalOrders[activeOperationalReference] === true ||
                    endingOrders[activeOperationalReference] === true ||
                    deliveryBusy[activeOperationalReference] === true ||
                    preparationBusy.includes(activeOperationalReference))
                }
              />
            )}
          </>
        )}

        {canConfigureCatalog && identity !== null && (
          <CatalogPanel
            key={`admin-catalog:${identity.identityId}:${identityLifecycle}:${identity.responsibilities.join(",")}`}
            onUnauthorized={returnToLogin}
          />
        )}

        {canConfigureGeneral && identity !== null && (
          <GeneralConfigurationPanel
            key={`admin-general:${identity.identityId}:${identityLifecycle}:${identity.responsibilities.join(",")}`}
            currentIdentityId={identity.identityId}
            onCurrentIdentityChanged={refreshCurrentIdentity}
            onUnauthorized={returnToLogin}
            onForbidden={() => void refreshCurrentIdentity()}
          />
        )}

        <OrderLookup
          key={
            authState.status === "authenticated"
              ? authState.identity.identityId
              : "anonymous"
          }
          requestedLookup={requestedLookup}
          canChangeOrderContext={canComposeOrders}
          canViewTerminalHistory={canComposeOrders}
          activeOperationalReference={activeOperationalReference}
          activeOrderId={activeOrderId}
          onContinueOrder={requestContinueOrder}
          onOpenDelivery={setDeliveryOperationalReference}
          identityId={
            authState.status === "authenticated"
              ? authState.identity.identityId
              : undefined
          }
          onUnauthorized={returnToLogin}
          onOrderState={rememberOrderState}
          onEndingBusy={rememberEndingBusy}
          onActiveOrderRetired={retireActiveOrder}
          isOrderMutationBusy={(reference) =>
            deliveryBusy[reference] === true ||
            preparationBusy.includes(reference)
          }
        />

        {authState.status === "authenticated" && (
          <DeliveryPanel
            refreshSequence={deliveryRefresh}
            onBusyChange={rememberDeliveryBusy}
            operationalReference={deliveryOperationalReference}
            onUnauthorized={returnToLogin}
            ordinaryMutationsBlocked={
              deliveryOperationalReference !== null &&
              (terminalOrders[deliveryOperationalReference] === true ||
                endingOrders[deliveryOperationalReference] === true ||
                preparationBusy.includes(deliveryOperationalReference))
            }
            onOrderChanged={requestOrderRefresh}
            onOrderRetired={retireActiveOrder}
          />
        )}
      </main>
    </NotificationSseProvider>
  );
}

export default App;
