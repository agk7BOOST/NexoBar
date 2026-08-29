import { type FormEvent, useCallback, useEffect, useState } from "react";
import {
  CatalogNetworkError,
  CatalogProblemError,
  createProduct,
  listProducts,
  type CreateProductRequest,
  type ProblemDetails,
  type Product,
} from "./catalog/catalogClient.ts";
import {
  confirmFirst,
  OrderOperationsNetworkError,
  OrderOperationsProblemError,
  type FirstConfirmationRequest,
  type FirstConfirmationResponse,
  type OrderOperationsProblemDetails,
} from "./orderOperations/orderOperationsClient.ts";
import { OrderLookup } from "./orderOperations/OrderLookup.tsx";

type Notice =
  | { kind: "success"; message: string }
  | { kind: "functional-error"; message: string }
  | { kind: "uncertain"; message: string };

interface ProductCreationIntention {
  request: CreateProductRequest;
  idempotencyKey: string;
}

interface CompositionEntry {
  productId: string;
  quantity: number;
}

interface FirstConfirmationIntention {
  request: FirstConfirmationRequest;
  idempotencyKey: string;
}

function functionalErrorMessage(problem: ProblemDetails): string {
  if (problem.code === "catalog.product.operational_name_conflict") {
    return "Ya existe un producto vigente con ese nombre.";
  }

  if (problem.code === "catalog.product.invalid") {
    if (problem.field === "operationalName") {
      return "Ingresá un nombre operacional válido.";
    }

    if (problem.field === "price") {
      return "Ingresá un precio válido mayor o igual a cero.";
    }
  }

  return "No se pudo crear el producto. Revisá los datos e intentá nuevamente.";
}

function confirmationErrorMessage(
  problem: OrderOperationsProblemDetails,
  products: Product[],
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
    case "order_operations.first_confirmation.duplicate_product":
      return `Un Producto no puede aparecer más de una vez en la Confirmación.${productLabel}`;
    case "order_operations.first_confirmation.product_not_current":
      return `Un Producto de la Composición ya no está vigente.${productLabel}`;
    case "order_operations.first_confirmation.product_unavailable":
      return `Un Producto de la Composición ya no está disponible.${productLabel}`;
    case "order_operations.first_confirmation.requires_preparation_not_supported":
      return `Un Producto requiere preparación, que todavía no está admitida.${productLabel}`;
    case "order_operations.first_confirmation.idempotency_key_conflict":
      return "La identidad de esta Confirmación ya fue usada para otra intención. Revisá la Composición e intentá nuevamente.";
    default:
      return "No se pudo confirmar la Composición. Revisá los datos e intentá nuevamente.";
  }
}

function App() {
  const [products, setProducts] = useState<Product[]>([]);
  const [isLoading, setIsLoading] = useState(true);
  const [loadError, setLoadError] = useState<string | null>(null);
  const [operationalName, setOperationalName] = useState("");
  const [price, setPrice] = useState("");
  const [isSubmitting, setIsSubmitting] = useState(false);
  const [notice, setNotice] = useState<Notice | null>(null);
  const [uncertainIntention, setUncertainIntention] =
    useState<ProductCreationIntention | null>(null);
  const [composition, setComposition] = useState<CompositionEntry[]>([]);
  const [context, setContext] = useState("");
  const [isConfirming, setIsConfirming] = useState(false);
  const [confirmationNotice, setConfirmationNotice] = useState<Notice | null>(
    null,
  );
  const [uncertainConfirmation, setUncertainConfirmation] =
    useState<FirstConfirmationIntention | null>(null);
  const [confirmedOrder, setConfirmedOrder] =
    useState<FirstConfirmationResponse | null>(null);

  const loadProducts = useCallback(async () => {
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

  async function submitIntention(intention: ProductCreationIntention) {
    setNotice(null);
    setIsSubmitting(true);

    const formMatchesIntention =
      operationalName === intention.request.operationalName &&
      price === intention.request.price;

    try {
      await createProduct(intention.request, intention.idempotencyKey);
      setUncertainIntention(null);
      if (formMatchesIntention) {
        setOperationalName("");
        setPrice("");
      }
      setNotice({
        kind: "success",
        message: "Producto creado correctamente.",
      });
      await loadProducts();
    } catch (error) {
      if (error instanceof CatalogProblemError) {
        setUncertainIntention(null);
        setNotice({
          kind: "functional-error",
          message: functionalErrorMessage(error.problem),
        });
      } else {
        setUncertainIntention(intention);
        setNotice({
          kind: "uncertain",
          message:
            error instanceof CatalogNetworkError
              ? "Resultado no confirmado: se perdió la comunicación y no sabemos si el producto fue creado."
              : "Resultado no confirmado: no fue posible confirmar la respuesta del servidor.",
        });
      }
    } finally {
      setIsSubmitting(false);
    }
  }

  async function handleSubmit(event: FormEvent<HTMLFormElement>) {
    event.preventDefault();

    if (uncertainIntention !== null) {
      setNotice({
        kind: "uncertain",
        message:
          "Resolvé o descartá la intención pendiente antes de iniciar otra.",
      });
      return;
    }

    await submitIntention({
      request: {
        operationalName,
        price,
        requiresPreparation: false,
      },
      idempotencyKey: crypto.randomUUID(),
    });
  }

  function discardUncertainIntention() {
    setUncertainIntention(null);
    setNotice({
      kind: "uncertain",
      message:
        "La intención pendiente fue descartada. El resultado anterior sigue sin confirmarse; el próximo envío será una intención nueva.",
    });
  }

  function addToComposition(product: Product) {
    if (!product.isAvailable || isCompositionLocked) {
      return;
    }

    setComposition((currentComposition) => {
      const existingEntry = currentComposition.find(
        (entry) => entry.productId === product.id,
      );

      if (existingEntry === undefined) {
        return [...currentComposition, { productId: product.id, quantity: 1 }];
      }

      return currentComposition.map((entry) =>
        entry.productId === product.id
          ? { ...entry, quantity: entry.quantity + 1 }
          : entry,
      );
    });
  }

  function increaseQuantity(productId: string) {
    setComposition((currentComposition) =>
      currentComposition.map((entry) =>
        entry.productId === productId
          ? { ...entry, quantity: entry.quantity + 1 }
          : entry,
      ),
    );
  }

  function decreaseQuantity(productId: string) {
    setComposition((currentComposition) =>
      currentComposition.flatMap((entry) => {
        if (entry.productId !== productId) {
          return [entry];
        }

        return entry.quantity === 1
          ? []
          : [{ ...entry, quantity: entry.quantity - 1 }];
      }),
    );
  }

  function removeFromComposition(productId: string) {
    setComposition((currentComposition) =>
      currentComposition.filter((entry) => entry.productId !== productId),
    );
  }

  async function submitFirstConfirmation(
    intention: FirstConfirmationIntention,
  ) {
    setConfirmationNotice(null);
    setIsConfirming(true);

    try {
      const confirmed = await confirmFirst(
        intention.request,
        intention.idempotencyKey,
      );
      setUncertainConfirmation(null);
      setComposition([]);
      setConfirmedOrder(confirmed);
      setConfirmationNotice({
        kind: "success",
        message: "Primera Confirmación realizada. Se creó el Pedido.",
      });
    } catch (error) {
      if (error instanceof OrderOperationsProblemError) {
        setUncertainConfirmation(null);
        setConfirmationNotice({
          kind: "functional-error",
          message: confirmationErrorMessage(error.problem, products),
        });
      } else {
        setUncertainConfirmation(intention);
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

  async function handleFirstConfirmation(event: FormEvent<HTMLFormElement>) {
    event.preventDefault();

    if (
      context.trim().length === 0 ||
      composition.length === 0 ||
      uncertainConfirmation !== null
    ) {
      return;
    }

    await submitFirstConfirmation({
      request: {
        context,
        items: composition.map(({ productId, quantity }) => ({
          productId,
          quantity,
        })),
      },
      idempotencyKey: crypto.randomUUID(),
    });
  }

  function discardUncertainConfirmation() {
    setUncertainConfirmation(null);
    setConfirmationNotice({
      kind: "uncertain",
      message:
        "La intención incierta fue descartada. El resultado previo sigue sin confirmarse; una futura Confirmación será una intención nueva.",
    });
  }

  const formDiffersFromUncertainIntention =
    uncertainIntention !== null &&
    (operationalName !== uncertainIntention.request.operationalName ||
      price !== uncertainIntention.request.price);
  const isCompositionLocked = isConfirming || uncertainConfirmation !== null;
  const canConfirm =
    context.trim().length > 0 && composition.length > 0 && !isCompositionLocked;

  return (
    <main className="page-shell">
      <header className="page-header">
        <p className="eyebrow">NexoBar</p>
        <h1>Catálogo de productos</h1>
        <p>Alta y consulta de productos vigentes.</p>
      </header>

      <section className="panel" aria-labelledby="create-title">
        <h2 id="create-title">Crear producto</h2>
        <form onSubmit={handleSubmit}>
          <label htmlFor="operational-name">Nombre operacional</label>
          <input
            id="operational-name"
            name="operationalName"
            value={operationalName}
            onChange={(event) => setOperationalName(event.target.value)}
            disabled={isSubmitting}
            required
          />

          <label htmlFor="price">Precio</label>
          <input
            id="price"
            name="price"
            value={price}
            onChange={(event) => setPrice(event.target.value)}
            inputMode="decimal"
            disabled={isSubmitting}
            required
          />

          <button
            type="submit"
            disabled={isSubmitting || uncertainIntention !== null}
          >
            {isSubmitting
              ? "Creando…"
              : uncertainIntention
                ? "Hay una intención pendiente"
                : "Crear producto"}
          </button>
        </form>

        {notice && (
          <p className={`notice notice--${notice.kind}`} role="status">
            {notice.message}
          </p>
        )}

        {uncertainIntention && (
          <div
            className="uncertain-intention"
            role="region"
            aria-label="Intención con resultado no confirmado"
          >
            <h3>Intención pendiente de confirmación</h3>
            <dl>
              <div>
                <dt>Nombre</dt>
                <dd>{uncertainIntention.request.operationalName}</dd>
              </div>
              <div>
                <dt>Precio</dt>
                <dd>{uncertainIntention.request.price}</dd>
              </div>
            </dl>
            <p>
              El reintento usa exactamente estos datos y la misma identidad.
            </p>
            {formDiffersFromUncertainIntention && (
              <p className="pending-change-warning">
                Los cambios del formulario no alteran esta intención pendiente.
                Para enviarlos como una intención nueva, descartá primero la
                pendiente.
              </p>
            )}
            <div className="intention-actions">
              <button
                type="button"
                onClick={() => void submitIntention(uncertainIntention)}
                disabled={isSubmitting}
              >
                Reintentar misma intención
              </button>
              <button
                className="secondary-button"
                type="button"
                onClick={discardUncertainIntention}
                disabled={isSubmitting}
              >
                Descartar e iniciar nueva
              </button>
            </div>
          </div>
        )}
      </section>

      <section className="panel" aria-labelledby="products-title">
        <div className="section-heading">
          <h2 id="products-title">Productos vigentes</h2>
          <button
            className="secondary-button"
            type="button"
            onClick={() => void loadProducts()}
            disabled={isLoading}
          >
            Actualizar
          </button>
        </div>

        {isLoading && <p>Cargando productos…</p>}
        {!isLoading && loadError && <p role="alert">{loadError}</p>}
        {!isLoading && !loadError && products.length === 0 && (
          <p>No hay productos vigentes.</p>
        )}
        {!isLoading && !loadError && products.length > 0 && (
          <div className="table-scroll">
            <table>
              <thead>
                <tr>
                  <th scope="col">Nombre</th>
                  <th scope="col">Precio</th>
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
                      <button
                        className="catalog-add-button"
                        type="button"
                        onClick={() => addToComposition(product)}
                        disabled={!product.isAvailable || isCompositionLocked}
                        aria-label={`Agregar ${product.operationalName} a la composición`}
                      >
                        Agregar
                      </button>
                    </td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
        )}
      </section>

      <section className="panel" aria-labelledby="composition-title">
        <div className="section-heading">
          <h2 id="composition-title">Composición</h2>
          <p className="ephemeral-label">Estado efímero</p>
        </div>

        {confirmedOrder && (
          <p className="new-operation-note">
            Esta Composición prepara una operación nueva e independiente del
            Pedido recién creado.
          </p>
        )}

        <form
          className="confirmation-form"
          onSubmit={(event) => void handleFirstConfirmation(event)}
        >
          <label htmlFor="order-context">Contexto</label>
          <input
            id="order-context"
            name="context"
            value={context}
            onChange={(event) => setContext(event.target.value)}
            disabled={isCompositionLocked}
          />
          <button type="submit" disabled={!canConfirm}>
            {isConfirming ? "Confirmando…" : "Confirmar Composición"}
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

        {uncertainConfirmation && (
          <div
            className="uncertain-intention"
            role="region"
            aria-label="Confirmación con resultado no confirmado"
          >
            <h3>Confirmación pendiente de resolución</h3>
            <dl>
              <div>
                <dt>Contexto</dt>
                <dd>{uncertainConfirmation.request.context}</dd>
              </div>
              {uncertainConfirmation.request.items.map((item) => {
                const product = products.find(
                  (candidate) => candidate.id === item.productId,
                );

                return (
                  <div key={item.productId}>
                    <dt>{product?.operationalName ?? item.productId}</dt>
                    <dd>Cantidad: {item.quantity}</dd>
                  </div>
                );
              })}
            </dl>
            <p>
              El reintento usa exactamente este Contexto, estos items y la misma
              identidad. No se reintentará automáticamente.
            </p>
            <div className="intention-actions">
              <button
                type="button"
                onClick={() =>
                  void submitFirstConfirmation(uncertainConfirmation)
                }
                disabled={isConfirming}
              >
                Reintentar misma Confirmación
              </button>
              <button
                className="secondary-button"
                type="button"
                onClick={discardUncertainConfirmation}
                disabled={isConfirming}
              >
                Descartar intención incierta
              </button>
            </div>
          </div>
        )}

        {composition.length === 0 && <p>La Composición está vacía.</p>}
        {composition.length > 0 && (
          <>
            <p className="informative-price-note">
              Los precios son los vigentes del Catálogo: son informativos y aún
              no están confirmados ni aplicados.
            </p>
            <div className="table-scroll">
              <table>
                <thead>
                  <tr>
                    <th scope="col">Nombre</th>
                    <th scope="col">Precio vigente informativo</th>
                    <th scope="col">Cantidad</th>
                    <th scope="col">Acciones</th>
                  </tr>
                </thead>
                <tbody>
                  {composition.map((entry) => {
                    const product = products.find(
                      (candidate) => candidate.id === entry.productId,
                    );

                    if (product === undefined) {
                      return null;
                    }

                    return (
                      <tr key={entry.productId}>
                        <td>{product.operationalName}</td>
                        <td>{product.price}</td>
                        <td
                          aria-label={`Cantidad de ${product.operationalName}`}
                        >
                          {entry.quantity}
                        </td>
                        <td>
                          <div className="composition-actions">
                            <button
                              type="button"
                              onClick={() => increaseQuantity(entry.productId)}
                              disabled={isCompositionLocked}
                              aria-label={`Aumentar cantidad de ${product.operationalName}`}
                            >
                              +1
                            </button>
                            <button
                              className="secondary-button"
                              type="button"
                              onClick={() => decreaseQuantity(entry.productId)}
                              disabled={isCompositionLocked}
                              aria-label={`Disminuir cantidad de ${product.operationalName}`}
                            >
                              −1
                            </button>
                            <button
                              className="secondary-button"
                              type="button"
                              onClick={() =>
                                removeFromComposition(entry.productId)
                              }
                              disabled={isCompositionLocked}
                              aria-label={`Retirar ${product.operationalName} de la composición`}
                            >
                              Retirar
                            </button>
                          </div>
                        </td>
                      </tr>
                    );
                  })}
                </tbody>
              </table>
            </div>
          </>
        )}
      </section>

      {confirmedOrder && (
        <section
          className="panel confirmed-order"
          aria-labelledby="confirmed-order-title"
        >
          <h2 id="confirmed-order-title">Pedido recién creado</h2>
          <dl className="confirmation-summary">
            <div>
              <dt>Contexto confirmado</dt>
              <dd>{confirmedOrder.context}</dd>
            </div>
            <div>
              <dt>Referencia operacional</dt>
              <dd>{confirmedOrder.operationalReference}</dd>
            </div>
            <div>
              <dt>Primera Incorporación</dt>
              <dd>{confirmedOrder.firstIncorporation.id}</dd>
            </div>
            <div>
              <dt>Momento de Confirmación</dt>
              <dd>
                <time dateTime={confirmedOrder.firstIncorporation.confirmedAt}>
                  {new Date(
                    confirmedOrder.firstIncorporation.confirmedAt,
                  ).toLocaleString()}
                </time>
              </dd>
            </div>
          </dl>
          <p className="applied-price-note">
            El Precio aplicado es la Condición histórica confirmada por el
            backend; no es el precio vigente informativo del Catálogo.
          </p>
          <div className="table-scroll">
            <table>
              <thead>
                <tr>
                  <th scope="col">Producto</th>
                  <th scope="col">Cantidad confirmada</th>
                  <th scope="col">Precio aplicado</th>
                </tr>
              </thead>
              <tbody>
                {confirmedOrder.firstIncorporation.items.map((item) => {
                  const product = products.find(
                    (candidate) => candidate.id === item.productId,
                  );

                  return (
                    <tr key={item.productId}>
                      <td>{product?.operationalName ?? item.productId}</td>
                      <td>{item.quantity}</td>
                      <td>{item.appliedPrice}</td>
                    </tr>
                  );
                })}
              </tbody>
            </table>
          </div>
        </section>
      )}

      <OrderLookup products={products} />
    </main>
  );
}

export default App;
