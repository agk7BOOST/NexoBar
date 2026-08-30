import { type FormEvent, useState } from "react";
import {
  CatalogNetworkError,
  CatalogProblemError,
  changeProductPrice,
  createProduct,
  type ChangeProductPriceRequest,
  type CreateProductRequest,
  type ProblemDetails,
  type Product,
} from "./catalogClient.ts";

type Notice =
  | { kind: "success"; message: string }
  | { kind: "functional-error"; message: string }
  | { kind: "uncertain"; message: string };

interface ProductCreationIntention {
  request: CreateProductRequest;
  idempotencyKey: string;
}

interface PriceEditor {
  productId: string;
  operationalName: string;
  observedPrice: string;
  newPrice: string;
}

interface ProductPriceChangeIntention {
  productId: string;
  operationalName: string;
  request: ChangeProductPriceRequest;
  idempotencyKey: string;
}

interface CatalogPanelProps {
  products: Product[];
  isLoading: boolean;
  loadError: string | null;
  reloadProducts: () => Promise<void>;
}

function creationErrorMessage(problem: ProblemDetails): string {
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

function priceChangeErrorMessage(problem: ProblemDetails): string {
  switch (problem.code) {
    case "catalog.product.price_change_invalid":
      return "Ingresá un nuevo precio válido mayor o igual a cero.";
    case "catalog.product.not_found":
      return "El Producto ya no existe.";
    case "catalog.product.not_current":
      return "El Producto ya no está vigente.";
    case "catalog.product.price_concurrency_conflict":
      return problem.currentPrice === undefined
        ? "El Precio cambió desde que fue observado. El Catálogo se actualizará."
        : `El Precio cambió desde que fue observado. El Precio vigente es ${problem.currentPrice}.`;
    case "catalog.product.idempotency_key_conflict":
      return "La identidad de este cambio de Precio ya fue usada para otra intención.";
    default:
      return "No se pudo cambiar el Precio. Revisá los datos e intentá nuevamente.";
  }
}

export function CatalogPanel({
  products,
  isLoading,
  loadError,
  reloadProducts,
}: CatalogPanelProps) {
  const [operationalName, setOperationalName] = useState("");
  const [price, setPrice] = useState("");
  const [isCreating, setIsCreating] = useState(false);
  const [creationNotice, setCreationNotice] = useState<Notice | null>(null);
  const [uncertainCreation, setUncertainCreation] =
    useState<ProductCreationIntention | null>(null);
  const [priceEditor, setPriceEditor] = useState<PriceEditor | null>(null);
  const [isChangingPrice, setIsChangingPrice] = useState(false);
  const [priceNotice, setPriceNotice] = useState<Notice | null>(null);
  const [uncertainPriceChange, setUncertainPriceChange] =
    useState<ProductPriceChangeIntention | null>(null);

  async function submitCreation(intention: ProductCreationIntention) {
    setCreationNotice(null);
    setIsCreating(true);

    const formMatchesIntention =
      operationalName === intention.request.operationalName &&
      price === intention.request.price;

    try {
      await createProduct(intention.request, intention.idempotencyKey);
      setUncertainCreation(null);
      if (formMatchesIntention) {
        setOperationalName("");
        setPrice("");
      }
      setCreationNotice({
        kind: "success",
        message: "Producto creado correctamente.",
      });
      await reloadProducts();
    } catch (error) {
      if (error instanceof CatalogProblemError) {
        setUncertainCreation(null);
        setCreationNotice({
          kind: "functional-error",
          message: creationErrorMessage(error.problem),
        });
      } else {
        setUncertainCreation(intention);
        setCreationNotice({
          kind: "uncertain",
          message:
            error instanceof CatalogNetworkError
              ? "Resultado no confirmado: se perdió la comunicación y no sabemos si el producto fue creado."
              : "Resultado no confirmado: no fue posible confirmar la respuesta del servidor.",
        });
      }
    } finally {
      setIsCreating(false);
    }
  }

  async function handleCreate(event: FormEvent<HTMLFormElement>) {
    event.preventDefault();

    if (uncertainCreation !== null) {
      return;
    }

    await submitCreation({
      request: {
        operationalName,
        price,
        requiresPreparation: false,
      },
      idempotencyKey: crypto.randomUUID(),
    });
  }

  function discardUncertainCreation() {
    setUncertainCreation(null);
    setCreationNotice({
      kind: "uncertain",
      message:
        "La intención pendiente fue descartada. El resultado anterior sigue sin confirmarse; el próximo envío será una intención nueva.",
    });
  }

  function openPriceEditor(product: Product) {
    if (uncertainPriceChange !== null || isChangingPrice) {
      return;
    }

    setPriceEditor({
      productId: product.id,
      operationalName: product.operationalName,
      observedPrice: product.price,
      newPrice: "",
    });
    setPriceNotice(null);
  }

  async function submitPriceChange(intention: ProductPriceChangeIntention) {
    setPriceNotice(null);
    setIsChangingPrice(true);

    try {
      await changeProductPrice(
        intention.productId,
        intention.request,
        intention.idempotencyKey,
      );
      setUncertainPriceChange(null);
      setPriceEditor(null);
      setPriceNotice({
        kind: "success",
        message: `Precio de ${intention.operationalName} actualizado correctamente.`,
      });
      await reloadProducts();
    } catch (error) {
      if (error instanceof CatalogProblemError) {
        setUncertainPriceChange(null);
        setPriceEditor(null);
        setPriceNotice({
          kind: "functional-error",
          message: priceChangeErrorMessage(error.problem),
        });
        if (
          error.problem.code === "catalog.product.price_concurrency_conflict"
        ) {
          await reloadProducts();
        }
      } else {
        setUncertainPriceChange(intention);
        setPriceNotice({
          kind: "uncertain",
          message:
            error instanceof CatalogNetworkError
              ? "Resultado no confirmado: se perdió la comunicación y no sabemos si el Precio fue cambiado."
              : "Resultado no confirmado: no fue posible confirmar la respuesta del servidor.",
        });
      }
    } finally {
      setIsChangingPrice(false);
    }
  }

  async function handlePriceChange(event: FormEvent<HTMLFormElement>) {
    event.preventDefault();

    if (priceEditor === null || uncertainPriceChange !== null) {
      return;
    }

    await submitPriceChange({
      productId: priceEditor.productId,
      operationalName: priceEditor.operationalName,
      request: {
        expectedCurrentPrice: priceEditor.observedPrice,
        newPrice: priceEditor.newPrice,
      },
      idempotencyKey: crypto.randomUUID(),
    });
  }

  function discardUncertainPriceChange() {
    setUncertainPriceChange(null);
    setPriceEditor(null);
    setPriceNotice({
      kind: "uncertain",
      message:
        "El cambio incierto fue descartado. El resultado previo sigue sin confirmarse; un cambio futuro será una intención nueva.",
    });
  }

  const creationFormDiffers =
    uncertainCreation !== null &&
    (operationalName !== uncertainCreation.request.operationalName ||
      price !== uncertainCreation.request.price);

  return (
    <>
      <section className="panel" aria-labelledby="create-title">
        <h2 id="create-title">Crear producto</h2>
        <form onSubmit={(event) => void handleCreate(event)}>
          <label htmlFor="operational-name">Nombre operacional</label>
          <input
            id="operational-name"
            name="operationalName"
            value={operationalName}
            onChange={(event) => setOperationalName(event.target.value)}
            disabled={isCreating}
            required
          />

          <label htmlFor="price">Precio</label>
          <input
            id="price"
            name="price"
            value={price}
            onChange={(event) => setPrice(event.target.value)}
            inputMode="decimal"
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
                ? "Hay una intención pendiente"
                : "Crear producto"}
          </button>
        </form>

        {creationNotice && (
          <p className={`notice notice--${creationNotice.kind}`} role="status">
            {creationNotice.message}
          </p>
        )}

        {uncertainCreation && (
          <div
            className="uncertain-intention"
            role="region"
            aria-label="Intención con resultado no confirmado"
          >
            <h3>Intención pendiente de confirmación</h3>
            <dl>
              <div>
                <dt>Nombre</dt>
                <dd>{uncertainCreation.request.operationalName}</dd>
              </div>
              <div>
                <dt>Precio</dt>
                <dd>{uncertainCreation.request.price}</dd>
              </div>
            </dl>
            <p>
              El reintento usa exactamente estos datos y la misma identidad.
            </p>
            {creationFormDiffers && (
              <p className="pending-change-warning">
                Los cambios del formulario no alteran esta intención pendiente.
                Para enviarlos como una intención nueva, descartá primero la
                pendiente.
              </p>
            )}
            <div className="intention-actions">
              <button
                type="button"
                onClick={() => void submitCreation(uncertainCreation)}
                disabled={isCreating}
              >
                Reintentar misma intención
              </button>
              <button
                className="secondary-button"
                type="button"
                onClick={discardUncertainCreation}
                disabled={isCreating}
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
            onClick={() => void reloadProducts()}
            disabled={isLoading}
          >
            Actualizar
          </button>
        </div>

        {priceNotice && (
          <p className={`notice notice--${priceNotice.kind}`} role="status">
            {priceNotice.message}
          </p>
        )}

        {uncertainPriceChange && (
          <div
            className="uncertain-intention"
            role="region"
            aria-label="Cambio de Precio con resultado no confirmado"
          >
            <h3>Cambio de Precio pendiente de resolución</h3>
            <dl>
              <div>
                <dt>Producto</dt>
                <dd>{uncertainPriceChange.operationalName}</dd>
              </div>
              <div>
                <dt>Precio vigente observado</dt>
                <dd>{uncertainPriceChange.request.expectedCurrentPrice}</dd>
              </div>
              <div>
                <dt>Nuevo precio</dt>
                <dd>{uncertainPriceChange.request.newPrice}</dd>
              </div>
            </dl>
            <p>
              El reintento usa exactamente estos precios y la misma identidad.
              No se reintentará automáticamente.
            </p>
            <div className="intention-actions">
              <button
                type="button"
                onClick={() => void submitPriceChange(uncertainPriceChange)}
                disabled={isChangingPrice}
              >
                Reintentar mismo cambio de Precio
              </button>
              <button
                className="secondary-button"
                type="button"
                onClick={discardUncertainPriceChange}
                disabled={isChangingPrice}
              >
                Descartar cambio incierto
              </button>
            </div>
          </div>
        )}

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
                  <th scope="col">Acciones</th>
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
                        className="secondary-button"
                        type="button"
                        onClick={() => openPriceEditor(product)}
                        disabled={
                          isChangingPrice || uncertainPriceChange !== null
                        }
                        aria-label={`Cambiar precio de ${product.operationalName}`}
                      >
                        Cambiar precio
                      </button>
                    </td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
        )}

        {priceEditor && uncertainPriceChange === null && (
          <form
            className="price-change-form"
            onSubmit={(event) => void handlePriceChange(event)}
            aria-label={`Cambiar precio de ${priceEditor.operationalName}`}
          >
            <div>
              <span className="field-label">Producto</span>
              <strong>{priceEditor.operationalName}</strong>
            </div>
            <div>
              <span className="field-label">Precio vigente observado</span>
              <strong>{priceEditor.observedPrice}</strong>
            </div>
            <label htmlFor="new-product-price">Nuevo precio</label>
            <input
              id="new-product-price"
              name="newPrice"
              value={priceEditor.newPrice}
              onChange={(event) =>
                setPriceEditor((current) =>
                  current === null
                    ? null
                    : { ...current, newPrice: event.target.value },
                )
              }
              inputMode="decimal"
              disabled={isChangingPrice}
              required
            />
            <div className="intention-actions">
              <button type="submit" disabled={isChangingPrice}>
                {isChangingPrice ? "Cambiando…" : "Confirmar cambio de Precio"}
              </button>
              <button
                className="secondary-button"
                type="button"
                onClick={() => setPriceEditor(null)}
                disabled={isChangingPrice}
              >
                Cancelar
              </button>
            </div>
          </form>
        )}
      </section>
    </>
  );
}
