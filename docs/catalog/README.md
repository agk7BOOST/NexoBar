# Catalog

Producto no significa Elemento de Inventario. Para cambiar la capacidad consumida por Confirmation, leer [colaboración transaccional](confirmation-collaboration.md).

## Reads y fronteras de S9

Catalog distingue administración de exploración operacional. La administración de Products requiere `CatalogConfiguration`. La exploración para Composición/Confirmation requiere `OrderOperationsAndBasicClosure`; no implica `CatalogConfiguration` ni existe una responsabilidad `CatalogRead`.

La proyección operacional contiene sólo Product id, nombre operacional, precio actual, disponibilidad y vigencia actual. `requiresPreparation` puede incluirse sólo cuando el frontend realmente lo necesite. No expone internals de Preparation Responsibility, datos de comandos administrativos, Historia ni lifecycle futuro.

La visibilidad de la excepción de Product temporalmente no disponible exige conjuntamente `OrderOperationsAndBasicClosure` y `OperationalIntervention`. RF-PED-024/RF-PED-025 ya establecen la incorporación excepcional explícita, `OperationalIntervention` para el override, que no cambia la disponibilidad general del Product y que no exige motivo de texto libre. La Confirmation materializada todavía rechaza incondicionalmente `!IsAvailable`; la excepción aprobada sigue siendo un gap de implementación MVP. Este documento no diseña su comando.

Los reads internos usan colaboraciones explícitas: OrderOperations consume la capacidad de Catalog para necesidades de Product/Confirmation y Catalog consume el lookup estrecho de OperationalConfiguration al configurar Preparation Responsibility. Ningún módulo accede directamente al `DbContext`, schema o tablas de otro; el Host coordina autenticación, no posee storage de negocio.

## Catalog y configuración de preparación

El Estado vigente de `Product` incluye `requiresPreparation` y `preparationResponsibilityId` nullable, con la invariante física y de aplicación:

```text
RequiresPreparation = false ↔ PreparationResponsibilityId = null
RequiresPreparation = true  ↔ PreparationResponsibilityId = UUID
```

`PreparationResponsibilityId` es una referencia externa y lógica; no existe FK física desde `Catalog` hacia `OperationalConfiguration`.

La mutación materializada es el comando explícito:

```text
POST /api/catalog/products/{productId}/preparation-configuration-changes
```

Su semántica es:

- `null → UUID`: habilitar preparación;
- `UUID A → UUID B`: reasignar prospectivamente;
- `UUID → null`: deshabilitar prospectivamente;
- `null → null`: no-op válido cuando el valor esperado coincide.

El comando recibe `expectedCurrentPreparationResponsibilityId` y realiza un `UPDATE` condicionado atómico con comparación nullable; no aplica last-write-wins. Mantiene idempotencia durable local y resuelve el replay antes de consultar `OperationalConfiguration`. Para una intención nueva cuyo destino no es `null`, `Catalog` usa la capacidad pública estrecha de existencia de `PreparationResponsibility`.

Price Change continúa siendo otro comando explícito de `Catalog`, no un `PATCH` genérico: recibe `expectedCurrentPrice`, ejecuta un `UPDATE` condicionado, responde `409 Conflict` ante una expectativa desactualizada, mantiene idempotencia durable local y no crea Price History ni reescribe `appliedPrice` históricos.

Los endpoints materializados de Catalog ya no son anónimos ni comparten una frontera única. La administración de Catalog requiere `CatalogConfiguration`. La exploración operacional de Products requiere `OrderOperationsAndBasicClosure`; los Products no disponibles sólo se incluyen cuando también está presente `OperationalIntervention`. El override de incorporación de Product no disponible de RF-PED-024/RF-PED-025 sigue siendo trabajo funcional pendiente: la Confirmation materializada todavía rechaza `!IsAvailable`.

El lookup de Preparation Responsibility para configurar un Product es un endpoint estrecho propiedad de Catalog y exige `CatalogConfiguration`; Catalog resuelve la referencia mediante su colaboración interna explícita con `OperationalConfiguration`. No se convierte el listado administrativo de `OperationalConfiguration` en una capacidad de Catalog ni se permite acceso directo entre storages.
