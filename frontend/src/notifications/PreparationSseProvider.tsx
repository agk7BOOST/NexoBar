import {
  createContext,
  useContext,
  useEffect,
  useRef,
  useState,
  type ReactNode,
} from "react";
import {
  PreparationSseTransport,
  type OrderChanged,
  type PreparationDestinationChanged,
} from "./PreparationSseTransport.ts";

const PreparationSseContext = createContext<PreparationSseTransport | null>(null);

interface PreparationSseProviderProps {
  identityId: string | null;
  children: ReactNode;
}

export function PreparationSseProvider({
  identityId,
  children,
}: PreparationSseProviderProps) {
  const transportRef = useRef<PreparationSseTransport | null>(null);
  if (transportRef.current === null) {
    transportRef.current = new PreparationSseTransport();
  }
  const transport = transportRef.current;

  useEffect(() => {
    transport.setActiveIdentity(identityId);
    return () => transport.setActiveIdentity(null);
  }, [identityId, transport]);

  useEffect(() => () => transport.dispose(), [transport]);

  return (
    <PreparationSseContext.Provider value={transport}>
      {children}
    </PreparationSseContext.Provider>
  );
}

function usePreparationSseTransport(): PreparationSseTransport {
  const transport = useContext(PreparationSseContext);
  if (transport === null) {
    throw new Error("Preparation SSE subscriptions require the App provider.");
  }
  return transport;
}

export function usePreparationDestinationInvalidation(
  destinationId: string,
  onInvalidated: (notification: PreparationDestinationChanged) => void,
): void {
  const transport = usePreparationSseTransport();
  useEffect(
    () => transport.subscribe(destinationId, onInvalidated),
    [destinationId, onInvalidated, transport],
  );
}

export function usePreparationConnectionGeneration(): number {
  const transport = usePreparationSseTransport();
  const [generation, setGeneration] = useState(transport.connectionGeneration);
  useEffect(() => transport.onConnected(setGeneration), [transport]);
  return generation;
}

export function useOrderInvalidation(
  orderId: string | null,
  onInvalidated: (notification: OrderChanged) => void,
): void {
  const transport = useContext(PreparationSseContext);
  useEffect(() => {
    if (transport === null || orderId === null) return;
    return transport.subscribeOrder(orderId, onInvalidated);
  }, [orderId, onInvalidated, transport]);
}

export function useOrderConnectionGeneration(): number {
  const transport = useContext(PreparationSseContext);
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
