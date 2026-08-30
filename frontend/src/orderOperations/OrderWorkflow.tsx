import { type FormEvent, useEffect, useState } from "react";
import type { Product } from "../catalog/catalogClient.ts";
import { canonicalizeConfirmationInstruction } from "./confirmationInstruction.ts";
import {
  hasDuplicateCompositionLines,
  type CompositionLine,
} from "./composition.ts";
import { CompositionLineEditor } from "./CompositionLineEditor.tsx";
import {
  confirmFirst,
  confirmSubsequent,
  OrderOperationsNetworkError,
  OrderOperationsProblemError,
  type FirstConfirmationRequest,
  type OrderOperationsProblemDetails,
  type SubsequentConfirmationRequest,
} from "./orderOperationsClient.ts";

type Notice =
  | { kind: "success"; message: string }
  | { kind: "functional-error"; message: string }
  | { kind: "uncertain"; message: string };

interface FirstConfirmationIntention {
  request: FirstConfirmationRequest;
  idempotencyKey: string;
}

interface SubsequentConfirmationIntention {
  operationalReference: string;
  request: SubsequentConfirmationRequest;
  idempotencyKey: string;
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

  const hasUncertainIntention =
    uncertainFirst !== null || uncertainSubsequent !== null;
  const isCompositionLocked = isConfirming || hasUncertainIntention;
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
      composition.length === 0
    ) {
      onActivateOrder(requestedExistingReference);
    }
  }, [
    composition.length,
    hasUncertainIntention,
    onActivateOrder,
    requestedExistingReference,
  ]);

  function addToComposition(product: Product) {
    if (!product.isAvailable || isCompositionLocked) {
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

  function addAnotherLine(product: Product) {
    if (!product.isAvailable || isCompositionLocked) {
      return;
    }

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

  async function submitFirst(intention: FirstConfirmationIntention) {
    setConfirmationNotice(null);
    setIsConfirming(true);

    try {
      const confirmed = await confirmFirst(
        intention.request,
        intention.idempotencyKey,
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
        setConfirmationNotice({
          kind: "functional-error",
          message: confirmationErrorMessage(error.problem, products, false),
        });
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
      );
      setUncertainSubsequent(null);
      setComposition([]);
      setDestinationMessage(null);
      setConfirmationNotice({
        kind: "success",
        message: "Nueva Incorporación confirmada correctamente.",
      });
      onOrderChanged(intention.operationalReference);
    } catch (error) {
      if (error instanceof OrderOperationsProblemError) {
        setUncertainSubsequent(null);
        setConfirmationNotice({
          kind: "functional-error",
          message: confirmationErrorMessage(error.problem, products, true),
        });
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

    if (activeOperationalReference === null) {
      if (context.trim().length === 0) {
        return;
      }

      await submitFirst({
        request: { context, items },
        idempotencyKey: crypto.randomUUID(),
      });
      return;
    }

    await submitSubsequent({
      operationalReference: activeOperationalReference,
      request: { items },
      idempotencyKey: crypto.randomUUID(),
    });
  }

  function requestNewOrder() {
    if (hasUncertainIntention) {
      setDestinationMessage(
        "Resolvé o descartá la Confirmación incierta antes de iniciar un nuevo Pedido.",
      );
      return;
    }

    if (composition.length > 0) {
      setPendingDestination({ kind: "new-order" });
      setDestinationMessage(
        "La Composición actual no se cambiará de destino. Descartala explícitamente para iniciar un nuevo Pedido.",
      );
      return;
    }

    setDestinationMessage(null);
    onStartNewOrder();
  }

  function discardCompositionAndChangeDestination() {
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

    setComposition([]);
    setDestinationMessage(null);
    setPendingDestination(null);

    if (destination.kind === "new-order") {
      onStartNewOrder();
    } else {
      onActivateOrder(destination.operationalReference);
    }
  }

  function discardUncertainFirst() {
    setUncertainFirst(null);
    setDestinationMessage(null);
    setConfirmationNotice({
      kind: "uncertain",
      message:
        "La intención incierta fue descartada. El resultado previo sigue sin confirmarse; una futura Confirmación será una intención nueva.",
    });
  }

  function discardUncertainSubsequent() {
    setUncertainSubsequent(null);
    setDestinationMessage(null);
    setConfirmationNotice({
      kind: "uncertain",
      message:
        "La intención incierta fue descartada. El resultado previo sigue sin confirmarse; una futura Incorporación será una intención nueva.",
    });
  }

  const canConfirm =
    composition.length > 0 &&
    !isCompositionLocked &&
    !hasDuplicateCompositionLines(composition) &&
    (isSubsequent || context.trim().length > 0);
  const modeLabel = isSubsequent ? "Nueva Composición" : "Composición inicial";
  const requestedDestinationMessage =
    requestedExistingReference === null
      ? null
      : hasUncertainIntention
        ? "Resolvé o descartá la Confirmación incierta antes de cambiar de Pedido."
        : composition.length > 0
          ? "La Composición actual no se cambiará de destino. Descartala explícitamente para continuar el Pedido seleccionado."
          : null;
  const displayedDestinationMessage =
    requestedDestinationMessage ?? destinationMessage;
  const canDiscardForDestination =
    !hasUncertainIntention &&
    (pendingDestination !== null ||
      (requestedExistingReference !== null && composition.length > 0));

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
            disabled={isConfirming}
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
              onClick={discardCompositionAndChangeDestination}
            >
              Descartar Composición
            </button>
          )}
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
                          onClick={() => addToComposition(product)}
                          disabled={!product.isAvailable || isCompositionLocked}
                          aria-label={`Agregar ${product.operationalName} a ${modeLabel}`}
                        >
                          Agregar
                        </button>
                        <button
                          className="secondary-button"
                          type="button"
                          onClick={() => addAnotherLine(product)}
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
            <button
              className="secondary-button"
              type="button"
              onClick={discardUncertainFirst}
              disabled={isConfirming}
            >
              Descartar intención incierta
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
            <button
              className="secondary-button"
              type="button"
              onClick={discardUncertainSubsequent}
              disabled={isConfirming}
            >
              Descartar intención incierta
            </button>
          </div>
        </div>
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
