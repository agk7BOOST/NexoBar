# Checkpoints históricos de slices

Registro de hitos y alcance declarado en el handoff anterior, conservado para trazabilidad. No es una fuente de decisiones técnicas actuales. El [índice vigente](../engineering-handoff.md) conduce al detalle y pendientes por tema.

## Estado de Slice 3

El checkpoint de Identity, autenticación y capabilities requerido antes del progreso humano de Preparation ya está materializado:

- `a6e6f1a feat: add identities and capabilities state`;
- `74b941c feat: add opaque identity sessions`;
- `66d0ab3 feat: add identity administration`;
- `f82e0c7 feat: secure preparation work access`.

S3-SEC-I1..I4 materializa Estado de Identity y capabilities, credencial y Session opacas, administración e invariante de `GeneralConfiguration`, autorización vigente de la lectura de Work, destinos habilitados, nombre vigente de Product y la superficie frontend mínima autenticada de Preparation.

Los increments posteriores materializados son:

- `311b255 feat: start preparation quantities`;
- `25166d9 feat: mark preparation quantities ready`;
- `c6072ce feat: add preparation progress frontend`.

La Preparation mínima operativa ordinaria está materializada para nacimiento de Work, lectura segura, Start parcial, Ready parcial, operación multi-actor, Historia, idempotencia, acciones frontend y recorrido E2E. **Preparation mínima operativa del slice cerrada.** Esto no equivale a Preparation completa del MVP ni resuelve Correction, excepciones, SSE, consulta de Historia u otros [pendientes vigentes](../order-operations/pending.md).

## Estado de Slice 4

Slice 4 — Delivery mínima operativa materializa:

- `DeliveryState` 1:1 para cada Content;
- el snapshot histórico `RequiresPreparationAtConfirmation` y clasificación prepared/direct;
- read model autorizado de Delivery con nombres vigentes de Product;
- Delivery parcial y la intención `DeliverQuantity`;
- Historia `QuantityDelivered` separada de State;
- idempotencia durable, replay original y semántica de reautorización;
- locks y concurrencia coherentes con Ready y entre entregas;
- frontend Delivery con intents por Content y tratamiento de incertidumbre;
- recorrido E2E integrado para Prepared y Direct Content.

**Delivery mínima operativa del Slice 4 cerrada.** Delivery Correction ordinaria se materializó posteriormente de forma vertical; esto no equivale a Delivery completa del MVP: reversals, excepciones, History UI, SSE y las demás [fronteras vigentes](../order-operations/pending.md) siguen pendientes. Liquidation y Closure se materializaron posteriormente en Slice 6.

## Estado de Slice 5 — Inventory

Los increments materializados de Inventory son:

- `1987975 feat: establish inventory item foundation`;
- `3f03a15 feat: add inventory count reconciliation`;
- `337968a feat: add everyday inventory movements`;
- `1e58d48 feat: add inventory movement history`;
- `b0ad822 feat: add inventory frontend foundation`;
- `9a5de08 feat: add inventory physical operations frontend`.

El alcance consolidado incluye módulo, DbContext y schema propios; InventoryItem con cantidad inicialmente no establecida; Create Item; autorización separada de configuración/operación; CountObservation y Reconciliation inicial/ordinaria conforme a `AD-INV-01` y `AD-INV-02`; invalidación del Conteo por `MovementRevision`; Entry, ManualExit y Waste con saldos negativos visibles; Historia paginada autorizada con actor; idempotencia durable; concurrencia same-Item y atomicidad de rollback. El frontend materializa vistas separadas de configuración y operación, `null != 0`, advertencia de saldo negativo, flujo Count → Reconcile, operaciones físicas, Historia/refresco y retry incierto con la misma key sin aritmética optimista. El E2E cubre ese recorrido con Identities de capacidades distintas.

**Inventory minimum operational core of Slice 5 is closed.** Esto no afirma que Inventory MVP esté completo. Permanecen diferidos Movement Correction, `retire/reactivate/delete`, Unit Correction y sus reglas antes/después de Historia, SSE, persistencia de intención incierta entre recargas y la política final de reutilización de nombres antes del lifecycle. Tampoco se decide aquí que un Item retirado guarde cantidad `null`, la semántica final de reutilización de nombre ni la forma relacional de Correction.

Inventory permanece independiente de ventas y Catalog: `Product != InventoryItem`; Order, Confirmation, Preparation y Delivery no generan Movimientos de Inventory automáticamente.

## Estado de Slice 6 — Terminación normal de Order

Los increments materializados son:

- `0fa462e feat: add authoritative pending composition`;
- `ddf5c52 feat: add order liquidation and freeze`;
- `8438757 feat: add explicit order closure`;
- `e2ad8dd feat: add order liquidation and closure frontend`.

El alcance consolidado incluye PendingComposition autoritativa para Orders existentes, retrofit de seguridad y actor de Confirmation, Functional Amount derivado de Delivery efectiva y AppliedPrice, Liquidation completa simple o con cobro externo, Freeze consecuente y Closure explícito terminal. Incluye State e History separados, idempotencia durable, coordinación del mismo Order, frontend con blockers y retry incierto y ambas ramas terminales E2E con consulta exacta después de Closure.

**Normal Order termination happy path of Slice 6 is closed.** Esta declaración se limita al recorrido normal materializado. Delivery Correction ordinaria está implementada; permanecen pendientes sus excepciones y reversals, Corrections/excepciones de Preparation aún no implementadas, Cancellation de contenido/completa, Correction de AppliedPrice, otras Corrections/excepciones de Order, SSE activo y persistencia de intención incierta entre recargas. Los errores post-Liquidation permanecen intencionalmente sin resolución en el flujo ordinario del MVP. La limitación de lectura de Liquidation `occurredAt` después de reload continúa registrada en [terminación](../order-operations/ending.md), [frontend](../order-operations/frontend.md) y [pendientes](../order-operations/pending.md).
