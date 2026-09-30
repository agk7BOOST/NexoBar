import { CopyReference } from "../ui/CopyReference.tsx";
import { formatOperationalDate } from "../formatOperationalDate.ts";
import {
  type FormEvent,
  useCallback,
  useEffect,
  useRef,
  useState,
} from "react";
import {
  CatalogProblemError,
  listOperationalProducts,
  type OperationalProduct,
} from "../catalog/catalogClient.ts";
import { getAntiforgeryToken } from "../identity/sessionClient.ts";
import { canonicalizeConfirmationInstruction } from "./confirmationInstruction.ts";
import {
  hasDuplicateCompositionLines,
  type CompositionLine,
} from "./composition.ts";
import { CompositionLineEditor } from "./CompositionLineEditor.tsx";
import { ActiveOrderFreshnessSubscription } from "../notifications/ActiveOrderFreshnessSubscription.tsx";
import { FreshnessReadCoordinator } from "../notifications/FreshnessReadCoordinator.ts";
import {
  confirmFirst,
  listOrderContexts,
  confirmSubsequent,
  discardPendingComposition,
  getPendingComposition,
  OrderOperationsNetworkError,
  OrderOperationsProblemError,
  startPendingComposition,
  type FirstConfirmationRequest,
  type OrderOperationsProblemDetails,
  type PendingComposition,
  type PendingCompositionCommand,
  type SubsequentConfirmationRequest,
  type OperationalContextOption,
} from "./orderOperationsClient.ts";

type Notice =
  | { kind: "success"; message: string }
  | { kind: "functional-error"; message: string }
  | { kind: "uncertain"; message: string };

interface FirstConfirmationIntention {
  request: FirstConfirmationRequest;
  idempotencyKey: string;
  antiforgeryToken: string;
}

interface SubsequentConfirmationIntention {
  operationalReference: string;
  request: SubsequentConfirmationRequest;
  idempotencyKey: string;
  antiforgeryToken: string;
}

interface PendingAddAction {
  product: OperationalProduct;
  anotherLine: boolean;
  unavailableProductExceptionRequested: boolean;
}

interface StartPendingIntention {
  command: PendingCompositionCommand;
  action: PendingAddAction;
}

interface DiscardPendingIntention {
  command: PendingCompositionCommand & { pendingCompositionId: string };
  destination: PendingDestination | null;
  clearsLocalComposition: boolean;
}

type PendingDestination =
  | { kind: "new-order" }
  | { kind: "existing-order"; operationalReference: string };

export interface OrderTargetRequest {
  operationalReference: string;
  sequence: number;
}

interface OrderWorkflowProps {
  /** Test seam only; the mounted workflow owns the production operational read. */
  products?: OperationalProduct[];
  activeOperationalReference: string | null;
  activeOrderContext?: string;
  activeOrderId?: string | null;
  requestedTarget?: OrderTargetRequest;
  onActivateOrder: (operationalReference: string) => void;
  onActiveOrderId?: (operationalReference: string, orderId: string) => void;
  onStartNewOrder: () => void;
  onOrderChanged: (operationalReference: string) => void;
  onActiveOrderRetired?: (operationalReference: string) => void;
  onUnauthorized: () => void;
  canRequestUnavailableProductException?: boolean;
  ordinaryMutationsBlocked?: boolean;
  endingRefreshSequence?: number;
}

function confirmationErrorMessage(
  problem: OrderOperationsProblemDetails,
  products: OperationalProduct[],
  isSubsequent: boolean,
): string {
  const product = products.find(
    (candidate) => candidate.id === problem.productId,
  );
  const productLabel = product
    ? ` El Producto afectado es ${product.operationalName}.`
    : "";

  switch (problem.code) {
    case "order_operations.first_confirmation.context_required":
      return "Ingresá un Contexto para confirmar la Composición.";
    case "order_operations.first_confirmation.composition_empty":
      return "Agregá al menos un Producto a la Composición.";
    case "order_operations.first_confirmation.quantity_invalid":
      return `Cada cantidad debe ser un entero positivo.${productLabel}`;
    case "order_operations.confirmation.duplicate_line":
      return `Dos líneas del mismo Producto tienen la misma instrucción. Combiná sus cantidades o cambiá una instrucción.${productLabel}`;
    case "order_operations.confirmation.instruction_requires_preparation":
      return `La instrucción solo puede confirmarse para un Producto que requiere preparación. Editá o quitá la instrucción antes de reintentar.${productLabel}`;
    case "order_operations.first_confirmation.product_not_current":
      return `Un Producto de la Composición ya no está vigente.${productLabel}`;
    case "order_operations.first_confirmation.product_unavailable":
    case "order_operations.confirmation.product_unavailable":
      return `Un Producto de la Composición ya no está disponible.${productLabel}`;
    case "order_operations.confirmation.operational_intervention_required":
      return "La Identidad actual ya no tiene autoridad para la intervención solicitada. Conservá la Composición y solicitá la autoridad necesaria antes de confirmar.";
    case "order_operations.first_confirmation.requires_preparation_not_supported":
      return `Un Producto requiere preparación, que todavía no está admitida.${productLabel}`;
    case "order_operations.order.operational_reference_invalid":
      return "La Referencia del pedido del Pedido no es válida.";
    case "order_operations.order.not_found":
      return "El Pedido activo ya no existe.";
    case "order_operations.first_confirmation.idempotency_key_conflict":
    case "order_operations.subsequent_confirmation.idempotency_key_conflict":
      return "Esta confirmación ya está asociada a otros datos. Revisá los productos antes de volver a intentarla.";
    default:
      return isSubsequent
        ? "No se pudo confirmar la nueva Incorporación. Revisá los datos e intentá nuevamente."
        : "No se pudo confirmar la Composición inicial. Revisá los datos e intentá nuevamente.";
  }
}

export function OrderWorkflow({
  products,
  activeOperationalReference,
  activeOrderContext,
  activeOrderId = null,
  requestedTarget,
  onActivateOrder,
  onActiveOrderId,
  onStartNewOrder,
  onOrderChanged,
  onActiveOrderRetired,
  onUnauthorized,
  canRequestUnavailableProductException = false,
  ordinaryMutationsBlocked = false,
  endingRefreshSequence = 0,
}: OrderWorkflowProps) {
  const [loadedOperationalProducts, setOperationalProducts] = useState<
    OperationalProduct[]
  >(products ?? []);
  const operationalProducts = products ?? loadedOperationalProducts;
  const [operationalReadRetired, setOperationalReadRetired] = useState(false);
  const operationalReadGeneration = useRef(0);
  const [productReadState, setProductReadState] = useState<
    "loading" | "ready" | "error"
  >(products ? "ready" : "loading");
  const [productReadRevision, setProductReadRevision] = useState(0);
  const [contextReadRevision, setContextReadRevision] = useState(0);
  const [composition, setComposition] = useState<CompositionLine[]>([]);
  const [contextId, setContextId] = useState("");
  const [contexts, setContexts] = useState<OperationalContextOption[]>([]);
  const [contextsLoaded, setContextsLoaded] = useState(false);
  const [contextLoadFailed, setContextLoadFailed] = useState(false);
  const [contextsForbidden, setContextsForbidden] = useState(false);
  const [isConfirming, setIsConfirming] = useState(false);
  const [confirmationNotice, setConfirmationNotice] = useState<Notice | null>(
    null,
  );
  const [uncertainFirst, setUncertainFirst] =
    useState<FirstConfirmationIntention | null>(null);
  const [uncertainSubsequent, setUncertainSubsequent] =
    useState<SubsequentConfirmationIntention | null>(null);
  const [pendingDestination, setPendingDestination] =
    useState<PendingDestination | null>(null);
  const [destinationMessage, setDestinationMessage] = useState<string | null>(
    null,
  );
  const [draftLineToFocus, setDraftLineToFocus] = useState<string | null>(null);
  const [pendingAuthority, setPendingAuthority] =
    useState<PendingComposition | null>(null);
  const [pendingAuthorityOrderId, setPendingAuthorityOrderId] = useState<
    string | null
  >(null);
  const [isPendingLoading, setIsPendingLoading] = useState(false);
  const [isPendingMutating, setIsPendingMutating] = useState(false);
  const [uncertainStart, setUncertainStart] =
    useState<StartPendingIntention | null>(null);
  const [uncertainDiscard, setUncertainDiscard] =
    useState<DiscardPendingIntention | null>(null);
  const [localPending, setLocalPending] = useState<{
    orderId: string;
    marker: PendingComposition;
  } | null>(null);
  const localPendingRef = useRef(localPending);
  const activeOrderRef = useRef(activeOperationalReference);
  const pendingReadCoordinator = useRef(new FreshnessReadCoordinator());
  const pendingReadSequence = useRef(0);
  const [staleComposition, setStaleComposition] = useState(false);
  activeOrderRef.current = activeOperationalReference;

  useEffect(() => {
    let current = true;
    void listOrderContexts()
      .then((loaded) => {
        if (!current) return;
        setContexts(loaded);
        setContextsForbidden(false);
        setContextsLoaded(true);
        setContextLoadFailed(false);
      })
      .catch((error: unknown) => {
        if (!current) return;
        if (
          error instanceof OrderOperationsProblemError &&
          error.problem.status === 401
        ) {
          onUnauthorized();
          return;
        }
        setContexts([]);
        setContextsLoaded(true);
        setContextsForbidden(
          error instanceof OrderOperationsProblemError &&
            error.problem.status === 403,
        );
        setContextLoadFailed(true);
      });
    return () => {
      current = false;
    };
  }, [contextReadRevision, onUnauthorized]);

  useEffect(() => {
    if (products !== undefined) return;

    const generation = ++operationalReadGeneration.current;
    void listOperationalProducts().then(
      (loadedProducts) => {
        if (generation === operationalReadGeneration.current) {
          setOperationalProducts(loadedProducts);
          setOperationalReadRetired(false);
          setProductReadState("ready");
        }
      },
      (error: unknown) => {
        if (generation !== operationalReadGeneration.current) return;
        if (error instanceof CatalogProblemError) {
          if (error.problem.status === 401) {
            onUnauthorized();
            return;
          }
          if (error.problem.status === 403) {
            setOperationalProducts([]);
            setOperationalReadRetired(true);
            return;
          }
        }
        setOperationalProducts([]);
        setProductReadState("error");
      },
    );

    return () => {
      operationalReadGeneration.current += 1;
    };
  }, [onUnauthorized, products, productReadRevision]);

  function rememberLocalPending(
    value: { orderId: string; marker: PendingComposition } | null,
  ) {
    localPendingRef.current = value;
    setLocalPending(value);
  }

  const reconcilePending = useCallback(
    async (
      orderId: string,
      isCurrent: () => boolean = () => true,
      terminalOnNotFound = false,
    ): Promise<PendingComposition | null> => {
      const readSequence = ++pendingReadSequence.current;
      const isCurrentRead = () =>
        isCurrent() && readSequence === pendingReadSequence.current;
      setIsPendingLoading(true);
      try {
        const current = await getPendingComposition(orderId);
        const authority = current.pendingComposition;
        if (!isCurrentRead() || activeOrderRef.current !== orderId) {
          return authority;
        }

        onActiveOrderId?.(orderId, current.orderId);

        setPendingAuthority(authority);
        setPendingAuthorityOrderId(orderId);
        const owned = localPendingRef.current;
        if (
          owned?.orderId === orderId &&
          authority?.pendingCompositionId !== owned.marker.pendingCompositionId
        ) {
          rememberLocalPending(null);
          if (composition.length > 0) {
            setStaleComposition(true);
            setConfirmationNotice({
              kind: "functional-error",
              message:
                "La composición pendiente del pedido cambió. Estos productos se conservan para revisión y no se enviarán como una operación nueva.",
            });
          }
        }
        return authority;
      } catch (error) {
        if (!isCurrentRead()) return null;
        if (error instanceof OrderOperationsProblemError) {
          if (error.problem.status === 401) {
            onUnauthorized();
          } else if (terminalOnNotFound && error.problem.status === 404) {
            onActiveOrderRetired?.(orderId);
          } else if (error.problem.status === 403) {
            setConfirmationNotice({
              kind: "functional-error",
              message:
                "La Identidad actual no tiene autorización para operar Composiciones del Pedido.",
            });
          } else {
            setConfirmationNotice({
              kind: "functional-error",
              message: "No se pudo reconciliar la Composición pendiente.",
            });
          }
        } else {
          setConfirmationNotice({
            kind: "uncertain",
            message:
              "No se pudo consultar la composición pendiente del pedido.",
          });
        }
        return null;
      } finally {
        if (isCurrentRead() && activeOrderRef.current === orderId) {
          setIsPendingLoading(false);
        }
      }
    },
    [composition.length, onActiveOrderId, onActiveOrderRetired, onUnauthorized],
  );

  const hasUncertainIntention =
    uncertainFirst !== null ||
    uncertainSubsequent !== null ||
    uncertainStart !== null ||
    uncertainDiscard !== null;
  const isPendingAuthorityResolved =
    activeOperationalReference === null ||
    pendingAuthorityOrderId === activeOperationalReference;
  const currentPendingAuthority =
    activeOperationalReference !== null && isPendingAuthorityResolved
      ? pendingAuthority
      : null;
  const hasAuthoritativePending = currentPendingAuthority !== null;
  const isWorkflowLocked =
    isConfirming ||
    isPendingLoading ||
    isPendingMutating ||
    !isPendingAuthorityResolved ||
    hasUncertainIntention ||
    staleComposition;
  const isCompositionLocked = ordinaryMutationsBlocked || isWorkflowLocked;
  const isSubsequent = activeOperationalReference !== null;
  const requestedExistingReference =
    requestedTarget !== undefined &&
    requestedTarget.operationalReference !== activeOperationalReference
      ? requestedTarget.operationalReference
      : null;

  useEffect(() => {
    if (
      requestedExistingReference !== null &&
      !hasUncertainIntention &&
      composition.length === 0 &&
      isPendingAuthorityResolved &&
      !hasAuthoritativePending
    ) {
      onActivateOrder(requestedExistingReference);
    }
  }, [
    composition.length,
    hasUncertainIntention,
    hasAuthoritativePending,
    isPendingAuthorityResolved,
    onActivateOrder,
    requestedExistingReference,
  ]);

  useEffect(() => {
    if (activeOperationalReference === null) {
      pendingReadCoordinator.current.cancel();
      pendingReadSequence.current++;
      return;
    }

    const timeoutId = window.setTimeout(() => {
      void reconcilePending(activeOperationalReference);
    }, 0);
    return () => window.clearTimeout(timeoutId);
  }, [activeOperationalReference, reconcilePending, endingRefreshSequence]);

  useEffect(() => () => pendingReadCoordinator.current.cancel(), []);

  const invalidatePendingComposition = useCallback(() => {
    const orderId = activeOrderRef.current;
    if (orderId === null) return;
    pendingReadCoordinator.current.invalidate(async (isCurrent) => {
      await reconcilePending(orderId, isCurrent, true);
    });
  }, [reconcilePending]);

  function applyAddToComposition(
    product: OperationalProduct,
    anotherLine: boolean,
    unavailableProductExceptionRequested: boolean,
  ) {
    if (anotherLine) {
      const draftLineId = crypto.randomUUID();
      setDraftLineToFocus(draftLineId);
      setComposition((current) => [
        ...current,
        {
          draftLineId,
          productId: product.id,
          quantity: 1,
          instruction: "",
          unavailableProductExceptionRequested,
        },
      ]);
      setConfirmationNotice(null);
      return;
    }

    setComposition((current) => {
      const existing = current.find(
        (line) =>
          line.productId === product.id &&
          canonicalizeConfirmationInstruction(line.instruction) === null &&
          line.unavailableProductExceptionRequested ===
            unavailableProductExceptionRequested,
      );
      if (existing === undefined) {
        return [
          ...current,
          {
            draftLineId: crypto.randomUUID(),
            productId: product.id,
            quantity: 1,
            instruction: "",
            unavailableProductExceptionRequested,
          },
        ];
      }

      return current.map((line) =>
        line.draftLineId === existing.draftLineId
          ? { ...line, quantity: line.quantity + 1 }
          : line,
      );
    });
  }

  async function beginAdd(
    product: OperationalProduct,
    anotherLine: boolean,
    unavailableProductExceptionRequested = false,
  ) {
    if (
      isCompositionLocked ||
      (!product.isAvailable && !unavailableProductExceptionRequested)
    ) {
      return;
    }

    if (activeOperationalReference === null) {
      applyAddToComposition(
        product,
        anotherLine,
        unavailableProductExceptionRequested,
      );
      return;
    }

    const owned = localPendingRef.current;
    if (
      owned?.orderId === activeOperationalReference &&
      currentPendingAuthority?.pendingCompositionId ===
        owned.marker.pendingCompositionId
    ) {
      applyAddToComposition(
        product,
        anotherLine,
        unavailableProductExceptionRequested,
      );
      return;
    }

    if (currentPendingAuthority !== null) {
      setConfirmationNotice({
        kind: "functional-error",
        message:
          "Este Pedido ya tiene una Composición pendiente cuyas líneas no están disponibles en este navegador. Descartala explícitamente para comenzar otra.",
      });
      return;
    }

    let antiforgeryToken: string;
    try {
      antiforgeryToken = await getAntiforgeryToken();
    } catch {
      setConfirmationNotice({
        kind: "functional-error",
        message: "No se pudo preparar el inicio seguro de la Composición.",
      });
      return;
    }

    await submitStartPending({
      command: {
        orderId: activeOperationalReference,
        idempotencyKey: crypto.randomUUID(),
        antiforgeryToken,
      },
      action: { product, anotherLine, unavailableProductExceptionRequested },
    });
  }

  function increaseQuantity(draftLineId: string) {
    setComposition((current) =>
      current.map((line) =>
        line.draftLineId === draftLineId
          ? { ...line, quantity: line.quantity + 1 }
          : line,
      ),
    );
  }

  function decreaseQuantity(draftLineId: string) {
    setComposition((current) =>
      current.flatMap((line) => {
        if (line.draftLineId !== draftLineId) {
          return [line];
        }

        return line.quantity === 1
          ? []
          : [{ ...line, quantity: line.quantity - 1 }];
      }),
    );
  }

  function changeInstruction(draftLineId: string, instruction: string) {
    setComposition((current) =>
      current.map((line) =>
        line.draftLineId === draftLineId ? { ...line, instruction } : line,
      ),
    );
    setConfirmationNotice(null);
  }

  function removeFromComposition(draftLineId: string) {
    setComposition((current) =>
      current.filter((line) => line.draftLineId !== draftLineId),
    );
  }

  function handleKnownAuthorization(
    error: OrderOperationsProblemError,
  ): boolean {
    if (error.problem.status === 401) {
      onUnauthorized();
      return true;
    }

    if (
      error.problem.status === 403 &&
      error.problem.code !==
        "order_operations.confirmation.operational_intervention_required"
    ) {
      setConfirmationNotice({
        kind: "functional-error",
        message:
          "La Identidad actual no tiene la responsabilidad necesaria para operar Pedidos.",
      });
      return true;
    }

    return false;
  }

  function refreshOperationalProductsAfterAvailabilityChange() {
    if (products !== undefined) return;

    const generation = ++operationalReadGeneration.current;
    void listOperationalProducts().then(
      (loadedProducts) => {
        if (generation === operationalReadGeneration.current) {
          setOperationalProducts(loadedProducts);
          setOperationalReadRetired(false);
        }
      },
      () => undefined,
    );
  }

  async function submitStartPending(intention: StartPendingIntention) {
    setIsPendingMutating(true);
    setConfirmationNotice(null);
    try {
      const marker = await startPendingComposition(intention.command);
      onOrderChanged(intention.command.orderId);
      setUncertainStart(null);
      rememberLocalPending({ orderId: intention.command.orderId, marker });
      setPendingAuthority(marker);
      setPendingAuthorityOrderId(intention.command.orderId);
      const authority = await reconcilePending(intention.command.orderId);
      if (authority?.pendingCompositionId === marker.pendingCompositionId) {
        applyAddToComposition(
          intention.action.product,
          intention.action.anotherLine,
          intention.action.unavailableProductExceptionRequested,
        );
      } else {
        setConfirmationNotice({
          kind: "functional-error",
          message:
            "La composición pendiente ya no está disponible. Se actualizó el pedido sin iniciar otra composición.",
        });
      }
    } catch (error) {
      if (error instanceof OrderOperationsProblemError) {
        setUncertainStart(null);
        if (!handleKnownAuthorization(error)) {
          setConfirmationNotice({
            kind: "functional-error",
            message:
              error.problem.code === "order.pending_composition_already_exists"
                ? "El pedido ya tiene productos pendientes de confirmar. Se actualizó su estado sin iniciar otra composición."
                : "No se pudo iniciar la Composición pendiente.",
          });
          await reconcilePending(intention.command.orderId);
        }
      } else {
        setUncertainStart(intention);
        setConfirmationNotice({
          kind: "uncertain",
          message:
            "No pudimos confirmar si se inició la composición. Podés reintentar esta operación sin duplicarla.",
        });
      }
    } finally {
      setIsPendingMutating(false);
    }
  }

  async function beginDiscardPending(
    marker: PendingComposition,
    destination: PendingDestination | null,
    clearsLocalComposition: boolean,
  ) {
    if (activeOperationalReference === null || isCompositionLocked) {
      return;
    }

    let antiforgeryToken: string;
    try {
      antiforgeryToken = await getAntiforgeryToken();
    } catch {
      setConfirmationNotice({
        kind: "functional-error",
        message: "No se pudo preparar el descarte seguro.",
      });
      return;
    }

    await submitDiscardPending({
      command: {
        orderId: activeOperationalReference,
        pendingCompositionId: marker.pendingCompositionId,
        idempotencyKey: crypto.randomUUID(),
        antiforgeryToken,
      },
      destination,
      clearsLocalComposition,
    });
  }

  async function submitDiscardPending(intention: DiscardPendingIntention) {
    setIsPendingMutating(true);
    setConfirmationNotice(null);
    try {
      await discardPendingComposition(intention.command);
      onOrderChanged(intention.command.orderId);
      setUncertainDiscard(null);
      if (intention.clearsLocalComposition) {
        setComposition([]);
        rememberLocalPending(null);
        setStaleComposition(false);
      }
      await reconcilePending(intention.command.orderId);
      setPendingDestination(null);
      setDestinationMessage(null);
      if (intention.destination?.kind === "new-order") {
        onStartNewOrder();
      } else if (intention.destination?.kind === "existing-order") {
        onActivateOrder(intention.destination.operationalReference);
      } else {
        setConfirmationNotice({
          kind: "success",
          message: "La Composición pendiente fue descartada explícitamente.",
        });
      }
    } catch (error) {
      if (error instanceof OrderOperationsProblemError) {
        setUncertainDiscard(null);
        if (!handleKnownAuthorization(error)) {
          setConfirmationNotice({
            kind: "functional-error",
            message:
              error.problem.code === "order.pending_composition_stale"
                ? "La composición pendiente cambió antes del descarte. Se actualizó el pedido."
                : "No se pudo descartar la Composición pendiente.",
          });
          await reconcilePending(intention.command.orderId);
        }
      } else {
        setUncertainDiscard(intention);
        setConfirmationNotice({
          kind: "uncertain",
          message:
            "Resultado incierto al descartar la Composición. Reintentá exactamente el mismo descarte.",
        });
      }
    } finally {
      setIsPendingMutating(false);
    }
  }

  async function submitFirst(intention: FirstConfirmationIntention) {
    setConfirmationNotice(null);
    setIsConfirming(true);

    try {
      const confirmed = await confirmFirst(
        intention.request,
        intention.idempotencyKey,
        intention.antiforgeryToken,
      );
      setUncertainFirst(null);
      setComposition([]);
      setDestinationMessage(null);
      setConfirmationNotice({
        kind: "success",
        message: `Pedido ${confirmed.operationalReference} creado.`,
      });
      onActivateOrder(confirmed.operationalReference);
      onOrderChanged(confirmed.operationalReference);
    } catch (error) {
      if (error instanceof OrderOperationsProblemError) {
        setUncertainFirst(null);
        if (
          error.problem.code ===
            "order_operations.first_confirmation.product_unavailable" ||
          error.problem.code ===
            "order_operations.confirmation.product_unavailable"
        ) {
          refreshOperationalProductsAfterAvailabilityChange();
        }
        if (!handleKnownAuthorization(error)) {
          setConfirmationNotice({
            kind: "functional-error",
            message: confirmationErrorMessage(
              error.problem,
              operationalProducts,
              false,
            ),
          });
        }
      } else {
        setUncertainFirst(intention);
        setConfirmationNotice({
          kind: "uncertain",
          message:
            error instanceof OrderOperationsNetworkError
              ? "Resultado no confirmado: se perdió la comunicación y no sabemos si el Pedido fue creado."
              : "Resultado no confirmado: no fue posible confirmar la respuesta del servidor.",
        });
      }
    } finally {
      setIsConfirming(false);
    }
  }

  async function submitSubsequent(intention: SubsequentConfirmationIntention) {
    setConfirmationNotice(null);
    setIsConfirming(true);

    try {
      await confirmSubsequent(
        intention.operationalReference,
        intention.request,
        intention.idempotencyKey,
        intention.antiforgeryToken,
      );
      setUncertainSubsequent(null);
      setComposition([]);
      rememberLocalPending(null);
      setPendingAuthority(null);
      setPendingAuthorityOrderId(intention.operationalReference);
      setDestinationMessage(null);
      setConfirmationNotice({
        kind: "success",
        message: `Productos agregados al pedido ${intention.operationalReference}.`,
      });
      onOrderChanged(intention.operationalReference);
      await reconcilePending(intention.operationalReference);
    } catch (error) {
      if (error instanceof OrderOperationsProblemError) {
        setUncertainSubsequent(null);
        if (
          error.problem.code ===
            "order_operations.subsequent_confirmation.product_unavailable" ||
          error.problem.code ===
            "order_operations.confirmation.product_unavailable"
        ) {
          refreshOperationalProductsAfterAvailabilityChange();
        }
        if (!handleKnownAuthorization(error)) {
          const isStale =
            error.problem.code === "order.pending_composition_stale";
          if (isStale) {
            rememberLocalPending(null);
            setStaleComposition(true);
            await reconcilePending(intention.operationalReference);
          }
          setConfirmationNotice({
            kind: "functional-error",
            message: isStale
              ? "La composición pendiente del pedido cambió. Estos productos no se enviarán automáticamente como una operación nueva."
              : confirmationErrorMessage(
                  error.problem,
                  operationalProducts,
                  true,
                ),
          });
        }
      } else {
        setUncertainSubsequent(intention);
        setConfirmationNotice({
          kind: "uncertain",
          message:
            error instanceof OrderOperationsNetworkError
              ? "Resultado no confirmado: se perdió la comunicación y no sabemos si la Incorporación fue creada."
              : "Resultado no confirmado: no fue posible confirmar la respuesta del servidor.",
        });
      }
    } finally {
      setIsConfirming(false);
    }
  }

  async function handleConfirmation(event: FormEvent<HTMLFormElement>) {
    event.preventDefault();

    if (composition.length === 0 || isCompositionLocked) {
      return;
    }

    if (hasDuplicateCompositionLines(composition)) {
      setConfirmationNotice({
        kind: "functional-error",
        message:
          "Hay líneas duplicadas para un mismo Producto e instrucción. Combiná sus cantidades o cambiá una instrucción.",
      });
      return;
    }

    const items = composition.map(
      ({
        productId,
        quantity,
        instruction,
        unavailableProductExceptionRequested,
      }) => ({
        productId,
        quantity,
        instruction: canonicalizeConfirmationInstruction(instruction),
        unavailableProductExceptionRequested,
      }),
    );

    let antiforgeryToken: string;
    try {
      antiforgeryToken = await getAntiforgeryToken();
    } catch {
      setConfirmationNotice({
        kind: "functional-error",
        message: "No se pudo preparar la Confirmación segura.",
      });
      return;
    }

    if (activeOperationalReference === null) {
      if (contextId.length === 0) {
        return;
      }

      await submitFirst({
        request: { contextId, items },
        idempotencyKey: crypto.randomUUID(),
        antiforgeryToken,
      });
      return;
    }

    const owned = localPendingRef.current;
    if (
      owned?.orderId !== activeOperationalReference ||
      currentPendingAuthority?.pendingCompositionId !==
        owned.marker.pendingCompositionId
    ) {
      setConfirmationNotice({
        kind: "functional-error",
        message:
          "Ya no hay una composición pendiente del pedido asociada a estos productos.",
      });
      await reconcilePending(activeOperationalReference);
      return;
    }

    await submitSubsequent({
      operationalReference: activeOperationalReference,
      request: {
        pendingCompositionId: owned.marker.pendingCompositionId,
        items,
      },
      idempotencyKey: crypto.randomUUID(),
      antiforgeryToken,
    });
  }

  function requestNewOrder() {
    if (hasUncertainIntention) {
      setDestinationMessage(
        "Reintentá la confirmación pendiente antes de iniciar otro pedido. Su resultado todavía no está confirmado.",
      );
      return;
    }

    if (composition.length > 0 || hasAuthoritativePending) {
      setPendingDestination({ kind: "new-order" });
      setDestinationMessage(
        "La Composición actual no se cambiará de destino. Descartala explícitamente para iniciar un nuevo Pedido.",
      );
      return;
    }

    setDestinationMessage(null);
    onStartNewOrder();
  }

  async function discardCompositionAndChangeDestination() {
    const destination =
      requestedExistingReference === null
        ? pendingDestination
        : {
            kind: "existing-order" as const,
            operationalReference: requestedExistingReference,
          };

    if (destination === null || hasUncertainIntention) {
      return;
    }

    const owned = localPendingRef.current;
    const marker =
      activeOperationalReference !== null &&
      owned?.orderId === activeOperationalReference
        ? owned.marker
        : currentPendingAuthority;
    if (marker !== null) {
      await beginDiscardPending(
        marker,
        destination,
        owned?.orderId === activeOperationalReference,
      );
      return;
    }

    setComposition([]);
    setStaleComposition(false);
    setDestinationMessage(null);
    setPendingDestination(null);

    if (destination.kind === "new-order") {
      onStartNewOrder();
    } else {
      onActivateOrder(destination.operationalReference);
    }
  }

  const canConfirm =
    composition.length > 0 &&
    (activeOperationalReference !== null ||
      (contexts.length > 0 && contextId.length > 0)) &&
    composition.every((line) => {
      const product = operationalProducts.find(
        (candidate) => candidate.id === line.productId,
      );
      return (
        product?.isAvailable === true ||
        line.unavailableProductExceptionRequested
      );
    }) &&
    !isCompositionLocked &&
    !hasDuplicateCompositionLines(composition) &&
    (isSubsequent || contextId.length > 0) &&
    (!isSubsequent ||
      (localPending !== null && currentPendingAuthority !== null));
  const modeLabel = isSubsequent
    ? "Agregar productos al pedido"
    : "Preparar pedido";
  const hasUnavailableProductExceptionRequested = composition.some(
    (line) => line.unavailableProductExceptionRequested,
  );
  const requestedDestinationMessage =
    requestedExistingReference === null
      ? null
      : hasUncertainIntention
        ? "Reintentá la confirmación pendiente para conocer su resultado antes de cambiar de pedido."
        : composition.length > 0 || hasAuthoritativePending
          ? "La Composición actual no se cambiará de destino. Descartala explícitamente para continuar el Pedido seleccionado."
          : null;
  const displayedDestinationMessage =
    requestedDestinationMessage ?? destinationMessage;
  const canDiscardForDestination =
    !hasUncertainIntention &&
    (pendingDestination !== null ||
      (requestedExistingReference !== null &&
        (composition.length > 0 || hasAuthoritativePending)));

  return (
    <section className="panel" aria-labelledby="composition-title">
      <ActiveOrderFreshnessSubscription
        orderId={activeOrderId}
        invalidate={invalidatePendingComposition}
      />
      <div className="section-heading">
        <div>
          <p className="eyebrow">
            {isSubsequent ? "Pedido activo" : "Nuevo pedido"}
          </p>
          <h2 id="composition-title" tabIndex={-1}>
            {modeLabel}
          </h2>
        </div>
        <p className="ephemeral-label">Productos por confirmar</p>
      </div>

      {isSubsequent && (
        <div className="active-order-summary" role="status">
          <span>Referencia del pedido activo</span>
          <strong>{activeOperationalReference}</strong>
          <CopyReference value={activeOperationalReference!} />
          {activeOrderContext && (
            <span>Contexto actual: {activeOrderContext}</span>
          )}
          <button
            className="secondary-button"
            type="button"
            onClick={requestNewOrder}
            disabled={isWorkflowLocked}
          >
            Iniciar nuevo pedido
          </button>
        </div>
      )}

      {displayedDestinationMessage && (
        <div className="destination-change" role="status">
          <p>{displayedDestinationMessage}</p>
          {canDiscardForDestination && (
            <button
              className="secondary-button"
              type="button"
              onClick={() => void discardCompositionAndChangeDestination()}
            >
              Descartar productos y cambiar de pedido
            </button>
          )}
        </div>
      )}

      {isSubsequent &&
        currentPendingAuthority !== null &&
        localPending?.marker.pendingCompositionId !==
          currentPendingAuthority.pendingCompositionId && (
          <div className="destination-change" role="alert">
            <p>
              Hay productos pendientes de confirmar en este pedido, pero sus
              líneas no están disponibles en esta sesión del navegador. No se
              recuperarán ni descartarán automáticamente.
            </p>
            <details>
              <summary>Detalle de la composición pendiente</summary>
              <dl>
                <div>
                  <dt>Identificador</dt>
                  <dd>{currentPendingAuthority.pendingCompositionId}</dd>
                </div>
                <div>
                  <dt>Creada</dt>
                  <dd>
                    {formatOperationalDate(currentPendingAuthority.createdAt)}
                  </dd>
                </div>
              </dl>
            </details>
            <button
              className="secondary-button"
              type="button"
              onClick={() =>
                void beginDiscardPending(currentPendingAuthority, null, false)
              }
              disabled={isCompositionLocked}
            >
              Descartar productos por confirmar
            </button>
          </div>
        )}

      {isSubsequent &&
        localPending?.orderId === activeOperationalReference &&
        currentPendingAuthority?.pendingCompositionId ===
          localPending.marker.pendingCompositionId && (
          <div className="active-order-summary" role="status">
            <span>Productos pendientes de confirmar en este pedido</span>
            <details>
              <summary>Referencia técnica de la composición</summary>
              <span className="technical-reference">
                {localPending.marker.pendingCompositionId}
              </span>
            </details>
            <button
              className="secondary-button"
              type="button"
              onClick={() =>
                void beginDiscardPending(localPending.marker, null, true)
              }
              disabled={isCompositionLocked}
            >
              Descartar productos por confirmar
            </button>
          </div>
        )}

      <div
        className="product-selector"
        role="region"
        aria-label="Productos para la Composición"
      >
        <h3>Productos disponibles</h3>
        {operationalReadRetired ? (
          <p role="alert">
            La lectura operacional de Productos ya no está autorizada.
          </p>
        ) : productReadState === "loading" && products === undefined ? (
          <p role="status">Cargando productos…</p>
        ) : productReadState === "error" && products === undefined ? (
          <div role="alert">
            <p>No pudimos consultar los productos.</p>
            <button
              type="button"
              onClick={() => {
                setProductReadState("loading");
                setProductReadRevision((value) => value + 1);
              }}
            >
              Reintentar consulta
            </button>
          </div>
        ) : operationalProducts.length === 0 ? (
          <p>No hay productos vigentes para agregar.</p>
        ) : (
          <div className="table-scroll">
            <table>
              <thead>
                <tr>
                  <th scope="col">Nombre</th>
                  <th scope="col">Precio actual del catálogo</th>
                  <th scope="col">Disponibilidad</th>
                  <th scope="col">Composición</th>
                </tr>
              </thead>
              <tbody>
                {operationalProducts.map((product) => (
                  <tr key={product.id}>
                    <td>{product.operationalName}</td>
                    <td>{product.price}</td>
                    <td>
                      {product.isAvailable ? "Disponible" : "No disponible"}
                    </td>
                    <td>
                      <div className="composition-actions">
                        <button
                          className="catalog-add-button"
                          type="button"
                          onClick={() => void beginAdd(product, false)}
                          disabled={!product.isAvailable || isCompositionLocked}
                          aria-label={`Agregar ${product.operationalName} a ${modeLabel}`}
                        >
                          Agregar
                        </button>
                        <button
                          className="secondary-button"
                          type="button"
                          onClick={() => void beginAdd(product, true)}
                          disabled={!product.isAvailable || isCompositionLocked}
                          aria-label={`Agregar otra línea de ${product.operationalName} a ${modeLabel}`}
                        >
                          Agregar otra línea
                        </button>
                        {!product.isAvailable &&
                          canRequestUnavailableProductException && (
                            <button
                              className="secondary-button"
                              type="button"
                              onClick={() =>
                                void beginAdd(product, false, true)
                              }
                              disabled={isCompositionLocked}
                              aria-label={`Agregar ${product.operationalName} mediante intervención a ${modeLabel}`}
                            >
                              Agregar mediante intervención
                            </button>
                          )}
                      </div>
                    </td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
        )}
      </div>

      {confirmationNotice && (
        <p
          className={`notice notice--${confirmationNotice.kind}`}
          role="status"
        >
          {confirmationNotice.message}
        </p>
      )}

      {hasDuplicateCompositionLines(composition) && (
        <p className="notice notice--functional-error" role="alert">
          Hay líneas duplicadas para un mismo Producto e instrucción. Combiná
          sus cantidades o cambiá una instrucción antes de confirmar.
        </p>
      )}

      {uncertainFirst && (
        <div
          className="uncertain-intention"
          role="region"
          aria-label="Primera Confirmación con resultado no confirmado"
        >
          <h3>Primera Confirmación pendiente de resolución</h3>
          <ConfirmationSnapshot
            context={
              contexts.find(
                (option) => option.id === uncertainFirst.request.contextId,
              )?.operationalName ??
              "Contexto seleccionado (nombre no disponible)"
            }
            items={uncertainFirst.request.items}
            products={operationalProducts}
          />
          <div className="intention-actions">
            <button
              type="button"
              onClick={() => void submitFirst(uncertainFirst)}
              disabled={isConfirming}
            >
              Reintentar confirmación
            </button>
          </div>
        </div>
      )}

      {uncertainSubsequent && (
        <div
          className="uncertain-intention"
          role="region"
          aria-label="Confirmación posterior con resultado no confirmado"
        >
          <h3>Confirmación posterior pendiente de resolución</h3>
          <dl>
            <div>
              <dt>Referencia del pedido</dt>
              <dd>{uncertainSubsequent.operationalReference}</dd>
            </div>
          </dl>
          <ConfirmationSnapshot
            items={uncertainSubsequent.request.items}
            products={operationalProducts}
          />
          <div className="intention-actions">
            <button
              type="button"
              onClick={() => void submitSubsequent(uncertainSubsequent)}
              disabled={isConfirming}
            >
              Reintentar confirmación
            </button>
          </div>
        </div>
      )}

      {uncertainStart && (
        <div
          className="uncertain-intention"
          role="region"
          aria-label="Inicio de Composición con resultado incierto"
        >
          <p>
            El resultado del inicio es incierto. El reintento conserva el mismo
            pedido y los mismos datos; no se duplicará.
          </p>
          <button
            type="button"
            onClick={() => void submitStartPending(uncertainStart)}
            disabled={isPendingMutating}
          >
            Reintentar mismo inicio
          </button>
        </div>
      )}

      {uncertainDiscard && (
        <div
          className="uncertain-intention"
          role="region"
          aria-label="Descarte de Composición con resultado incierto"
        >
          <p>
            El resultado del descarte es incierto. El reintento conserva el
            mismo pedido y los mismos datos; no se duplicará.
          </p>
          <button
            type="button"
            onClick={() => void submitDiscardPending(uncertainDiscard)}
            disabled={isPendingMutating}
          >
            Reintentar mismo descarte
          </button>
        </div>
      )}

      {staleComposition && composition.length > 0 && (
        <button
          className="secondary-button"
          type="button"
          onClick={() => {
            setComposition([]);
            setStaleComposition(false);
          }}
        >
          Descartar estos productos sin confirmar
        </button>
      )}

      {composition.length === 0 ? (
        <p>Todavía no agregaste productos.</p>
      ) : (
        <>
          <p className="informative-price-note">
            Los precios son los vigentes del Catálogo: son informativos y aún no
            están confirmados ni aplicados.
          </p>
          <div className="table-scroll">
            <table>
              <thead>
                <tr>
                  <th scope="col">Nombre</th>
                  <th scope="col">Precio actual del catálogo</th>
                  <th scope="col">Cantidad</th>
                  <th scope="col">Instrucción opcional</th>
                  <th scope="col">Acciones</th>
                </tr>
              </thead>
              <tbody>
                {composition.map((entry, index) => {
                  const product = operationalProducts.find(
                    (candidate) => candidate.id === entry.productId,
                  );

                  if (product === undefined) {
                    return null;
                  }

                  const lineNumber = index + 1;

                  return (
                    <CompositionLineEditor
                      key={entry.draftLineId}
                      line={entry}
                      lineNumber={lineNumber}
                      product={product}
                      isLocked={isCompositionLocked}
                      shouldFocusInstruction={
                        draftLineToFocus === entry.draftLineId
                      }
                      onInstructionChange={changeInstruction}
                      onIncrease={increaseQuantity}
                      onDecrease={decreaseQuantity}
                      onRemove={removeFromComposition}
                    />
                  );
                })}
              </tbody>
            </table>
          </div>
        </>
      )}
      <form
        className={`confirmation-form${isSubsequent ? " confirmation-form--subsequent" : ""}`}
        onSubmit={(event) => void handleConfirmation(event)}
      >
        <h3>{isSubsequent ? "Agregar al Pedido" : "Crear Pedido"}</h3>
        {isSubsequent ? (
          <p>
            Se agregarán estos productos al Pedido {activeOperationalReference}
            {activeOrderContext ? ` · ${activeOrderContext}` : ""}.
          </p>
        ) : (
          <>
            <label htmlFor="order-context">Contexto del pedido</label>
            <select
              id="order-context"
              aria-label="Contexto del pedido"
              value={contextId}
              onChange={(event) => setContextId(event.target.value)}
              disabled={isCompositionLocked || contexts.length === 0}
            >
              <option value="">Seleccionar Contexto</option>
              {contexts.map((option) => (
                <option key={option.id} value={option.id}>
                  {option.operationalName}
                </option>
              ))}
            </select>
            <p>
              El Contexto ayuda a ubicar y coordinar el pedido, por ejemplo una
              mesa. No cambia precios, disponibilidad ni destino de preparación.
            </p>
            {contextsForbidden && (
              <p role="alert">
                Tu usuario no tiene autorización para consultar contextos.
              </p>
            )}
            {contextLoadFailed && !contextsForbidden && (
              <div role="alert">
                <p>No pudimos consultar los contextos.</p>
                <button
                  type="button"
                  onClick={() => {
                    setContextsLoaded(false);
                    setContextLoadFailed(false);
                    setContextReadRevision((value) => value + 1);
                  }}
                >
                  Reintentar consulta
                </button>
              </div>
            )}
            {!contextsLoaded && <p role="status">Cargando contextos…</p>}
            {contextsLoaded && contexts.length === 0 && !contextLoadFailed && (
              <p role="status">
                Se requiere configurar un Contexto antes de confirmar un Pedido.
              </p>
            )}
          </>
        )}
        <button type="submit" disabled={!canConfirm}>
          {isConfirming
            ? "Confirmando…"
            : hasUnavailableProductExceptionRequested
              ? isSubsequent
                ? "Agregar al Pedido con intervención"
                : "Crear Pedido con intervención"
              : isSubsequent
                ? "Agregar al Pedido"
                : "Crear Pedido"}
        </button>
        {hasUnavailableProductExceptionRequested && (
          <p className="notice notice--functional-error" role="status">
            Esta Confirmación incluye Productos actualmente marcados como no
            disponibles.
          </p>
        )}
      </form>
    </section>
  );
}

interface ConfirmationSnapshotProps {
  context?: string;
  items: {
    productId: string;
    quantity: number;
    instruction: string | null;
    unavailableProductExceptionRequested: boolean;
  }[];
  products: OperationalProduct[];
}

function ConfirmationSnapshot({
  context,
  items,
  products,
}: ConfirmationSnapshotProps) {
  return (
    <>
      <dl>
        {context !== undefined && (
          <div>
            <dt>Contexto</dt>
            <dd>{context}</dd>
          </div>
        )}
        {items.map((item) => {
          const product = products.find(
            (candidate) => candidate.id === item.productId,
          );

          return (
            <div key={`${item.productId}:${item.instruction ?? ""}`}>
              <dt>
                {product?.operationalName ??
                  "Nombre del producto no disponible"}
              </dt>
              <dd>
                Cantidad: {item.quantity}. Instrucción:{" "}
                {item.instruction ?? "Sin instrucción"}
                {item.unavailableProductExceptionRequested &&
                  ". Intervención solicitada"}
                {!product && (
                  <details>
                    <summary>Referencia técnica del producto</summary>
                    <span className="technical-reference">
                      {item.productId}
                    </span>
                  </details>
                )}
              </dd>
            </div>
          );
        })}
      </dl>
      <p>
        El reintento conserva estos productos y sus condiciones sin duplicar la
        confirmación. No se hará automáticamente.
      </p>
    </>
  );
}
