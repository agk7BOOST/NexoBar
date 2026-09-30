import { SensitiveActionDialog } from "../ui/SensitiveActionDialog.tsx";
import {
  type FormEvent,
  useCallback,
  useEffect,
  useRef,
  useState,
} from "react";
import {
  CatalogNetworkError,
  CatalogProblemError,
  changeProductPreparationConfiguration,
  changeProductPrice,
  changeProductGroup,
  changeProductOperationalName,
  createProduct,
  deleteProduct,
  createGroup,
  listGroups,
  reactivateProduct,
  retireProduct,
  type CatalogGroup,
  type ChangeProductGroupRequest,
  type ChangeProductOperationalNameRequest,
  type ChangeProductPriceRequest,
  type ChangeProductPreparationConfigurationRequest,
  type CreateProductRequest,
  type ProblemDetails,
  type Product,
  type PreparationResponsibilityOption,
  listPreparationResponsibilityOptions,
  listProducts,
} from "./catalogClient.ts";
import {
  getAntiforgeryToken,
  SessionProblemError,
} from "../identity/sessionClient.ts";

type Notice =
  | { kind: "success"; message: string }
  | { kind: "functional-error"; message: string }
  | { kind: "uncertain"; message: string };

const noUnauthorizedAction = () => undefined;

interface ProductCreationIntention {
  request: CreateProductRequest;
  idempotencyKey: string;
  antiforgeryToken: string;
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
  antiforgeryToken: string;
}

interface PreparationEditor {
  productId: string;
  operationalName: string;
  observedPreparationResponsibilityId: string | null;
  requiresPreparation: boolean;
  selectedPreparationResponsibilityId: string | null;
}

interface ProductPreparationChangeIntention {
  productId: string;
  operationalName: string;
  request: ChangeProductPreparationConfigurationRequest;
  idempotencyKey: string;
  antiforgeryToken: string;
}

interface GroupCreationIntention {
  request: { operationalName: string };
  idempotencyKey: string;
  antiforgeryToken: string;
}

interface ProductGroupChangeIntention {
  productId: string;
  operationalName: string;
  request: ChangeProductGroupRequest;
  idempotencyKey: string;
  antiforgeryToken: string;
}

interface ProductRenameIntention {
  productId: string;
  request: ChangeProductOperationalNameRequest;
  idempotencyKey: string;
  antiforgeryToken: string;
}

interface ProductLifecycleIntention {
  productId: string;
  operationalName: string;
  action: "retire" | "reactivate";
  idempotencyKey: string;
  antiforgeryToken: string;
}

interface ProductDeleteIntention {
  productId: string;
  operationalName: string;
  idempotencyKey: string;
  antiforgeryToken: string;
}

interface CatalogPanelProps {
  onUnauthorized?: () => void;
  /** Legacy test seam; mounted production panels own their administrative read. */
  products?: Product[];
  isLoading?: boolean;
  loadError?: string | null;
  reloadProducts?: () => Promise<void>;
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
      return "Este cambio de precio ya está asociado a otros datos. Revisá la operación antes de volver a intentarla.";
    default:
      return "No se pudo cambiar el Precio. Revisá los datos e intentá nuevamente.";
  }
}

function preparationChangeErrorMessage(problem: ProblemDetails): string {
  switch (problem.code) {
    case "catalog.product.preparation_configuration_concurrency_conflict":
      return "La configuración de preparación cambió desde que fue observada. El Catálogo se actualizará.";
    case "catalog.product.preparation_responsibility_not_found":
      return "El destino de preparación seleccionado ya no está disponible. El Catálogo se actualizará.";
    case "catalog.product.not_found":
      return "El Producto ya no existe.";
    case "catalog.product.not_current":
      return "El Producto ya no está vigente.";
    case "catalog.product.idempotency_key_conflict":
      return "Este cambio de preparación ya está asociado a otros datos. Revisá la operación antes de volver a intentarla.";
    default:
      return "No se pudo actualizar la configuración de preparación. Revisá los datos e intentá nuevamente.";
  }
}

function groupErrorMessage(problem: ProblemDetails): string {
  switch (problem.code) {
    case "catalog.group.operational_name_conflict":
      return "Ya existe un Grupo con ese nombre operacional.";
    case "catalog.group.invalid":
      return "Ingresá un nombre de Grupo válido.";
    case "catalog.group.not_found":
      return "El Grupo ya no existe. Se actualizarán los Grupos disponibles.";
    case "catalog.product.group_concurrency_conflict":
      return "El Grupo del Producto cambió desde que fue observado. El Catálogo se actualizará.";
    case "catalog.product.not_current":
      return "El Producto ya no está activo.";
    case "catalog.product.idempotency_key_conflict":
    case "catalog.product.group_change.idempotency_key_conflict":
      return "Este cambio de grupo ya está asociado a otros datos. Revisá la operación antes de volver a intentarla.";
    default:
      return "No se pudo actualizar el Grupo. Revisá los datos e intentá nuevamente.";
  }
}

function renameErrorMessage(problem: ProblemDetails): string {
  switch (problem.code) {
    case "catalog.product.operational_name_invalid":
      return "Ingresá un nombre operacional válido.";
    case "catalog.product.operational_name_conflict":
      return "Ya existe un Producto activo con ese nombre.";
    case "catalog.product.operational_name_concurrency_conflict":
      return "El nombre cambió desde que fue observado. El Catálogo se actualizará.";
    case "catalog.product.not_found":
      return "El Producto ya no existe.";
    case "catalog.product.idempotency_key_conflict":
    case "catalog.product.operational_name_change.idempotency_key_conflict":
      return "Este cambio de nombre ya está asociado a otros datos. Revisá la operación antes de volver a intentarla.";
    default:
      return "No se pudo renombrar el Producto. Revisá los datos e intentá nuevamente.";
  }
}

function lifecycleErrorMessage(
  problem: ProblemDetails,
  action: "retire" | "reactivate",
): string {
  switch (problem.code) {
    case "catalog.product.already_retired":
      return "El Producto ya está retirado.";
    case "catalog.product.already_active":
      return "El Producto ya está activo.";
    case "catalog.product.preparation_destination_not_current":
      return "No se puede reactivar: el destino de preparación está retirado. Reactivá primero ese destino.";
    case "catalog.product.reactivation_name_conflict":
      return "No se puede reactivar: otro Producto activo usa ese nombre.";
    case "catalog.product.not_found":
      return "El Producto ya no existe.";
    case "catalog.product.idempotency_key_conflict":
    case "catalog.product.retire.idempotency_key_conflict":
    case "catalog.product.reactivate.idempotency_key_conflict":
      return `Este ${action === "retire" ? "retiro" : "reactivación"} ya está asociado a otros datos. Revisá la operación antes de volver a intentarla.`;
    default:
      return `No se pudo ${action === "retire" ? "retirar" : "reactivar"} el Producto.`;
  }
}

export function CatalogPanel({
  onUnauthorized = noUnauthorizedAction,
  products: providedProducts,
  isLoading: providedIsLoading,
  loadError: providedLoadError,
  reloadProducts: providedReloadProducts,
}: CatalogPanelProps) {
  const [loadedProducts, setLoadedProducts] = useState<Product[]>([]);
  const [isLoading, setIsLoading] = useState(true);
  const [catalogReady, setCatalogReady] = useState(false);
  const [loadError, setLoadError] = useState<string | null>(null);
  const [isForbidden, setIsForbidden] = useState(false);
  const readGeneration = useRef(0);
  const preparationResponsibilityReadGeneration = useRef(0);
  const [
    preparationResponsibilityOptions,
    setPreparationResponsibilityOptions,
  ] = useState<PreparationResponsibilityOption[]>([]);
  const [
    isPreparationResponsibilityOptionsLoading,
    setIsPreparationResponsibilityOptionsLoading,
  ] = useState(true);
  const [
    preparationResponsibilityOptionsError,
    setPreparationResponsibilityOptionsError,
  ] = useState<string | null>(null);
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
  const [preparationEditor, setPreparationEditor] =
    useState<PreparationEditor | null>(null);
  const [isChangingPreparation, setIsChangingPreparation] = useState(false);
  const [preparationNotice, setPreparationNotice] = useState<Notice | null>(
    null,
  );
  const [uncertainPreparationChange, setUncertainPreparationChange] =
    useState<ProductPreparationChangeIntention | null>(null);
  const [groups, setGroups] = useState<CatalogGroup[]>([]);
  const [groupName, setGroupName] = useState("");
  const [groupNotice, setGroupNotice] = useState<Notice | null>(null);
  const [isCreatingGroup, setIsCreatingGroup] = useState(false);
  const [uncertainGroupCreation, setUncertainGroupCreation] =
    useState<GroupCreationIntention | null>(null);
  const [groupEditor, setGroupEditor] = useState<{
    productId: string;
    operationalName: string;
    observedGroupId: string | null;
    selectedGroupId: string | null;
  } | null>(null);
  const [groupNoticeForProduct, setGroupNoticeForProduct] =
    useState<Notice | null>(null);
  const [uncertainGroupChange, setUncertainGroupChange] =
    useState<ProductGroupChangeIntention | null>(null);
  const [renameEditor, setRenameEditor] = useState<{
    productId: string;
    observedName: string;
    newName: string;
  } | null>(null);
  const [renameNotice, setRenameNotice] = useState<Notice | null>(null);
  const [uncertainRename, setUncertainRename] =
    useState<ProductRenameIntention | null>(null);
  const [lifecycleNotice, setLifecycleNotice] = useState<Notice | null>(null);
  const [uncertainLifecycle, setUncertainLifecycle] =
    useState<ProductLifecycleIntention | null>(null);
  const [isChangingLifecycle, setIsChangingLifecycle] = useState(false);
  const [deleteCandidate, setDeleteCandidate] = useState<Product | null>(null);
  const [uncertainDelete, setUncertainDelete] =
    useState<ProductDeleteIntention | null>(null);
  const [isDeleting, setIsDeleting] = useState(false);
  const [deleteNotice, setDeleteNotice] = useState<Notice | null>(null);
  const [isChangingGroup, setIsChangingGroup] = useState(false);
  const [isRenaming, setIsRenaming] = useState(false);
  const [isLoadingGroups, setIsLoadingGroups] = useState(true);
  const groupsReadGeneration = useRef(0);
  const [catalogRefreshRequired, setCatalogRefreshRequired] = useState(false);
  const editorOpener = useRef<HTMLElement | null>(null);
  const [editorRequest, setEditorRequest] = useState<{
    kind: "price" | "preparation" | "group" | "rename" | "delete";
    sequence: number;
  }>();

  useEffect(() => {
    if (!editorRequest) return;
    const editor = document.getElementById(
      `catalog-${editorRequest.kind}-editor`,
    );
    const field = editor?.querySelector<HTMLInputElement | HTMLSelectElement>(
      "input:not([disabled]), select:not([disabled])",
    );
    (field ?? editor)?.focus();
    if (
      (editorRequest.kind === "price" || editorRequest.kind === "rename") &&
      field instanceof HTMLInputElement
    )
      field.select();
    editor?.scrollIntoView?.({ block: "start" });
  }, [editorRequest]);

  function focusOpenedEditor(
    kind: "price" | "preparation" | "group" | "rename" | "delete",
  ) {
    editorOpener.current =
      document.activeElement instanceof HTMLElement
        ? document.activeElement
        : null;
    setEditorRequest((current) => ({
      kind,
      sequence: (current?.sequence ?? 0) + 1,
    }));
  }

  function returnToProduct() {
    if (
      editorOpener.current?.isConnected &&
      !editorOpener.current.closest("[hidden]")
    )
      editorOpener.current.focus();
    else document.getElementById("products-title")?.focus();
    editorOpener.current?.scrollIntoView?.({ block: "nearest" });
  }

  const loadCatalog = useCallback(
    async (generation: number) => {
      try {
        const loadedProducts = await listProducts();
        if (generation === readGeneration.current) {
          setLoadedProducts(loadedProducts);
        }
        return generation === readGeneration.current;
      } catch (error) {
        if (generation !== readGeneration.current) return;
        if (error instanceof CatalogProblemError) {
          if (error.problem.status === 401) {
            onUnauthorized();
            return;
          }
          if (error.problem.status === 403) {
            setLoadedProducts([]);
            setIsForbidden(true);
            return;
          }
        }
        setLoadError("No se pudo cargar el listado de productos.");
        return false;
      } finally {
        if (generation === readGeneration.current) {
          setIsLoading(false);
          setCatalogReady(true);
        }
      }
    },
    [onUnauthorized],
  );

  const reloadCatalog = useCallback(() => {
    const generation = ++readGeneration.current;
    setIsLoading(true);
    setLoadError(null);
    return loadCatalog(generation);
  }, [loadCatalog]);

  const loadPreparationResponsibilityOptions = useCallback(
    async (generation: number) => {
      try {
        const loaded = await listPreparationResponsibilityOptions();
        if (generation === preparationResponsibilityReadGeneration.current) {
          setPreparationResponsibilityOptions(loaded);
        }
      } catch (error) {
        if (generation !== preparationResponsibilityReadGeneration.current)
          return;
        if (error instanceof CatalogProblemError) {
          if (error.problem.status === 401) {
            onUnauthorized();
            return;
          }
          if (error.problem.status === 403) {
            setLoadedProducts([]);
            setPreparationResponsibilityOptions([]);
            setIsForbidden(true);
            return;
          }
        }
        setPreparationResponsibilityOptionsError(
          "No se pudieron cargar los destinos de preparación.",
        );
      } finally {
        if (generation === preparationResponsibilityReadGeneration.current) {
          setIsPreparationResponsibilityOptionsLoading(false);
        }
      }
    },
    [onUnauthorized],
  );

  const reloadPreparationResponsibilityOptions = useCallback(() => {
    const generation = ++preparationResponsibilityReadGeneration.current;
    setIsPreparationResponsibilityOptionsLoading(true);
    setPreparationResponsibilityOptionsError(null);
    return loadPreparationResponsibilityOptions(generation);
  }, [loadPreparationResponsibilityOptions]);

  const loadGroups = useCallback(
    async (generation: number) => {
      try {
        const loaded = await listGroups();
        if (generation === groupsReadGeneration.current) setGroups(loaded);
      } catch (error) {
        if (generation !== groupsReadGeneration.current) return;
        if (error instanceof CatalogProblemError) {
          if (error.problem.status === 401) onUnauthorized();
          if (error.problem.status === 403) setIsForbidden(true);
        }
      } finally {
        if (generation === groupsReadGeneration.current)
          setIsLoadingGroups(false);
      }
    },
    [onUnauthorized],
  );

  const reloadGroups = useCallback(() => {
    const generation = ++groupsReadGeneration.current;
    setIsLoadingGroups(true);
    return loadGroups(generation);
  }, [loadGroups]);

  useEffect(() => {
    if (providedProducts !== undefined) return;
    void loadCatalog(++readGeneration.current);
    return () => {
      readGeneration.current += 1;
    };
  }, [providedProducts, loadCatalog]); // The parent key fences this read at each Identity lifecycle.

  useEffect(() => {
    void loadPreparationResponsibilityOptions(
      ++preparationResponsibilityReadGeneration.current,
    );
    return () => {
      preparationResponsibilityReadGeneration.current += 1;
    };
  }, [loadPreparationResponsibilityOptions]); // The parent key fences this Catalog-owned lookup at each Identity lifecycle.

  useEffect(() => {
    void loadGroups(++groupsReadGeneration.current);
    return () => {
      groupsReadGeneration.current += 1;
    };
  }, [loadGroups]);

  const products = providedProducts ?? loadedProducts;
  const displayedIsLoading = providedIsLoading ?? isLoading;
  const displayedLoadError = providedLoadError ?? loadError;
  const reloadProducts = providedReloadProducts ?? reloadCatalog;
  async function refreshConfirmedCatalog() {
    try {
      const refreshed: unknown = await reloadProducts();
      setCatalogRefreshRequired(refreshed === false);
    } catch {
      setCatalogRefreshRequired(true);
    }
  }
  const showSetup =
    providedProducts !== undefined ? providedIsLoading !== true : catalogReady;

  function clearSettledNotices() {
    const keepPending = (notice: Notice | null, isPending: boolean) =>
      isPending && notice?.kind === "uncertain" ? notice : null;
    setGroupNotice((current) =>
      keepPending(current, uncertainGroupCreation !== null),
    );
    setCreationNotice((current) =>
      keepPending(current, uncertainCreation !== null),
    );
    setPriceNotice((current) =>
      keepPending(current, uncertainPriceChange !== null),
    );
    setPreparationNotice((current) =>
      keepPending(current, uncertainPreparationChange !== null),
    );
    setGroupNoticeForProduct((current) =>
      keepPending(current, uncertainGroupChange !== null),
    );
    setRenameNotice((current) =>
      keepPending(current, uncertainRename !== null),
    );
    setLifecycleNotice((current) =>
      keepPending(current, uncertainLifecycle !== null),
    );
    setDeleteNotice((current) =>
      keepPending(current, uncertainDelete !== null),
    );
  }

  async function prepareMutation(
    setNotice: (notice: Notice) => void = setCreationNotice,
  ): Promise<string | null> {
    try {
      return await getAntiforgeryToken();
    } catch (error) {
      if (error instanceof SessionProblemError && error.status === 401) {
        onUnauthorized();
      }
      setNotice({
        kind: "functional-error",
        message: "No se pudo preparar la operación segura del Catálogo.",
      });
      return null;
    }
  }

  async function submitCreation(intention: ProductCreationIntention) {
    clearSettledNotices();
    setCreationNotice(null);
    setIsCreating(true);

    const formMatchesIntention =
      operationalName === intention.request.operationalName &&
      price === intention.request.price;

    try {
      await createProduct(
        intention.request,
        intention.idempotencyKey,
        intention.antiforgeryToken,
      );
      setUncertainCreation(null);
      if (formMatchesIntention) {
        setOperationalName("");
        setPrice("");
      }
      setCreationNotice({
        kind: "success",
        message: `Se creó “${intention.request.operationalName}”.`,
      });
      await refreshConfirmedCatalog();
    } catch (error) {
      if (error instanceof CatalogProblemError) {
        if (error.problem.status === 401) {
          onUnauthorized();
          return;
        }
        if (error.problem.status === 403) {
          setLoadedProducts([]);
          setIsForbidden(true);
          return;
        }
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

    const antiforgeryToken = await prepareMutation();
    if (antiforgeryToken === null) return;

    await submitCreation({
      request: {
        operationalName,
        price,
        requiresPreparation: false,
      },
      idempotencyKey: crypto.randomUUID(),
      antiforgeryToken,
    });
  }

  function discardUncertainCreation() {
    setUncertainCreation(null);
    setCreationNotice({
      kind: "uncertain",
      message:
        "Descartaste el reintento pendiente. El resultado anterior sigue sin confirmarse; el próximo envío será otra operación.",
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
    focusOpenedEditor("price");
  }

  async function submitPriceChange(intention: ProductPriceChangeIntention) {
    clearSettledNotices();
    setPriceNotice(null);
    setIsChangingPrice(true);

    try {
      await changeProductPrice(
        intention.productId,
        intention.request,
        intention.idempotencyKey,
        intention.antiforgeryToken,
      );
      setUncertainPriceChange(null);
      setPriceEditor(null);
      setPriceNotice({
        kind: "success",
        message: `Se actualizó el precio de “${intention.operationalName}”.`,
      });
      await refreshConfirmedCatalog();
      returnToProduct();
    } catch (error) {
      if (error instanceof CatalogProblemError) {
        if (error.problem.status === 401) {
          onUnauthorized();
          return;
        }
        if (error.problem.status === 403) {
          setLoadedProducts([]);
          setIsForbidden(true);
          return;
        }
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

    const antiforgeryToken = await prepareMutation();
    if (antiforgeryToken === null) return;

    await submitPriceChange({
      productId: priceEditor.productId,
      operationalName: priceEditor.operationalName,
      request: {
        expectedCurrentPrice: priceEditor.observedPrice,
        newPrice: priceEditor.newPrice,
      },
      idempotencyKey: crypto.randomUUID(),
      antiforgeryToken,
    });
  }

  function discardUncertainPriceChange() {
    setUncertainPriceChange(null);
    setPriceEditor(null);
    setPriceNotice({
      kind: "uncertain",
      message:
        "Dejaste de reintentar. Esto no deshace la operación; su resultado sigue sin confirmarse.",
    });
  }

  function openPreparationEditor(product: Product) {
    if (uncertainPreparationChange !== null || isChangingPreparation) {
      return;
    }
    setPreparationEditor({
      productId: product.id,
      operationalName: product.operationalName,
      observedPreparationResponsibilityId: product.preparationResponsibilityId,
      requiresPreparation: product.requiresPreparation,
      selectedPreparationResponsibilityId: product.preparationResponsibilityId,
    });
    setPreparationNotice(null);
    focusOpenedEditor("preparation");
  }

  async function submitPreparationChange(
    intention: ProductPreparationChangeIntention,
  ) {
    clearSettledNotices();
    setPreparationNotice(null);
    setIsChangingPreparation(true);
    try {
      await changeProductPreparationConfiguration(
        intention.productId,
        intention.request,
        intention.idempotencyKey,
        intention.antiforgeryToken,
      );
      setUncertainPreparationChange(null);
      setPreparationEditor(null);
      setPreparationNotice({
        kind: "success",
        message: `Se actualizó la preparación de “${intention.operationalName}”.`,
      });
      await refreshConfirmedCatalog();
    } catch (error) {
      if (error instanceof CatalogProblemError) {
        if (error.problem.status === 401) {
          onUnauthorized();
          return;
        }
        if (error.problem.status === 403) {
          setLoadedProducts([]);
          setPreparationResponsibilityOptions([]);
          setIsForbidden(true);
          return;
        }
        setUncertainPreparationChange(null);
        setPreparationEditor(null);
        setPreparationNotice({
          kind: "functional-error",
          message: preparationChangeErrorMessage(error.problem),
        });
        if (
          error.problem.code ===
            "catalog.product.preparation_configuration_concurrency_conflict" ||
          error.problem.code ===
            "catalog.product.preparation_responsibility_not_found"
        ) {
          await reloadProducts();
        }
        if (
          error.problem.code ===
          "catalog.product.preparation_responsibility_not_found"
        ) {
          await reloadPreparationResponsibilityOptions();
        }
      } else {
        setUncertainPreparationChange(intention);
        setPreparationNotice({
          kind: "uncertain",
          message:
            error instanceof CatalogNetworkError
              ? "Resultado no confirmado: se perdió la comunicación y no sabemos si la configuración de preparación fue actualizada."
              : "Resultado no confirmado: no fue posible confirmar la respuesta del servidor.",
        });
      }
    } finally {
      setIsChangingPreparation(false);
    }
  }

  async function handlePreparationChange(event: FormEvent<HTMLFormElement>) {
    event.preventDefault();
    if (preparationEditor === null || uncertainPreparationChange !== null) {
      return;
    }
    if (
      preparationEditor.requiresPreparation &&
      preparationEditor.selectedPreparationResponsibilityId === null
    ) {
      setPreparationNotice({
        kind: "functional-error",
        message: "Seleccioná un destino de preparación.",
      });
      return;
    }
    const antiforgeryToken = await prepareMutation(setPreparationNotice);
    if (antiforgeryToken === null) return;
    await submitPreparationChange({
      productId: preparationEditor.productId,
      operationalName: preparationEditor.operationalName,
      request: {
        expectedCurrentPreparationResponsibilityId:
          preparationEditor.observedPreparationResponsibilityId,
        newPreparationResponsibilityId: preparationEditor.requiresPreparation
          ? preparationEditor.selectedPreparationResponsibilityId
          : null,
      },
      idempotencyKey: crypto.randomUUID(),
      antiforgeryToken,
    });
  }

  function discardUncertainPreparationChange() {
    setUncertainPreparationChange(null);
    setPreparationEditor(null);
    setPreparationNotice({
      kind: "uncertain",
      message:
        "Dejaste de reintentar. Esto no deshace la operación; su resultado sigue sin confirmarse.",
    });
  }

  async function submitGroupCreation(intention: GroupCreationIntention) {
    clearSettledNotices();
    setGroupNotice(null);
    setIsCreatingGroup(true);
    try {
      await createGroup(
        intention.request,
        intention.idempotencyKey,
        intention.antiforgeryToken,
      );
      setUncertainGroupCreation(null);
      setGroupName("");
      setGroupNotice({
        kind: "success",
        message: `Se creó el grupo “${intention.request.operationalName}”.`,
      });
      await reloadGroups();
    } catch (error) {
      if (error instanceof CatalogProblemError) {
        if (error.problem.status === 401) {
          onUnauthorized();
          return;
        }
        if (error.problem.status === 403) {
          setIsForbidden(true);
          return;
        }
        setUncertainGroupCreation(null);
        setGroupNotice({
          kind: "functional-error",
          message: groupErrorMessage(error.problem),
        });
      } else {
        setUncertainGroupCreation(intention);
        setGroupNotice({
          kind: "uncertain",
          message:
            "Resultado no confirmado: no fue posible confirmar la creación del Grupo.",
        });
      }
    } finally {
      setIsCreatingGroup(false);
    }
  }

  async function handleGroupCreation(event: FormEvent<HTMLFormElement>) {
    event.preventDefault();
    if (uncertainGroupCreation !== null) return;
    if (groupName.trim() === "") {
      setGroupNotice({
        kind: "functional-error",
        message: "Ingresá un nombre de Grupo válido.",
      });
      return;
    }
    const antiforgeryToken = await prepareMutation(setGroupNotice);
    if (antiforgeryToken === null) return;
    await submitGroupCreation({
      request: { operationalName: groupName },
      idempotencyKey: crypto.randomUUID(),
      antiforgeryToken,
    });
  }

  function openGroupEditor(product: Product) {
    if (!product.isActive || uncertainGroupChange !== null || isChangingGroup)
      return;
    setGroupEditor({
      productId: product.id,
      operationalName: product.operationalName,
      observedGroupId: product.groupId ?? null,
      selectedGroupId: product.groupId ?? null,
    });
    focusOpenedEditor("group");
    setGroupNoticeForProduct(null);
  }

  async function submitGroupChange(intention: ProductGroupChangeIntention) {
    clearSettledNotices();
    setGroupNoticeForProduct(null);
    setIsChangingGroup(true);
    try {
      await changeProductGroup(
        intention.productId,
        intention.request,
        intention.idempotencyKey,
        intention.antiforgeryToken,
      );
      setUncertainGroupChange(null);
      setGroupEditor(null);
      setGroupNoticeForProduct({
        kind: "success",
        message: `Se actualizó el grupo de “${intention.operationalName}”.`,
      });
      await refreshConfirmedCatalog();
      returnToProduct();
    } catch (error) {
      if (error instanceof CatalogProblemError) {
        if (error.problem.status === 401) {
          onUnauthorized();
          return;
        }
        if (error.problem.status === 403) {
          setIsForbidden(true);
          return;
        }
        setUncertainGroupChange(null);
        setGroupEditor(null);
        setGroupNoticeForProduct({
          kind: "functional-error",
          message: groupErrorMessage(error.problem),
        });
        if (
          error.problem.code === "catalog.product.group_concurrency_conflict" ||
          error.problem.code === "catalog.group.not_found" ||
          error.problem.code === "catalog.product.not_current"
        ) {
          await reloadProducts();
        }
        if (error.problem.code === "catalog.group.not_found")
          await reloadGroups();
      } else {
        setUncertainGroupChange(intention);
        setGroupNoticeForProduct({
          kind: "uncertain",
          message:
            "Resultado no confirmado: no fue posible confirmar el cambio de Grupo.",
        });
      }
    } finally {
      setIsChangingGroup(false);
    }
  }

  async function handleGroupChange(event: FormEvent<HTMLFormElement>) {
    event.preventDefault();
    if (
      groupEditor === null ||
      uncertainGroupChange !== null ||
      groupEditor.selectedGroupId === groupEditor.observedGroupId
    )
      return;
    const antiforgeryToken = await prepareMutation(setGroupNoticeForProduct);
    if (antiforgeryToken === null) return;
    await submitGroupChange({
      productId: groupEditor.productId,
      operationalName: groupEditor.operationalName,
      request: {
        expectedCurrentGroupId: groupEditor.observedGroupId,
        newGroupId: groupEditor.selectedGroupId,
      },
      idempotencyKey: crypto.randomUUID(),
      antiforgeryToken,
    });
  }

  function openRenameEditor(product: Product) {
    if (uncertainRename !== null || isRenaming) return;
    setRenameEditor({
      productId: product.id,
      observedName: product.operationalName,
      newName: product.operationalName,
    });
    focusOpenedEditor("rename");
    setRenameNotice(null);
  }

  async function submitRename(intention: ProductRenameIntention) {
    clearSettledNotices();
    setRenameNotice(null);
    setIsRenaming(true);
    try {
      await changeProductOperationalName(
        intention.productId,
        intention.request,
        intention.idempotencyKey,
        intention.antiforgeryToken,
      );
      setUncertainRename(null);
      setRenameEditor(null);
      setRenameNotice({
        kind: "success",
        message: "Se guardó el nuevo nombre del producto.",
      });
      await refreshConfirmedCatalog();
      returnToProduct();
    } catch (error) {
      if (error instanceof CatalogProblemError) {
        if (error.problem.status === 401) {
          onUnauthorized();
          return;
        }
        if (error.problem.status === 403) {
          setIsForbidden(true);
          return;
        }
        setUncertainRename(null);
        setRenameEditor(null);
        setRenameNotice({
          kind: "functional-error",
          message: renameErrorMessage(error.problem),
        });
        if (
          error.problem.code ===
          "catalog.product.operational_name_concurrency_conflict"
        )
          await reloadProducts();
      } else {
        setUncertainRename(intention);
        setRenameNotice({
          kind: "uncertain",
          message:
            "Resultado no confirmado: no fue posible confirmar el renombre.",
        });
      }
    } finally {
      setIsRenaming(false);
    }
  }

  async function handleRename(event: FormEvent<HTMLFormElement>) {
    event.preventDefault();
    if (
      renameEditor === null ||
      uncertainRename !== null ||
      renameEditor.newName.trim() === ""
    ) {
      if (renameEditor?.newName.trim() === "")
        setRenameNotice({
          kind: "functional-error",
          message: "Ingresá un nombre operacional válido.",
        });
      return;
    }
    const antiforgeryToken = await prepareMutation(setRenameNotice);
    if (antiforgeryToken === null) return;
    await submitRename({
      productId: renameEditor.productId,
      request: {
        expectedCurrentOperationalName: renameEditor.observedName,
        newOperationalName: renameEditor.newName,
      },
      idempotencyKey: crypto.randomUUID(),
      antiforgeryToken,
    });
  }

  async function submitLifecycle(intention: ProductLifecycleIntention) {
    clearSettledNotices();
    setLifecycleNotice(null);
    setIsChangingLifecycle(true);
    try {
      const action =
        intention.action === "retire" ? retireProduct : reactivateProduct;
      await action(
        intention.productId,
        intention.idempotencyKey,
        intention.antiforgeryToken,
      );
      setUncertainLifecycle(null);
      setLifecycleNotice({
        kind: "success",
        message:
          intention.action === "retire"
            ? `Se retiró “${intention.operationalName}”.`
            : `“${intention.operationalName}” está activo y disponible.`,
      });
      await refreshConfirmedCatalog();
    } catch (error) {
      if (error instanceof CatalogProblemError) {
        if (error.problem.status === 401) {
          onUnauthorized();
          return;
        }
        if (error.problem.status === 403) {
          setIsForbidden(true);
          return;
        }
        setUncertainLifecycle(null);
        setLifecycleNotice({
          kind: "functional-error",
          message: lifecycleErrorMessage(error.problem, intention.action),
        });
        if (
          error.problem.code === "catalog.product.already_retired" ||
          error.problem.code === "catalog.product.already_active"
        )
          await reloadProducts();
      } else {
        setUncertainLifecycle(intention);
        setLifecycleNotice({
          kind: "uncertain",
          message:
            "Resultado no confirmado: no fue posible confirmar el cambio de ciclo de vida.",
        });
      }
    } finally {
      setIsChangingLifecycle(false);
    }
  }

  async function handleLifecycle(
    product: Product,
    action: "retire" | "reactivate",
  ) {
    if (uncertainLifecycle !== null || isChangingLifecycle) return;
    const antiforgeryToken = await prepareMutation(setLifecycleNotice);
    if (antiforgeryToken === null) return;
    await submitLifecycle({
      productId: product.id,
      operationalName: product.operationalName,
      action,
      idempotencyKey: crypto.randomUUID(),
      antiforgeryToken,
    });
  }

  async function submitDelete(intention: ProductDeleteIntention) {
    clearSettledNotices();
    setDeleteNotice(null);
    setIsDeleting(true);
    try {
      await deleteProduct(
        intention.productId,
        intention.idempotencyKey,
        intention.antiforgeryToken,
      );
      setUncertainDelete(null);
      setDeleteCandidate(null);
      setDeleteNotice({
        kind: "success",
        message: `Se eliminó definitivamente “${intention.operationalName}”.`,
      });
      await refreshConfirmedCatalog();
      returnToProduct();
    } catch (error) {
      if (error instanceof CatalogProblemError) {
        if (error.problem.status === 401) {
          onUnauthorized();
          return;
        }
        if (error.problem.status === 403) {
          setIsForbidden(true);
          return;
        }
        setUncertainDelete(null);
        setDeleteNotice({
          kind: "functional-error",
          message:
            error.problem.code ===
            "catalog.product.delete.confirmed_participation"
              ? products.find((product) => product.id === intention.productId)
                  ?.isActive === true
                ? "El Producto no puede eliminarse porque participó en un Pedido confirmado. Podés retirarlo por separado."
                : products.find((product) => product.id === intention.productId)
                      ?.isActive === false
                  ? "El Producto no puede eliminarse porque participó en un Pedido confirmado. Ya está retirado; se conserva su historial."
                  : "El Producto no puede eliminarse porque participó en un Pedido confirmado. Actualizá la lista para consultar su estado."
              : error.problem.code === "catalog.product.not_found"
                ? "El Producto ya no existe en el Catálogo."
                : "No se pudo eliminar el Producto.",
        });
      } else {
        setUncertainDelete(intention);
        setDeleteNotice({
          kind: "uncertain",
          message:
            "No pudimos confirmar si se eliminó el producto. Podés reintentar esta operación sin duplicarla.",
        });
      }
    } finally {
      setIsDeleting(false);
    }
  }

  async function handleDelete(product: Product) {
    if (isDeleting || uncertainDelete !== null) return;
    setIsDeleting(true);
    const antiforgeryToken = await prepareMutation(setDeleteNotice);
    if (antiforgeryToken === null) {
      setIsDeleting(false);
      return;
    }
    await submitDelete({
      productId: product.id,
      operationalName: product.operationalName,
      idempotencyKey: crypto.randomUUID(),
      antiforgeryToken,
    });
  }

  function preparationDestinationLabel(
    preparationResponsibilityId: string | null,
  ): string {
    if (preparationResponsibilityId === null) {
      return "Sin preparación";
    }
    const option = preparationResponsibilityOptions.find(
      (current) => current.id === preparationResponsibilityId,
    );
    return option === undefined
      ? `Responsabilidad no disponible (Id: ${preparationResponsibilityId})`
      : option.operationalName;
  }

  if (isForbidden) {
    return null;
  }

  const creationFormDiffers =
    uncertainCreation !== null &&
    (operationalName !== uncertainCreation.request.operationalName ||
      price !== uncertainCreation.request.price);

  const setupSections = (
    <>
      <section className="panel" aria-labelledby="groups-title">
        <h2 id="groups-title" tabIndex={-1}>
          Grupos
        </h2>
        <form onSubmit={(event) => void handleGroupCreation(event)}>
          <label htmlFor="group-operational-name">Nombre del grupo</label>
          <input
            id="group-operational-name"
            value={groupName}
            onChange={(event) => setGroupName(event.target.value)}
            disabled={isCreatingGroup}
          />
          <button
            type="submit"
            disabled={isCreatingGroup || uncertainGroupCreation !== null}
          >
            {isCreatingGroup ? "Creando…" : "Crear Grupo"}
          </button>
        </form>
        {groupNotice && (
          <p
            className={`notice notice--${groupNotice.kind}`}
            role={groupNotice.kind === "functional-error" ? "alert" : "status"}
          >
            {groupNotice.message}
          </p>
        )}
        {uncertainGroupCreation && (
          <div
            className="uncertain-intention"
            role="region"
            aria-label="Creación de Grupo con resultado no confirmado"
          >
            <p>
              La creación de «{uncertainGroupCreation.request.operationalName}»
              no fue confirmada.
            </p>
            <p>
              El reintento conserva el mismo nombre sin duplicar la operación.
            </p>
            <div className="intention-actions">
              <button
                type="button"
                onClick={() => void submitGroupCreation(uncertainGroupCreation)}
                disabled={isCreatingGroup}
              >
                Reintentar misma creación
              </button>
              <button
                className="secondary-button"
                type="button"
                onClick={() => {
                  setUncertainGroupCreation(null);
                  setGroupNotice({
                    kind: "uncertain",
                    message: "Descartaste el reintento pendiente.",
                  });
                }}
              >
                Dejar de reintentar
              </button>
              <p>
                Esto no deshace la operación. Su resultado sigue sin
                confirmarse.
              </p>
            </div>
          </div>
        )}
        {isLoadingGroups ? (
          <p>Cargando Grupos…</p>
        ) : groups.length === 0 ? (
          <p>No hay Grupos creados.</p>
        ) : (
          <ul aria-label="Grupos del Catálogo">
            {groups.map((group) => (
              <li key={group.id}>{group.operationalName}</li>
            ))}
          </ul>
        )}
      </section>
      <section className="panel" aria-labelledby="create-title">
        <h2 id="create-title" tabIndex={-1}>
          Crear producto
        </h2>
        <form
          className="paired-fields-form"
          onSubmit={(event) => void handleCreate(event)}
        >
          <label htmlFor="operational-name">Nombre</label>
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
                ? "Hay una operación pendiente de confirmar"
                : "Crear producto"}
          </button>
        </form>

        {creationNotice && (
          <p
            className={`notice notice--${creationNotice.kind}`}
            role={
              creationNotice.kind === "functional-error" ? "alert" : "status"
            }
          >
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
              El reintento conserva los mismos datos sin duplicar la operación.
            </p>
            {creationFormDiffers && (
              <p className="pending-change-warning">
                Los cambios del formulario no alteran la operación pendiente.
                Para enviarlos como otra operación, descartá primero el
                reintento pendiente.
              </p>
            )}
            <div className="intention-actions">
              <button
                type="button"
                onClick={() => void submitCreation(uncertainCreation)}
                disabled={isCreating}
              >
                Reintentar esta operación
              </button>
              <button
                className="secondary-button"
                type="button"
                onClick={discardUncertainCreation}
                disabled={isCreating}
              >
                Dejar de reintentar
              </button>
              <p>
                Esto no deshace la operación. Su resultado sigue sin
                confirmarse.
              </p>
            </div>
          </div>
        )}
      </section>
    </>
  );

  return (
    <>
      {showSetup && products.length === 0 && setupSections}
      <section className="panel" aria-labelledby="products-title">
        <div className="section-heading">
          <h2 id="products-title" tabIndex={-1}>
            Productos
          </h2>
          {products.length > 0 && (
            <div className="catalog-section-actions">
              <button
                type="button"
                className="secondary-button"
                onClick={() => {
                  const heading = document.getElementById("create-title");
                  heading?.focus();
                  heading?.scrollIntoView?.({ block: "start" });
                }}
              >
                Nuevo producto
              </button>
              <button
                type="button"
                className="secondary-button"
                onClick={() => {
                  const heading = document.getElementById("groups-title");
                  heading?.focus();
                  heading?.scrollIntoView?.({ block: "start" });
                }}
              >
                Administrar grupos
              </button>
            </div>
          )}
          <button
            className="secondary-button"
            type="button"
            onClick={() => {
              clearSettledNotices();
              void reloadProducts();
            }}
            disabled={displayedIsLoading}
          >
            Actualizar
          </button>
        </div>

        {priceNotice && (
          <p
            className={`notice notice--${priceNotice.kind}`}
            role={priceNotice.kind === "functional-error" ? "alert" : "status"}
          >
            {priceNotice.message}
          </p>
        )}
        {preparationNotice && (
          <p
            className={`notice notice--${preparationNotice.kind}`}
            role={
              preparationNotice.kind === "functional-error" ? "alert" : "status"
            }
          >
            {preparationNotice.message}
          </p>
        )}
        {groupNoticeForProduct && (
          <p
            className={`notice notice--${groupNoticeForProduct.kind}`}
            role={
              groupNoticeForProduct.kind === "functional-error"
                ? "alert"
                : "status"
            }
          >
            {groupNoticeForProduct.message}
          </p>
        )}
        {renameNotice && (
          <p
            className={`notice notice--${renameNotice.kind}`}
            role={renameNotice.kind === "functional-error" ? "alert" : "status"}
          >
            {renameNotice.message}
          </p>
        )}
        {lifecycleNotice && (
          <p
            className={`notice notice--${lifecycleNotice.kind}`}
            role={
              lifecycleNotice.kind === "functional-error" ? "alert" : "status"
            }
          >
            {lifecycleNotice.message}
          </p>
        )}
        {deleteNotice && (!deleteCandidate || uncertainDelete !== null) && (
          <p
            className={`notice notice--${deleteNotice.kind}`}
            role={deleteNotice.kind === "functional-error" ? "alert" : "status"}
          >
            {deleteNotice.message}
          </p>
        )}

        {isPreparationResponsibilityOptionsLoading && (
          <p>Cargando destinos de preparación…</p>
        )}
        {!isPreparationResponsibilityOptionsLoading &&
          preparationResponsibilityOptionsError && (
            <p role="alert">{preparationResponsibilityOptionsError}</p>
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
              El reintento conserva estos precios sin duplicar el cambio. No se
              hará automáticamente.
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
                Dejar de reintentar
              </button>
              <p>
                Esto no deshace la operación. Su resultado sigue sin
                confirmarse.
              </p>
            </div>
          </div>
        )}

        {uncertainPreparationChange && (
          <div
            className="uncertain-intention"
            role="region"
            aria-label="Configuración de preparación con resultado no confirmado"
          >
            <h3>Configuración de preparación pendiente de resolución</h3>
            <dl>
              <div>
                <dt>Producto</dt>
                <dd>{uncertainPreparationChange.operationalName}</dd>
              </div>
              <div>
                <dt>Destino vigente observado</dt>
                <dd>
                  {preparationDestinationLabel(
                    uncertainPreparationChange.request
                      .expectedCurrentPreparationResponsibilityId,
                  )}
                </dd>
              </div>
              <div>
                <dt>Nuevo destino</dt>
                <dd>
                  {preparationDestinationLabel(
                    uncertainPreparationChange.request
                      .newPreparationResponsibilityId,
                  )}
                </dd>
              </div>
            </dl>
            <p>
              El reintento conserva este destino y el estado observado sin
              duplicar el cambio. No se hará automáticamente.
            </p>
            <div className="intention-actions">
              <button
                type="button"
                onClick={() =>
                  void submitPreparationChange(uncertainPreparationChange)
                }
                disabled={isChangingPreparation}
              >
                Reintentar misma configuración de preparación
              </button>
              <button
                className="secondary-button"
                type="button"
                onClick={discardUncertainPreparationChange}
                disabled={isChangingPreparation}
              >
                Dejar de reintentar
              </button>
              <p>
                Esto no deshace la operación. Su resultado sigue sin
                confirmarse.
              </p>
            </div>
          </div>
        )}

        {catalogRefreshRequired && (
          <div role="status">
            <p>
              El cambio está confirmado, pero no pudimos actualizar la lista.
            </p>
            <button
              type="button"
              onClick={() => void refreshConfirmedCatalog()}
            >
              Reintentar consulta
            </button>
          </div>
        )}
        {displayedIsLoading && <p>Cargando productos…</p>}
        {!displayedIsLoading && displayedLoadError && (
          <p role="alert">{displayedLoadError}</p>
        )}
        {!displayedIsLoading &&
          !displayedLoadError &&
          products.length === 0 && <p>No hay productos vigentes.</p>}
        {!displayedIsLoading && !displayedLoadError && products.length > 0 && (
          <div className="table-scroll catalog-products-table">
            <table>
              <caption>
                Ciclo de vida y disponibilidad son independientes. Retirar no
                elimina el Producto ni modifica operaciones confirmadas;
                reactivar lo vuelve activo y disponible según la autoridad del
                Catálogo.
              </caption>
              <thead>
                <tr>
                  <th scope="col" id="catalog-column-name">
                    Nombre
                  </th>
                  <th scope="col" id="catalog-column-price">
                    Precio
                  </th>
                  <th scope="col" id="catalog-column-lifecycle">
                    Ciclo de vida
                  </th>
                  <th scope="col" id="catalog-column-availability">
                    Disponibilidad temporal
                  </th>
                  <th scope="col" id="catalog-column-group">
                    Grupo
                  </th>
                  <th scope="col" id="catalog-column-preparation">
                    Preparación
                  </th>
                  <th scope="col" id="catalog-column-actions">
                    Acciones
                  </th>
                </tr>
              </thead>
              <tbody>
                {products.map((product) => (
                  <tr key={product.id} aria-label={product.operationalName}>
                    <td headers="catalog-column-name" data-label="Producto">
                      <strong>{product.operationalName}</strong>
                    </td>
                    <td headers="catalog-column-price" data-label="Precio">
                      {product.price}
                    </td>
                    <td headers="catalog-column-lifecycle" data-label="Estado">
                      <span
                        aria-label={`Estado de ciclo de vida: ${product.isActive ? "Activo" : "Retirado"}`}
                      >
                        {product.isActive ? (
                          "Activo"
                        ) : (
                          <strong>Retirado</strong>
                        )}
                      </span>
                    </td>
                    <td
                      headers="catalog-column-availability"
                      data-label="Disponibilidad"
                    >
                      <span
                        aria-label={`Estado de disponibilidad: ${product.isAvailable ? "Disponible" : "No disponible"}`}
                      >
                        {product.isActive ? (
                          product.isAvailable ? (
                            "Disponible"
                          ) : (
                            "No disponible"
                          )
                        ) : (
                          <>
                            Disponibilidad conservada:{" "}
                            {product.isAvailable
                              ? "Disponible"
                              : "No disponible"}
                            . No puede agregarse a pedidos mientras esté
                            retirado.
                          </>
                        )}
                      </span>
                    </td>
                    <td headers="catalog-column-group" data-label="Grupo">
                      {product.groupId == null
                        ? "Sin Grupo"
                        : (groups.find((group) => group.id === product.groupId)
                            ?.operationalName ?? "Grupo no disponible")}
                    </td>
                    <td
                      headers="catalog-column-preparation"
                      data-label="Preparación"
                    >
                      {product.requiresPreparation
                        ? `Requiere preparación: ${preparationDestinationLabel(product.preparationResponsibilityId)}`
                        : "No requiere preparación"}
                    </td>
                    <td
                      headers="catalog-column-actions"
                      data-label="Acciones"
                      data-product-name={product.operationalName}
                    >
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
                      {product.isActive && (
                        <button
                          className="secondary-button"
                          type="button"
                          onClick={() => openGroupEditor(product)}
                          disabled={
                            isChangingGroup || uncertainGroupChange !== null
                          }
                          aria-label={`Configurar Grupo de ${product.operationalName}`}
                        >
                          Configurar Grupo
                        </button>
                      )}
                      <button
                        className="secondary-button"
                        type="button"
                        onClick={() => openPreparationEditor(product)}
                        disabled={
                          isChangingPreparation ||
                          uncertainPreparationChange !== null
                        }
                        aria-label={`Configurar preparación de ${product.operationalName}`}
                      >
                        Configurar preparación
                      </button>
                      <button
                        className="secondary-button"
                        type="button"
                        onClick={() => openRenameEditor(product)}
                        disabled={isRenaming || uncertainRename !== null}
                        aria-label={`Renombrar ${product.operationalName}`}
                      >
                        Renombrar
                      </button>
                      {product.isActive ? (
                        <button
                          className="secondary-button"
                          type="button"
                          onClick={() =>
                            void handleLifecycle(product, "retire")
                          }
                          disabled={
                            isChangingLifecycle || uncertainLifecycle !== null
                          }
                          aria-label={`Retirar ${product.operationalName}`}
                        >
                          Retirar
                        </button>
                      ) : (
                        <button
                          className="secondary-button"
                          type="button"
                          onClick={() =>
                            void handleLifecycle(product, "reactivate")
                          }
                          disabled={
                            isChangingLifecycle || uncertainLifecycle !== null
                          }
                          aria-label={`Reactivar ${product.operationalName}`}
                        >
                          Reactivar
                        </button>
                      )}
                      <button
                        type="button"
                        className="danger-button"
                        onClick={() => {
                          setDeleteCandidate(product);
                          focusOpenedEditor("delete");
                        }}
                        disabled={isDeleting || uncertainDelete !== null}
                        aria-label={`Eliminar definitivamente ${product.operationalName}`}
                      >
                        Eliminar definitivamente
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
            id="catalog-price-editor"
            onKeyDown={(event) => {
              if (event.key === "Escape" && !isChangingPrice) {
                event.preventDefault();
                setPriceEditor(null);
                returnToProduct();
              }
            }}
            tabIndex={-1}
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
                {isChangingPrice ? "Cambiando…" : "Guardar precio"}
              </button>
              <button
                className="secondary-button"
                type="button"
                onClick={() => {
                  setPriceEditor(null);
                  returnToProduct();
                }}
                disabled={isChangingPrice}
              >
                Cancelar
              </button>
            </div>
          </form>
        )}

        {preparationEditor && uncertainPreparationChange === null && (
          <form
            className="price-change-form"
            id="catalog-preparation-editor"
            onKeyDown={(event) => {
              if (event.key === "Escape" && !isChangingPreparation) {
                event.preventDefault();
                setPreparationEditor(null);
                returnToProduct();
              }
            }}
            tabIndex={-1}
            onSubmit={(event) => void handlePreparationChange(event)}
            aria-label={`Configurar preparación de ${preparationEditor.operationalName}`}
          >
            <div>
              <span className="field-label">Producto</span>
              <strong>{preparationEditor.operationalName}</strong>
            </div>
            <label>
              <input
                type="checkbox"
                checked={preparationEditor.requiresPreparation}
                onChange={(event) =>
                  setPreparationEditor((current) =>
                    current === null
                      ? null
                      : {
                          ...current,
                          requiresPreparation: event.target.checked,
                        },
                  )
                }
                disabled={isChangingPreparation}
              />{" "}
              Requiere preparación
            </label>
            <label htmlFor="preparation-responsibility-destination">
              Destino de preparación
            </label>
            <select
              id="preparation-responsibility-destination"
              value={
                preparationEditor.selectedPreparationResponsibilityId ?? ""
              }
              onChange={(event) =>
                setPreparationEditor((current) =>
                  current === null
                    ? null
                    : {
                        ...current,
                        selectedPreparationResponsibilityId:
                          event.target.value || null,
                      },
                )
              }
              disabled={
                isChangingPreparation || !preparationEditor.requiresPreparation
              }
            >
              <option value="">Seleccioná una responsabilidad</option>
              {preparationEditor.selectedPreparationResponsibilityId !== null &&
                !preparationResponsibilityOptions.some(
                  (option) =>
                    option.id ===
                    preparationEditor.selectedPreparationResponsibilityId,
                ) && (
                  <option
                    value={
                      preparationEditor.selectedPreparationResponsibilityId
                    }
                  >
                    Destino actual no disponible
                  </option>
                )}
              {preparationResponsibilityOptions.map((option) => (
                <option key={option.id} value={option.id}>
                  {option.operationalName}
                </option>
              ))}
            </select>
            {preparationEditor.requiresPreparation &&
              preparationEditor.selectedPreparationResponsibilityId ===
                null && (
                <p role="alert">
                  Seleccioná un destino de preparación antes de confirmar.
                </p>
              )}
            <div className="intention-actions">
              <button
                type="submit"
                disabled={
                  isChangingPreparation ||
                  (preparationEditor.requiresPreparation &&
                    preparationEditor.selectedPreparationResponsibilityId ===
                      null)
                }
              >
                {isChangingPreparation
                  ? "Actualizando…"
                  : "Confirmar configuración de preparación"}
              </button>
              <button
                className="secondary-button"
                type="button"
                onClick={() => {
                  setPreparationEditor(null);
                  returnToProduct();
                }}
                disabled={isChangingPreparation}
              >
                Cancelar
              </button>
            </div>
          </form>
        )}

        {uncertainGroupChange && (
          <div
            className="uncertain-intention"
            role="region"
            aria-label="Cambio de Grupo con resultado no confirmado"
          >
            <p>
              El cambio de Grupo de {uncertainGroupChange.operationalName} no
              fue confirmado.
            </p>
            <p>
              El reintento conserva el estado observado y el destino, sin
              duplicar el cambio.
            </p>
            <div className="intention-actions">
              <button
                type="button"
                onClick={() => void submitGroupChange(uncertainGroupChange)}
                disabled={isChangingGroup}
              >
                Reintentar mismo cambio de Grupo
              </button>
              <button
                className="secondary-button"
                type="button"
                onClick={() => {
                  setUncertainGroupChange(null);
                  setGroupEditor(null);
                }}
              >
                Dejar de reintentar
              </button>
              <p>
                Esto no deshace la operación. Su resultado sigue sin
                confirmarse.
              </p>
            </div>
          </div>
        )}

        {groupEditor && uncertainGroupChange === null && (
          <form
            className="price-change-form"
            id="catalog-group-editor"
            onKeyDown={(event) => {
              if (event.key === "Escape" && !isChangingGroup) {
                event.preventDefault();
                setGroupEditor(null);
                returnToProduct();
              }
            }}
            tabIndex={-1}
            onSubmit={(event) => void handleGroupChange(event)}
            aria-label={`Configurar Grupo de ${groupEditor.operationalName}`}
          >
            <div>
              <span className="field-label">Producto</span>
              <strong>{groupEditor.operationalName}</strong>
            </div>
            <label htmlFor="product-group-selection">Grupo del Producto</label>
            <select
              id="product-group-selection"
              aria-label={`Grupo del Producto de ${groupEditor.operationalName}`}
              value={groupEditor.selectedGroupId ?? ""}
              onChange={(event) =>
                setGroupEditor((current) =>
                  current === null
                    ? null
                    : {
                        ...current,
                        selectedGroupId: event.target.value || null,
                      },
                )
              }
              disabled={isChangingGroup}
            >
              <option value="">Sin Grupo</option>
              {groups.map((group) => (
                <option key={group.id} value={group.id}>
                  {group.operationalName}
                </option>
              ))}
            </select>
            <div className="intention-actions">
              <button
                type="submit"
                disabled={
                  isChangingGroup ||
                  groupEditor.selectedGroupId === groupEditor.observedGroupId
                }
              >
                {isChangingGroup ? "Actualizando…" : "Confirmar Grupo"}
              </button>
              <button
                className="secondary-button"
                type="button"
                onClick={() => {
                  setGroupEditor(null);
                  returnToProduct();
                }}
                disabled={isChangingGroup}
              >
                Cancelar
              </button>
            </div>
          </form>
        )}

        {uncertainRename && (
          <div
            className="uncertain-intention"
            role="region"
            aria-label="Renombre con resultado no confirmado"
          >
            <p>El renombre del Producto no fue confirmado.</p>
            <p>
              El reintento conserva el nombre observado y el nuevo nombre, sin
              duplicar el cambio.
            </p>
            <div className="intention-actions">
              <button
                type="button"
                onClick={() => void submitRename(uncertainRename)}
                disabled={isRenaming}
              >
                Reintentar mismo renombre
              </button>
              <button
                className="secondary-button"
                type="button"
                onClick={() => {
                  setUncertainRename(null);
                  setRenameEditor(null);
                }}
              >
                Dejar de reintentar
              </button>
              <p>
                Esto no deshace la operación. Su resultado sigue sin
                confirmarse.
              </p>
            </div>
          </div>
        )}

        {renameEditor && uncertainRename === null && (
          <form
            className="price-change-form"
            id="catalog-rename-editor"
            onKeyDown={(event) => {
              if (event.key === "Escape" && !isRenaming) {
                event.preventDefault();
                setRenameEditor(null);
                returnToProduct();
              }
            }}
            tabIndex={-1}
            onSubmit={(event) => void handleRename(event)}
            aria-label={`Renombrar ${renameEditor.observedName}`}
          >
            <div>
              <span className="field-label">Nombre observado</span>
              <strong>{renameEditor.observedName}</strong>
            </div>
            <label htmlFor="new-product-operational-name">Nuevo nombre</label>
            <input
              id="new-product-operational-name"
              value={renameEditor.newName}
              onChange={(event) =>
                setRenameEditor((current) =>
                  current === null
                    ? null
                    : { ...current, newName: event.target.value },
                )
              }
              disabled={isRenaming}
              required
            />
            <div className="intention-actions">
              <button
                type="submit"
                disabled={
                  isRenaming ||
                  renameEditor.newName.trim() === renameEditor.observedName
                }
              >
                {isRenaming ? "Renombrando…" : "Confirmar renombre"}
              </button>
              <button
                className="secondary-button"
                type="button"
                onClick={() => {
                  setRenameEditor(null);
                  returnToProduct();
                }}
                disabled={isRenaming}
              >
                Cancelar
              </button>
            </div>
          </form>
        )}

        {uncertainLifecycle && (
          <div
            className="uncertain-intention"
            role="region"
            aria-label="Cambio de ciclo de vida con resultado no confirmado"
          >
            <p>
              El{" "}
              {uncertainLifecycle.action === "retire"
                ? "retiro"
                : "reactivación"}{" "}
              de {uncertainLifecycle.operationalName} no fue confirmado.
            </p>
            <p>
              El reintento conserva la misma acción sin duplicar el cambio. No
              se hará automáticamente.
            </p>
            <div className="intention-actions">
              <button
                type="button"
                onClick={() => void submitLifecycle(uncertainLifecycle)}
                disabled={isChangingLifecycle}
              >
                Reintentar misma acción
              </button>
              <button
                className="secondary-button"
                type="button"
                onClick={() => setUncertainLifecycle(null)}
                disabled={isChangingLifecycle}
              >
                Dejar de reintentar
              </button>
              <p>
                Esto no deshace la operación. Su resultado sigue sin
                confirmarse.
              </p>
            </div>
          </div>
        )}
        {deleteCandidate && uncertainDelete === null && (
          <SensitiveActionDialog
            objectName={deleteCandidate.operationalName}
            consequence="Se quitará su configuración del catálogo. Sólo puede eliminarse si nunca participó en un pedido confirmado."
            busy={isDeleting}
            fallbackFocusId="products-title"
            feedback={
              deleteNotice && (
                <p
                  role={
                    deleteNotice.kind === "functional-error"
                      ? "alert"
                      : "status"
                  }
                >
                  {deleteNotice.message}
                </p>
              )
            }
            onConfirm={() => void handleDelete(deleteCandidate)}
            onClose={() => {
              setDeleteCandidate(null);
              returnToProduct();
            }}
          />
        )}
        {uncertainDelete && (
          <div
            className="uncertain-intention"
            role="region"
            aria-label="Eliminación con resultado no confirmado"
          >
            <p>
              La eliminación de {uncertainDelete.operationalName} tiene
              resultado incierto. Podés reintentar esta eliminación sin
              duplicarla.
            </p>
            <button
              type="button"
              disabled={isDeleting}
              onClick={() => void submitDelete(uncertainDelete)}
            >
              Reintentar misma eliminación
            </button>
            <button
              type="button"
              className="secondary-button"
              disabled={isDeleting}
              onClick={() => {
                setUncertainDelete(null);
                setDeleteCandidate(null);
              }}
            >
              Dejar de reintentar
            </button>
            <p>
              Esto no deshace la operación. Su resultado sigue sin confirmarse.
            </p>
          </div>
        )}
      </section>
      {showSetup && products.length > 0 && setupSections}
    </>
  );
}
