# Inventory operativo

## Estado y autoridad

`Inventory` posee `InventoryDbContext`, el schema `inventory` y migration history propia sobre PostgreSQL. `InventoryItem` es Estado del módulo; su ID es UUID v7, las cantidades autoritativas son decimal string en HTTP y `numeric(28,12)` en PostgreSQL, y `MovementRevision` es monotónica. `CurrentRegisteredQuantity` es nullable: `null` significa existencia no establecida, no cero.

Inventory separa `InventoryConfiguration` de `InventoryOperation`. Configuration administra Elements; Operation registra hechos físicos, Movements e History. Una Identity puede tener ambas responsabilidades, pero ninguna implica la otra.

Contratos HTTP actuales:

```text
POST /api/inventory/items
GET  /api/inventory/configuration/items
GET  /api/inventory/operations/items
POST /api/inventory/items/{itemId}/retire
POST /api/inventory/items/{itemId}/reactivate
POST /api/inventory/items/{itemId}/unit-corrections
POST /api/inventory/items/{itemId}/delete
POST /api/inventory/items/{itemId}/counts
POST /api/inventory/items/{itemId}/reconcile
POST /api/inventory/items/{itemId}/entries
POST /api/inventory/items/{itemId}/manual-exits
POST /api/inventory/items/{itemId}/waste
GET  /api/inventory/items/{itemId}/movements
```

## MVP-FC-INV-LU — Lifecycle, Unit Correction y eligible Delete: CLOSED

### Modelo de lifecycle y readiness

Se persisten `InventoryItem.IsActive` y `CurrentRegisteredQuantity`; no existe un enum persistido de lifecycle/readiness. La readiness ordinaria se deriva:

| `IsActive` | `CurrentRegisteredQuantity` | Estado técnico/operacional |
| --- | --- | --- |
| `false` | `null` | Retirado; no hay existencia física actual establecida. |
| `true` | `null` | Activo, pero requiere Count/Reconciliation; Movement ordinario no permitido. |
| `true` | no `null` | Activo y listo para la operación ordinaria. |

La persistencia impone cantidad null para un Element retirado. Los Movements Entry, ManualExit y Waste requieren `IsActive = true` y `CurrentRegisteredQuantity != null`. Los errores estables distinguen retirado (`inventory.item.retired`), activo sin existencia establecida (`inventory.item.reconciliation_required`) e inexistente (`inventory.item.not_found`). Count es válido en Elements activos con o sin existencia establecida; no se aceptan nuevos Counts para un Element retirado.

### Retire, Reactivate y nombres

Retire requiere `InventoryConfiguration`. Conserva el ID, nombre, Unit, Movement History y `MovementRevision`; establece `IsActive = false`, limpia `CurrentRegisteredQuantity` e invalida observaciones Count pendientes. No crea `InventoryMovement` ni fuerza el saldo a cero. Los saldos anteriores siguen siendo históricamente interpretables desde los Movements, pero dejan de ser conocimiento físico actual.

Reactivate también requiere `InventoryConfiguration` y conserva ID, Unit e History. Establece `IsActive = true`, deja `CurrentRegisteredQuantity = null` y no habilita inmediatamente Movements ordinarios. `InventoryOperation` debe ejecutar un Count físico nuevo seguido de Reconciliation. La primera Reconciliation usa `PreviousRegisteredQuantity = null`, no inventa cero, establece la cantidad observada y crea el Movement normal de Reconciliation; puede establecer cero. Aunque la observación coincida con el último saldo histórico anterior a Retire, establece existencia desde ausencia de existencia actual.

El nombre operacional normalizado es único entre Elements activos; los nombres de Elements retirados se pueden reutilizar. Reactivate conserva el nombre si está disponible y puede recibir un nombre operacional de reemplazo si otro Element activo ya lo usa. Si el nombre está ocupado y no se provee un reemplazo disponible, Reactivate entra en conflicto. Esto es una opción de Reactivate; no existe una capacidad general `RenameElement`.

### CountObservations y Unit Correction

CountObservation registra una observación no negativa, Unit observada, `ObservedMovementRevision`, actor y timestamp UTC, sin alterar el saldo. Las observaciones pendientes se invalidan cuando cambia la Unit o cuando se retira el Element. Una CountObservation invalidada permanece como observación registrada mientras se conserve su fila, no se puede usar en Reconciliation y devuelve `inventory.reconciliation.observation_invalidated`. El replay durable del comando Count es independiente de poder consumir su observación. La invalidación no se revierte: `U1 → Count → U2 → U1` no revive el Count anterior y requiere nueva verificación física.

La corrección explícita de Unit requiere `InventoryConfiguration`, `expectedCurrentUnit` y `newUnit`. Se permite sólo mientras no exista ningún `InventoryMovement` para el Element, tanto activo como retirado. No convierte cantidad, no crea Movement ni incrementa `MovementRevision`; invalida Counts pendientes. Solicitar la Unit vigente puede guardarse como no-op durable. Una Unit esperada obsoleta produce conflicto.

Tras el primer `InventoryMovement`, la Unit de ese Element no puede cambiarse. El comando devuelve `inventory.item.unit_correction_requires_replacement`. El camino MVP es retirar el Element anterior, crear uno nuevo con la Unit deseada y establecer su existencia mediante Count/Reconciliation. El reemplazo tiene otro ID, Unit propia y existencia/History independientes; no se copia saldo. Así todos los Movements de un Element mantienen un único significado de Unit: no se migraron cantidades históricas ni se añadieron snapshots, eras o versiones de Unit, conversiones o relaciones automáticas de sucesión.

### Read models

`GET /api/inventory/configuration/items` devuelve todos los Elements no eliminados: activos listos, activos que requieren Reconciliation y retirados. Expone lifecycle, readiness derivada, Unit, elegibilidad para corregir Unit y elegibilidad para Delete. El read incluye retirados; la implementación anterior filtraba `IsActive = true` y los ocultaba tras recarga. La regresión se corrigió: Retire → reload autoritativo → el mismo ID sigue visible como Retirado → Reactivate usa esa identidad estable.

`GET /api/inventory/operations/items` devuelve sólo Elements activos, listos o pendientes de establecimiento. Retirados quedan excluidos; activos pendientes siguen visibles para Count/Reconciliation. Elements eliminados no aparecen en ninguno de los reads.

### Definitive Delete

Un Element puede eliminarse físicamente si y sólo si no existe ningún `InventoryMovement` para él. No bloquean por sí solos Delete: estado activo o retirado, existencia sin establecer, Counts usados o invalidados, comandos/idempotency rows, ni caminos `no_discrepancy` que no crearon Movement. La existencia de cualquier `InventoryMovement` es el blocker funcional autoritativo.

`POST /api/inventory/items/{itemId}/delete` requiere `InventoryConfiguration`; aplica a Elements activos, retirados o pendientes de establecimiento. En éxito elimina físicamente `InventoryItem` y sus `CountObservation` de forma atómica, libera el nombre y retira el Element de los reads de Configuration y Operation. No hay soft delete. Si existe cualquier Movement, responde `inventory.item.delete_movement_history_conflict` y conserva Element e History. Retire puede ser la alternativa para dejar de operar un Element con History, pero Delete no se transforma automáticamente en Retire.

El command ledger durable de Delete sobrevive al Element eliminado. Tras estabilizar Session e Identity, un replay exacto con la misma key, actor e intención se busca antes de reautorizar `InventoryConfiguration` y devuelve el resultado durable, incluso tras revocar esa responsabilidad si Session e Identity siguen válidas. Otro actor o distinto item con la misma key produce conflicto; una key nueva para el ID eliminado devuelve `inventory.item.not_found`. Los command ledgers conservan el ID como dato escalar, sin FK restrictiva a las filas vivas que deben poder borrarse. La relación funcional `InventoryMovement → InventoryItem` sí se preserva. Los ledgers son soporte técnico de replay, no History de dominio.

Delete y Movement se serializan mediante `FOR UPDATE` sobre la fila del Element, sin lock global de Inventory. Si Delete obtiene el lock primero, confirma la eliminación y el Movement posterior ve Element ausente; no se confirma Movement. Si Movement bloquea y confirma primero, Delete encuentra History, entra en conflicto y Element y Movement permanecen.

### Movements, History e idempotencia

`CountObservation` no modifica el saldo. La Reconciliation inicial desde `null` crea un Movement, conserva `PreviousRegisteredQuantity = null` y no inventa diferencia contra cero (**AD-INV-01**). La Reconciliation ordinaria deriva diferencia contra saldo actual; sin discrepancia responde `no_discrepancy`, no crea Movement ni avanza `MovementRevision` (**AD-INV-02**).

Entry suma una cantidad positiva. ManualExit y Waste restan una cantidad positiva y pueden atravesar cero; el saldo negativo se conserva y se muestra como inconsistencia. Cuando cambia Estado, el comando confirma atómicamente Element, Movement y resultado durable. La History paginada va en orden descendente de `MovementRevision` e incluye efecto con signo, saldos, datos de Reconciliation, actor y timestamp. El Estado actual no se reconstruye ordinariamente desde History. `Correction` no cuenta aquí con comando/API/comportamiento implementado.

Los comandos usan `Idempotency-Key` UUID v4 y replay durable. Igual key e intención exacta reproduce el resultado; una intención incompatible entra en conflicto. Los detalles transversales siguen en [HTTP e idempotencia](../architecture/http-and-idempotency.md).

No existe integración automática con Catalog, Product, ventas u OrderOperations: `Product != InventoryItem`; Order, Confirmation, Preparation y Delivery no crean Inventory Movements automáticamente.

## Frescura operacional

Inventory usa sólo `inventory.operation` para invalidar el listado autoritativo `GET /api/inventory/operations/items`. Sus commits invalidan el read cuando cambian Elements visibles o sus datos: creación visible, Retire, Reactivate, corrección efectiva de Unit, Delete, Entry, ManualExit, Waste y Reconciliation que crea Movement. Conteo, Reconciliation sin discrepancia, replay exacto, no-op, rechazo, conflicto y rollback no publican. La invalidación es best-effort y no es Estado de dominio; luego el cliente vuelve a leer desde autoridad.

La misma señal se consume con `FreshnessReadCoordinator` sólo mientras la superficie Inventory Operation autorizada está montada. Configuration no tiene `inventory.configuration` SSE: después de sus mutaciones locales usa reload autoritativo y maneja conflictos por expected-current; no se promete push freshness de Configuration entre operadores. No hay scope de History ni de Element individual. Detalle de transporte en [SSE y frescura multiusuario](../architecture/sse-and-freshness.md#sse-10--vertical-implementado-frescura-operacional-de-inventory).

## Migraciones

- `InitialInventory`: Elements y comandos durables de creación.
- `AddInventoryCountReconciliation`: CountObservation, Reconciliation, History y ledgers de comando.
- `AddEverydayInventoryMovements`: Entry, ManualExit y Waste sobre el mismo Estado e History.
- `20260922150000_AddInventoryLifecycleAndUnitCorrection`: `IsActive`, invariante activo/existencia, unicidad de nombre activo, invalidación de CountObservation y persistencia durable para Retire, Reactivate y Unit Correction.
- `20260922170000_AddInventoryElementDelete`: ledger durable de Delete, ajustes de FK de ledgers técnicos para permitir Delete físico, limpieza de Counts y cambios de snapshot.

Las dos migraciones nuevas pasaron Up y Down; `HasPendingModelChanges = false`. No materializan Unit versionada ni relación de sucesor.

## Pendientes

- Inventory Movement Correction; su relación con Movements previos sigue sin decisión aplicable.
- Continuidad cross-reload de intents inciertos; siguen en memoria en el frontend.
