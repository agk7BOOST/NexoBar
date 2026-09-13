import {
  createContext,
  useContext,
  useEffect,
  useRef,
  useState,
  type ReactNode,
} from "react";
import {
  NotificationSseTransport,
  type InventoryOperationChanged,
  type OrderChanged,
  type PreparationDestinationChanged,
} from "./NotificationSseTransport.ts";

const NotificationSseContext = createContext<NotificationSseTransport | null>(null);

interface NotificationSseProviderProps {
  identityId: string | null;
  children: ReactNode;
}

export function NotificationSseProvider({
  identityId,
  children,
}: NotificationSseProviderProps) {
  const transportRef = useRef<NotificationSseTransport | null>(null);
  if (transportRef.current === null) {
    transportRef.current = new NotificationSseTransport();
  }
  const transport = transportRef.current;

  useEffect(() => {
    transport.setActiveIdentity(identityId);
    return () => transport.setActiveIdentity(null);
  }, [identityId, transport]);

  useEffect(() => () => transport.dispose(), [transport]);

  return (
    <NotificationSseContext.Provider value={transport}>
      {children}
    </NotificationSseContext.Provider>
  );
}

function useNotificationSseTransport(): NotificationSseTransport {
  const transport = useContext(NotificationSseContext);
  if (transport === null) {
    throw new Error("Notification SSE subscriptions require the App provider.");
  }
  return transport;
}

export function usePreparationDestinationInvalidation(
  destinationId: string,
  onInvalidated: (notification: PreparationDestinationChanged) => void,
): void {
  const transport = useNotificationSseTransport();
  useEffect(
    () => transport.subscribe(destinationId, onInvalidated),
    [destinationId, onInvalidated, transport],
  );
}

export function usePreparationConnectionGeneration(): number {
  const transport = useNotificationSseTransport();
  const [generation, setGeneration] = useState(transport.connectionGeneration);
  useEffect(() => transport.onConnected(setGeneration), [transport]);
  return generation;
}

export function useOrderInvalidation(
  orderId: string | null,
  onInvalidated: (notification: OrderChanged) => void,
): void {
  const transport = useContext(NotificationSseContext);
  useEffect(() => {
    if (transport === null || orderId === null) return;
    return transport.subscribeOrder(orderId, onInvalidated);
  }, [orderId, onInvalidated, transport]);
}

export function useOrderConnectionGeneration(): number {
  const transport = useContext(NotificationSseContext);
  const [generation, setGeneration] = useState(
    transport?.connectionGeneration ?? 0,
  );
  useEffect(() => {
    if (transport === null) {
      setGeneration(0);
      return;
    }
    setGeneration(transport.connectionGeneration);
    return transport.onConnected(setGeneration);
  }, [transport]);
  return generation;
}

export function useInventoryOperationInvalidation(
  onInvalidated: (notification: InventoryOperationChanged) => void,
): void {
  const transport = useContext(NotificationSseContext);
  useEffect(() => {
    if (transport === null) return;
    return transport.subscribeInventoryOperation(onInvalidated);
  }, [onInvalidated, transport]);
}

export function useInventoryOperationConnectionGeneration(): number {
  const transport = useContext(NotificationSseContext);
  const [generation, setGeneration] = useState(
    transport?.connectionGeneration ?? 0,
  );
  useEffect(() => {
    if (transport === null) {
      setGeneration(0);
      return;
    }
    setGeneration(transport.connectionGeneration);
    return transport.onConnected(setGeneration);
  }, [transport]);
  return generation;
}
