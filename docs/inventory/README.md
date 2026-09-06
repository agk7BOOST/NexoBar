# Inventory operativo

## Inventory mínimo operativo

`Inventory` posee `InventoryDbContext`, el schema `inventory` y migration history propia sobre la PostgreSQL primaria compartida. `InventoryItem` es Estado propio del módulo y contiene `Id` UUID v7, nombre operacional con unicidad case-insensitive, unidad operacional, `CurrentRegisteredQuantity` nullable y `MovementRevision` monotónica. Las cantidades autoritativas se representan como decimal string en HTTP y `numeric(28,12)` en PostgreSQL.

Un Item recién creado tiene cantidad no inicializada: `CurrentRegisteredQuantity = null` y `MovementRevision = 0`. `null` significa que todavía no se estableció una existencia; no equivale a cero. La creación requiere `InventoryConfiguration`, antiforgery e `Idempotency-Key` UUID v4. Las lecturas también separan capacidades honestamente: configuración requiere `InventoryConfiguration`, mientras Estado operacional, Conteo, Reconciliación, Movimientos e Historia requieren `InventoryOperation`. Una Identity con una sola responsabilidad no obtiene implícitamente la otra.

Los contratos materializados son:

```text
POST /api/inventory/items
GET  /api/inventory/configuration/items
GET  /api/inventory/operations/items
POST /api/inventory/items/{itemId}/counts
POST /api/inventory/items/{itemId}/reconcile
POST /api/inventory/items/{itemId}/entries
POST /api/inventory/items/{itemId}/manual-exits
POST /api/inventory/items/{itemId}/waste
GET  /api/inventory/items/{itemId}/movements
```

### Conteo, Reconciliación y decisiones aplicadas

`CountObservation` registra el hecho observado sin modificar el saldo. Conserva Item, cantidad física no negativa, unidad operacional, `ObservedMovementRevision`, actor y timestamp UTC. La Reconciliación consume una observación del mismo Item y la invalida si la unidad cambió o si `MovementRevision` ya no coincide: cualquier Reconciliation con cambio, Entry, ManualExit o Waste intermedia avanza la revisión y hace obsoleto el Conteo.

- `AD-INV-01` está aplicada: la Reconciliación inicial establece la cantidad desde `null`, crea un Movimiento, conserva `PreviousRegisteredQuantity = null` y no inventa una diferencia contra cero.
- `AD-INV-02` está aplicada: una Reconciliación ordinaria deriva la diferencia contra el saldo registrado; si no hay discrepancia devuelve `no_discrepancy`, no crea Movimiento y no incrementa `MovementRevision`.

La Reconciliación que sí cambia Estado incrementa `MovementRevision` y confirma atómicamente Item, `InventoryMovement` y comando durable. El lock `FOR UPDATE` del Item serializa Reconciliaciones y Movimientos del mismo Item; Conteo usa `FOR SHARE` para capturar coherentemente revisión y unidad. Los locks de idempotencia, la autorización estabilizada y la transacción `READ COMMITTED` preservan replay, concurrencia same-Item y rollback total ante una falla de persistencia.

### Movimientos físicos, Historia e idempotencia

`Entry` suma una cantidad positiva. `ManualExit` y `Waste` restan una cantidad positiva y pueden atravesar cero: el saldo negativo se conserva como inconsistencia operacional visible, no se recorta ni se rechaza. Los tres requieren que la cantidad ya esté establecida, incrementan la revisión exactamente una vez y crean `InventoryMovement` con naturaleza, cantidad, saldo previo y resultante, actor y timestamp. `Correction` figura en la forma persistente reservada, pero no tiene comando, API ni comportamiento implementado.

Create Item, Count, Reconciliation, Entry, ManualExit y Waste mantienen idempotencia durable local. La misma key UUID v4 con el mismo actor e intención reproduce el resultado original sin duplicar Estado ni Historia; una reutilización incompatible responde conflicto. Un replay confirmado todavía requiere Session utilizable e Identity activa, pero no reinterpreta el efecto por una revocación posterior de la capability.

La Historia autorizada de Movimientos se consulta en orden descendente por `MovementRevision`, con cursor exclusivo `beforeRevision`, página por defecto de 50 y límite entre 1 y 100. Incluye el efecto con signo, saldos previo/resultante y detalle de Reconciliación. Persiste `ActorIdentityId` y resuelve al leer el nombre operacional vigente mediante la capacidad pública de Identities; no guarda `SessionId` ni un snapshot de nombre. El Estado vigente no se reconstruye ordinariamente desde esta Historia.

No existe integración automática con `Catalog`, Product, ventas u `OrderOperations`: `Product != InventoryItem`. Order, Confirmation, Preparation y Delivery no crean Movimientos de Inventory automáticamente.

## Migraciones

Las migraciones vigentes de Inventory en Slice 5 son:

- `InitialInventory`, que crea Items y comandos durables de creación;
- `AddInventoryCountReconciliation`, que agrega CountObservation, Reconciliation, Historia y comandos durables;
- `AddEverydayInventoryMovements`, que materializa Entry, ManualExit y Waste sobre el mismo Estado, Historia y namespace durable de Movimientos.

## Pendientes

- Movement Correction de Inventory; la forma final de su relación con Movimientos previos no está decidida aquí;
- `retire/reactivate/delete` de InventoryItem;
- Unit Correction de InventoryItem y sus reglas antes/después de existir Historia;
- actualización activa de Inventory mediante SSE;
- persistencia cross-reload de intents inciertos de Inventory;
- política final de reutilización de nombres antes de materializar lifecycle de InventoryItem;

Tampoco se decide aquí que un Item retirado guarde cantidad `null`, la semántica final de reutilización de nombre ni la forma relacional de Correction.
