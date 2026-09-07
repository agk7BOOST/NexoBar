import { useCallback, useEffect, useState } from "react";
import { CatalogPanel } from "./catalog/CatalogPanel.tsx";
import { listProducts, type Product } from "./catalog/catalogClient.ts";
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
  SessionProblemError,
  type CurrentIdentity,
} from "./identity/sessionClient.ts";
import { PreparationPanel } from "./preparation/PreparationPanel.tsx";
import { DeliveryPanel } from "./delivery/DeliveryPanel.tsx";
import { InventoryPanel } from "./inventory/InventoryPanel.tsx";
import type { OrderResponse } from "./orderOperations/orderOperationsClient.ts";

type AuthState =
  | { status: "loading" }
  | { status: "unauthenticated" }
  | { status: "authenticated"; identity: CurrentIdentity };

function App() {
  const [products, setProducts] = useState<Product[]>([]);
  const [isLoading, setIsLoading] = useState(true);
  const [loadError, setLoadError] = useState<string | null>(null);
  const [activeOperationalReference, setActiveOperationalReference] = useState<
    string | null
  >(null);
  const [requestedLookup, setRequestedLookup] =
    useState<RequestedOrderLookup>();
  const [requestedTarget, setRequestedTarget] = useState<OrderTargetRequest>();
  const [deliveryOperationalReference, setDeliveryOperationalReference] =
    useState<string | null>(null);
  const [authState, setAuthState] = useState<AuthState>({ status: "loading" });
  const [terminalOrders, setTerminalOrders] = useState<Record<string, boolean>>(
    {},
  );
  const [deliveryBusy, setDeliveryBusy] = useState<Record<string, boolean>>({});
  const [preparationBusy, setPreparationBusy] = useState<string[]>([]);
  const [preparationRefresh, setPreparationRefresh] = useState(0);
  const [deliveryRefresh, setDeliveryRefresh] = useState(0);
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
      [order.operationalReference]: order.isFrozen || order.isClosed,
    }));
  }, []);
  const rememberEndingBusy = useCallback((reference: string, busy: boolean) => {
    setEndingOrders((current) => ({ ...current, [reference]: busy }));
  }, []);

  const reloadProducts = useCallback(async () => {
    setIsLoading(true);
    setLoadError(null);

    try {
      setProducts(await listProducts());
    } catch {
      setLoadError("No se pudo cargar el listado de productos.");
    } finally {
      setIsLoading(false);
    }
  }, []);

  useEffect(() => {
    let isCurrent = true;

    void listProducts().then(
      (loadedProducts) => {
        if (isCurrent) {
          setProducts(loadedProducts);
          setIsLoading(false);
        }
      },
      () => {
        if (isCurrent) {
          setLoadError("No se pudo cargar el listado de productos.");
          setIsLoading(false);
        }
      },
    );

    return () => {
      isCurrent = false;
    };
  }, []);

  useEffect(() => {
    let isCurrent = true;
    void getCurrentIdentity().then(
      (identity) => {
        if (isCurrent) setAuthState({ status: "authenticated", identity });
      },
      (error: unknown) => {
        if (!isCurrent) return;
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
    discardAntiforgeryToken();
    setAuthState({ status: "unauthenticated" });
    setEndingOrders({});
    setDeliveryBusy({});
    setPreparationBusy([]);
  }, []);

  function activateOrder(operationalReference: string) {
    setActiveOperationalReference(operationalReference);
    setRequestedTarget(undefined);
  }

  function startNewOrder() {
    setActiveOperationalReference(null);
    setRequestedTarget(undefined);
  }

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

  return (
    <main className="page-shell">
      <header className="page-header">
        <p className="eyebrow">NexoBar</p>
        <h1>Catálogo de productos</h1>
        <p>Alta, consulta y operación con productos vigentes.</p>
      </header>

      {authState.status === "loading" && <p>Cargando sesión…</p>}
      {authState.status === "unauthenticated" && (
        <LoginPanel
          onAuthenticated={(identity) =>
            setAuthState({ status: "authenticated", identity })
          }
        />
      )}
      {authState.status === "authenticated" && (
        <>
          <SessionBar
            identity={authState.identity}
            onLoggedOut={returnToLogin}
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
          <OrderWorkflow
            products={products}
            activeOperationalReference={activeOperationalReference}
            requestedTarget={requestedTarget}
            onActivateOrder={activateOrder}
            onStartNewOrder={startNewOrder}
            onOrderChanged={requestOrderRefresh}
            onUnauthorized={returnToLogin}
            ordinaryMutationsBlocked={
              activeOperationalReference !== null &&
              (terminalOrders[activeOperationalReference] === true ||
                endingOrders[activeOperationalReference] === true ||
                deliveryBusy[activeOperationalReference] === true ||
                preparationBusy.includes(activeOperationalReference))
            }
          />
        </>
      )}

      <CatalogPanel
        products={products}
        isLoading={isLoading}
        loadError={loadError}
        reloadProducts={reloadProducts}
      />

      <OrderLookup
        key={
          authState.status === "authenticated"
            ? authState.identity.identityId
            : "anonymous"
        }
        products={products}
        requestedLookup={requestedLookup}
        activeOperationalReference={activeOperationalReference}
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
        />
      )}
    </main>
  );
}

export default App;
