import {
  type FormEvent,
  useCallback,
  useEffect,
  useRef,
  useState,
} from "react";
import type { Product } from "../catalog/catalogClient.ts";
import { getAntiforgeryToken } from "../identity/sessionClient.ts";
import { canonicalizeConfirmationInstruction } from "./confirmationInstruction.ts";
import {
  hasDuplicateCompositionLines,
  type CompositionLine,
} from "./composition.ts";
import { CompositionLineEditor } from "./CompositionLineEditor.tsx";
import {
  confirmFirst,
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
  product: Product;
  anotherLine: boolean;
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
  products: Product[];
  activeOperationalReference: string | null;
  requestedTarget?: OrderTargetRequest;
  onActivateOrder: (operationalReference: string) => void;
  onStartNewOrder: () => void;
  onOrderChanged: (operationalReference: string) => void;
  onUnauthorized: () => void;
  ordinaryMutationsBlocked?: boolean;
}

function confirmationErrorMessage(
  problem: OrderOperationsProblemDetails,
  products: Product[],
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
      return `Un Producto de la Composición ya no está disponible.${productLabel}`;
    case "order_operations.first_confirmation.requires_preparation_not_supported":
      return `Un Producto requiere preparación, que todavía no está admitida.${productLabel}`;
    case "order_operations.order.operational_reference_invalid":
      return "La Referencia operacional del Pedido no es válida.";
    case "order_operations.order.not_found":
      return "El Pedido activo ya no existe.";
    case "order_operations.first_confirmation.idempotency_key_conflict":
    case "order_operations.subsequent_confirmation.idempotency_key_conflict":
      return "La identidad de esta Confirmación ya fue usada para otra intención.";
    default:
      return isSubsequent
        ? "No se pudo confirmar la nueva Incorporación. Revisá los datos e intentá nuevamente."
        : "No se pudo confirmar la Composición inicial. Revisá los datos e intentá nuevamente.";
  }
}

export function OrderWorkflow({
  products,
  activeOperationalReference,
  requestedTarget,
  onActivateOrder,
  onStartNewOrder,
  onOrderChanged,
  onUnauthorized,
  ordinaryMutationsBlocked = false,
}: OrderWorkflowProps) {
  const [composition, setComposition] = useState<CompositionLine[]>([]);
  const [context, setContext] = useState("");
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
  const [staleComposition, setStaleComposition] = useState(false);
  activeOrderRef.current = activeOperationalReference;

  function rememberLocalPending(
    value: { orderId: string; marker: PendingComposition } | null,
  ) {
    localPendingRef.current = value;
    setLocalPending(value);
  }

  const reconcilePending = useCallback(
    async (orderId: string): Promise<PendingComposition | null> => {
      setIsPendingLoading(true);
      try {
        const authority = (await getPendingComposition(orderId))
          .pendingComposition;
        if (activeOrderRef.current !== orderId) {
          return authority;
        }

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
                "La Composición autoritativa cambió. El borrador local se conserva sólo para revisión y no se reenviará con otro identificador.",
            });
          }
        }
        return authority;
      } catch (error) {
        if (error instanceof OrderOperationsProblemError) {
          if (error.problem.status === 401) {
            onUnauthorized();
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
              "No se pudo consultar el Estado autoritativo de la Composición pendiente.",
          });
        }
        return null;
      } finally {
        if (activeOrderRef.current === orderId) {
          setIsPendingLoading(false);
        }
      }
    },
    [composition.length, onUnauthorized],
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
      return;
    }

    const timeoutId = window.setTimeout(() => {
      void reconcilePending(activeOperationalReference);
    }, 0);
    return () => window.clearTimeout(timeoutId);
  }, [activeOperationalReference, reconcilePending]);

  function applyAddToComposition(product: Product, anotherLine: boolean) {
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
        },
      ]);
      setConfirmationNotice(null);
      return;
    }

    setComposition((current) => {
      const existing = current.find(
        (line) =>
          line.productId === product.id &&
          canonicalizeConfirmationInstruction(line.instruction) === null,
      );
      if (existing === undefined) {
        return [
          ...current,
          {
            draftLineId: crypto.randomUUID(),
            productId: product.id,
            quantity: 1,
            instruction: "",
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

  async function beginAdd(product: Product, anotherLine: boolean) {
    if (!product.isAvailable || isCompositionLocked) {
      return;
    }

    if (activeOperationalReference === null) {
      applyAddToComposition(product, anotherLine);
      return;
    }

    const owned = localPendingRef.current;
    if (
      owned?.orderId === activeOperationalReference &&
      currentPendingAuthority?.pendingCompositionId ===
        owned.marker.pendingCompositionId
    ) {
      applyAddToComposition(product, anotherLine);
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
      action: { product, anotherLine },
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

    if (error.problem.status === 403) {
      setConfirmationNotice({
        kind: "functional-error",
        message:
          "La Identidad actual no tiene la responsabilidad necesaria para operar Pedidos.",
      });
      return true;
    }

    return false;
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
        );
      } else {
        setConfirmationNotice({
          kind: "functional-error",
          message:
            "La Composición pendiente ya no está vigente. Se actualizó el Estado autoritativo sin iniciar el borrador local.",
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
                ? "El Pedido ya tiene una Composición pendiente. Se actualizó el Estado autoritativo; no se inició otro borrador."
                : "No se pudo iniciar la Composición pendiente.",
          });
          await reconcilePending(intention.command.orderId);
        }
      } else {
        setUncertainStart(intention);
        setConfirmationNotice({
          kind: "uncertain",
          message:
            "Resultado incierto al iniciar la Composición. Reintentá exactamente la misma intención.",
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
                ? "La Composición pendiente cambió antes del descarte. Se actualizó el Estado autoritativo."
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
        message: "Primera Confirmación realizada. Se creó el Pedido.",
      });
      onActivateOrder(confirmed.operationalReference);
      onOrderChanged(confirmed.operationalReference);
    } catch (error) {
      if (error instanceof OrderOperationsProblemError) {
        setUncertainFirst(null);
        if (!handleKnownAuthorization(error)) {
          setConfirmationNotice({
            kind: "functional-error",
            message: confirmationErrorMessage(error.problem, products, false),
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
        message: "Nueva Incorporación confirmada correctamente.",
      });
      onOrderChanged(intention.operationalReference);
      await reconcilePending(intention.operationalReference);
    } catch (error) {
      if (error instanceof OrderOperationsProblemError) {
        setUncertainSubsequent(null);
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
              ? "La Composición autoritativa cambió. Este borrador no se reenviará automáticamente con otro identificador."
              : confirmationErrorMessage(error.problem, products, true),
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

    const items = composition.map(({ productId, quantity, instruction }) => ({
      productId,
      quantity,
      instruction: canonicalizeConfirmationInstruction(instruction),
    }));

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
      if (context.trim().length === 0) {
        return;
      }

      await submitFirst({
        request: { context, items },
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
          "No existe una Composición pendiente autoritativa asociada a este borrador.",
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
        "Resolvé o descartá la Confirmación incierta antes de iniciar un nuevo Pedido.",
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
    !isCompositionLocked &&
    !hasDuplicateCompositionLines(composition) &&
    (isSubsequent || context.trim().length > 0) &&
    (!isSubsequent ||
      (localPending !== null && currentPendingAuthority !== null));
  const modeLabel = isSubsequent ? "Nueva Composición" : "Composición inicial";
  const requestedDestinationMessage =
    requestedExistingReference === null
      ? null
      : hasUncertainIntention
        ? "Resolvé o descartá la Confirmación incierta antes de cambiar de Pedido."
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
      <div className="section-heading">
        <div>
          <p className="eyebrow">
            {isSubsequent ? "Pedido activo" : "Nuevo Pedido"}
          </p>
          <h2 id="composition-title">{modeLabel}</h2>
        </div>
        <p className="ephemeral-label">Estado efímero</p>
      </div>

      {isSubsequent && (
        <div className="active-order-summary" role="status">
          <span>Referencia del Pedido activo</span>
          <strong>{activeOperationalReference}</strong>
          <button
            className="secondary-button"
            type="button"
            onClick={requestNewOrder}
            disabled={isWorkflowLocked}
          >
            Iniciar nuevo Pedido
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
              Descartar Composición
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
              Existe una Composición pendiente autoritativa, pero sus líneas no
              están disponibles en esta memoria local. No se recuperarán ni se
              descartarán automáticamente.
            </p>
            <dl>
              <div>
                <dt>Identificador</dt>
                <dd>{currentPendingAuthority.pendingCompositionId}</dd>
              </div>
              <div>
                <dt>Creada</dt>
                <dd>{currentPendingAuthority.createdAt}</dd>
              </div>
            </dl>
            <button
              className="secondary-button"
              type="button"
              onClick={() =>
                void beginDiscardPending(currentPendingAuthority, null, false)
              }
              disabled={isCompositionLocked}
            >
              Descartar Composición pendiente
            </button>
          </div>
        )}

      {isSubsequent &&
        localPending?.orderId === activeOperationalReference &&
        currentPendingAuthority?.pendingCompositionId ===
          localPending.marker.pendingCompositionId && (
          <div className="active-order-summary" role="status">
            <span>Composición pendiente autoritativa activa</span>
            <strong>{localPending.marker.pendingCompositionId}</strong>
            <button
              className="secondary-button"
              type="button"
              onClick={() =>
                void beginDiscardPending(localPending.marker, null, true)
              }
              disabled={isCompositionLocked}
            >
              Descartar Composición actual
            </button>
          </div>
        )}

      <div
        className="product-selector"
        role="region"
        aria-label="Productos para la Composición"
      >
        <h3>Productos disponibles</h3>
        {products.length === 0 ? (
          <p>No hay productos vigentes para agregar.</p>
        ) : (
          <div className="table-scroll">
            <table>
              <thead>
                <tr>
                  <th scope="col">Nombre</th>
                  <th scope="col">Precio vigente informativo</th>
                  <th scope="col">Disponibilidad</th>
                  <th scope="col">Composición</th>
                </tr>
              </thead>
              <tbody>
                {products.map((product) => (
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
                      </div>
                    </td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
        )}
      </div>

      <form
        className={`confirmation-form${isSubsequent ? " confirmation-form--subsequent" : ""}`}
        onSubmit={(event) => void handleConfirmation(event)}
      >
        {!isSubsequent && (
          <>
            <label htmlFor="order-context">Contexto</label>
            <input
              id="order-context"
              name="context"
              value={context}
              onChange={(event) => setContext(event.target.value)}
              disabled={isCompositionLocked}
            />
          </>
        )}
        <button type="submit" disabled={!canConfirm}>
          {isConfirming
            ? "Confirmando…"
            : isSubsequent
              ? "Confirmar nueva Incorporación"
              : "Confirmar Primera Composición"}
        </button>
      </form>

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
            context={uncertainFirst.request.context}
            items={uncertainFirst.request.items}
            products={products}
          />
          <div className="intention-actions">
            <button
              type="button"
              onClick={() => void submitFirst(uncertainFirst)}
              disabled={isConfirming}
            >
              Reintentar misma Primera Confirmación
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
              <dt>Referencia operacional</dt>
              <dd>{uncertainSubsequent.operationalReference}</dd>
            </div>
          </dl>
          <ConfirmationSnapshot
            items={uncertainSubsequent.request.items}
            products={products}
          />
          <div className="intention-actions">
            <button
              type="button"
              onClick={() => void submitSubsequent(uncertainSubsequent)}
              disabled={isConfirming}
            >
              Reintentar misma Confirmación posterior
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
            Pedido y la misma Idempotency-Key.
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
            mismo Pedido, marcador e Idempotency-Key.
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
          Descartar borrador local desvinculado
        </button>
      )}

      {composition.length === 0 ? (
        <p>La Composición está vacía.</p>
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
                  <th scope="col">Precio vigente informativo</th>
                  <th scope="col">Cantidad</th>
                  <th scope="col">Instrucción opcional</th>
                  <th scope="col">Acciones</th>
                </tr>
              </thead>
              <tbody>
                {composition.map((entry, index) => {
                  const product = products.find(
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
    </section>
  );
}

interface ConfirmationSnapshotProps {
  context?: string;
  items: {
    productId: string;
    quantity: number;
    instruction: string | null;
  }[];
  products: Product[];
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
              <dt>{product?.operationalName ?? item.productId}</dt>
              <dd>
                Cantidad: {item.quantity}. Instrucción:{" "}
                {item.instruction ?? "Sin instrucción"}
              </dd>
            </div>
          );
        })}
      </dl>
      <p>
        El reintento usa exactamente este snapshot y la misma identidad. No se
        reintentará automáticamente.
      </p>
    </>
  );
}
