import type { FormEvent } from "react";
import type { PreparationResponsibilityOption } from "./catalogClient.ts";

export interface PreparationEditor {
  productId: string;
  operationalName: string;
  observedPreparationResponsibilityId: string | null;
  requiresPreparation: boolean;
  selectedPreparationResponsibilityId: string | null;
}

interface CatalogPreparationEditorProps {
  preparationEditor: PreparationEditor;
  preparationResponsibilityOptions: PreparationResponsibilityOption[];
  isChangingPreparation: boolean;
  onRequiresPreparationChange: (requiresPreparation: boolean) => void;
  onDestinationChange: (destinationId: string | null) => void;
  onSubmit: (event: FormEvent<HTMLFormElement>) => void;
  onCancel: () => void;
}

export function CatalogPreparationEditor({
  preparationEditor,
  preparationResponsibilityOptions,
  isChangingPreparation,
  onRequiresPreparationChange,
  onDestinationChange,
  onSubmit,
  onCancel,
}: CatalogPreparationEditorProps) {
  return (
    <form
      className="price-change-form"
      id="catalog-preparation-editor"
      onKeyDown={(event) => {
        if (event.key === "Escape" && !isChangingPreparation) {
          event.preventDefault();
          onCancel();
        }
      }}
      tabIndex={-1}
      onSubmit={onSubmit}
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
            onRequiresPreparationChange(event.target.checked)
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
        value={preparationEditor.selectedPreparationResponsibilityId ?? ""}
        onChange={(event) => onDestinationChange(event.target.value || null)}
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
              value={preparationEditor.selectedPreparationResponsibilityId}
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
        preparationEditor.selectedPreparationResponsibilityId === null && (
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
              preparationEditor.selectedPreparationResponsibilityId === null)
          }
        >
          {isChangingPreparation
            ? "Actualizando…"
            : "Confirmar configuración de preparación"}
        </button>
        <button
          className="secondary-button"
          type="button"
          onClick={onCancel}
          disabled={isChangingPreparation}
        >
          Cancelar
        </button>
      </div>
    </form>
  );
}
