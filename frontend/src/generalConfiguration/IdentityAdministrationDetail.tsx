import type { Ref } from "react";
import {
  FUNCTIONAL_RESPONSIBILITIES,
  type AdministrativeIdentity,
  type FunctionalResponsibility,
  type PreparationResponsibility,
} from "./identityAdministrationClient.ts";
import { responsibilityLabels } from "./responsibilityLabels.ts";

interface IdentityAdministrationDetailProps {
  selectedIdentity: AdministrativeIdentity;
  detailRef: Ref<HTMLElement>;
  preparationResponsibilities: PreparationResponsibility[];
  isPreparationResponsibilitiesLoading: boolean;
  identityEditing: boolean;
  identityMutationBlocked: boolean;
  enablementMutationBlocked: boolean;
  credentialEditingBlocked: boolean;
  renamingBlocked: boolean;
  onReturnToList: () => void;
  onResponsibilityChange: (
    kind: "assign" | "revoke",
    responsibility: FunctionalResponsibility,
  ) => void;
  onEnablementChange: (
    kind: "grant" | "revoke",
    responsibility: PreparationResponsibility,
  ) => void;
  onToggleActivation: () => void;
  onEditCredential: () => void;
  onRename: () => void;
  onDelete: () => void;
}

export function IdentityAdministrationDetail({
  selectedIdentity,
  detailRef,
  preparationResponsibilities,
  isPreparationResponsibilitiesLoading,
  identityEditing,
  identityMutationBlocked,
  enablementMutationBlocked,
  credentialEditingBlocked,
  renamingBlocked,
  onReturnToList,
  onResponsibilityChange,
  onEnablementChange,
  onToggleActivation,
  onEditCredential,
  onRename,
  onDelete,
}: IdentityAdministrationDetailProps) {
  return (
    <section
      ref={detailRef}
      className="identity-detail"
      tabIndex={-1}
      aria-label={"Administrar identidad " + selectedIdentity.operationalName}
    >
      <div className="section-heading">
        <div>
          <h3>{selectedIdentity.operationalName}</h3>
          <p>
            <span>{selectedIdentity.isActive ? "Activa" : "Inactiva"}</span>
            {" · Acceso "}
            <span>
              {selectedIdentity.hasLocalCredential
                ? "configurado"
                : "sin configurar"}
            </span>
          </p>
          {selectedIdentity.loginIdentifier && (
            <p>Usuario de acceso: {selectedIdentity.loginIdentifier}</p>
          )}
        </div>
        <button
          type="button"
          className="secondary-button"
          disabled={identityEditing}
          onClick={onReturnToList}
        >
          Volver al listado
        </button>
      </div>

      <section className="identity-detail-group">
        <h4>Responsabilidades</h4>
        <ul
          className="identity-detail-actions"
          aria-label={
            "Responsabilidades de " + selectedIdentity.operationalName
          }
        >
          {FUNCTIONAL_RESPONSIBILITIES.map((responsibility) => {
            const isAssigned =
              selectedIdentity.responsibilities.includes(responsibility);
            return (
              <li key={responsibility}>
                <span>
                  {`${responsibilityLabels[responsibility]}: ${isAssigned ? "Asignada" : "No asignada"}`}
                </span>
                <button
                  className="secondary-button"
                  type="button"
                  onClick={() =>
                    onResponsibilityChange(
                      isAssigned ? "revoke" : "assign",
                      responsibility,
                    )
                  }
                  disabled={identityMutationBlocked}
                  aria-label={
                    (isAssigned ? "Quitar " : "Asignar ") +
                    responsibilityLabels[responsibility] +
                    " a " +
                    selectedIdentity.operationalName
                  }
                >
                  {isAssigned ? "Quitar" : "Asignar"}
                </button>
              </li>
            );
          })}
        </ul>
      </section>

      <section className="identity-detail-group">
        <h4>Destinos habilitados</h4>
        <p>
          Un destino de preparación es un lugar de trabajo, no un cargo ni una
          persona. Esta identidad puede atender únicamente los destinos
          habilitados cuando también tiene Preparación asignada.
        </p>
        {!selectedIdentity.responsibilities.includes("Preparation") && (
          <p>Asigná Preparación para habilitar destinos a esta identidad.</p>
        )}
        <ul
          className="identity-detail-actions"
          aria-label={
            "Destinos habilitados de " + selectedIdentity.operationalName
          }
        >
          {preparationResponsibilities
            .filter(
              (responsibility) =>
                selectedIdentity.responsibilities.includes("Preparation") ||
                selectedIdentity.preparationEnablements.includes(
                  responsibility.id,
                ),
            )
            .map((responsibility) => {
              const isEnabled =
                selectedIdentity.preparationEnablements.includes(
                  responsibility.id,
                );
              return (
                <li key={responsibility.id}>
                  <span>
                    {`${responsibility.operationalName}: ${isEnabled ? "Habilitada" : "No habilitada"}`}
                  </span>
                  {!selectedIdentity.responsibilities.includes(
                    "Preparation",
                  ) && <small>No utilizable sin Preparación</small>}
                  <button
                    className="secondary-button"
                    type="button"
                    onClick={() =>
                      onEnablementChange(
                        isEnabled ? "revoke" : "grant",
                        responsibility,
                      )
                    }
                    disabled={enablementMutationBlocked}
                    aria-label={
                      (isEnabled
                        ? "Quitar habilitación "
                        : "Habilitar destino ") +
                      responsibility.operationalName +
                      " a " +
                      selectedIdentity.operationalName
                    }
                  >
                    {isEnabled ? "Quitar habilitación" : "Habilitar destino"}
                  </button>
                </li>
              );
            })}
          {!isPreparationResponsibilitiesLoading &&
            selectedIdentity.preparationEnablements
              .filter(
                (id) =>
                  !preparationResponsibilities.some(
                    (responsibility) => responsibility.id === id,
                  ),
              )
              .map((id) => (
                <li key={id}>
                  <span>Destino de preparación desconocido</span>
                  <details>
                    <summary>Identificador técnico</summary>
                    <span className="technical-reference">{id}</span>
                  </details>
                </li>
              ))}
        </ul>
      </section>

      <section className="identity-detail-group identity-sensitive-actions">
        <h4>Acceso y acciones sensibles</h4>
        <div className="identity-action-buttons">
          <button
            className="secondary-button"
            type="button"
            onClick={onToggleActivation}
            disabled={identityMutationBlocked}
            aria-label={
              (selectedIdentity.isActive ? "Desactivar " : "Activar ") +
              selectedIdentity.operationalName
            }
          >
            {selectedIdentity.isActive ? "Desactivar" : "Activar"}
          </button>
          <button
            className="secondary-button"
            type="button"
            onClick={onEditCredential}
            disabled={credentialEditingBlocked}
            aria-label={
              (selectedIdentity.hasLocalCredential
                ? "Cambiar acceso de "
                : "Configurar acceso de ") + selectedIdentity.operationalName
            }
          >
            {selectedIdentity.hasLocalCredential
              ? "Cambiar acceso"
              : "Configurar acceso"}
          </button>
          <button
            className="secondary-button"
            type="button"
            onClick={onRename}
            disabled={renamingBlocked}
            aria-label={"Cambiar nombre de " + selectedIdentity.operationalName}
          >
            Cambiar nombre
          </button>
          <button
            type="button"
            className="danger-button"
            onClick={onDelete}
            disabled={identityMutationBlocked}
            aria-label={
              "Eliminar definitivamente " + selectedIdentity.operationalName
            }
          >
            Eliminar definitivamente
          </button>
        </div>
      </section>
    </section>
  );
}
