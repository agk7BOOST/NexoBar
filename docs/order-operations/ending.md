# Liquidation, Freeze y Closure

### Functional Amount, Liquidation, Freeze y Closure — Slice 6

`OrderEconomicStateReader` deriva el Importe funcional del Estado vigente: suma de Delivery efectiva (`DeliveredQuantity`) × `AppliedPrice` histórico de cada Content. Usa aritmética `decimal` exacta y comprobada, con representación HTTP decimal string; no usa el precio actual de Catalog ni replay de History. Al liquidar se persiste un snapshot del importe completo.

La elegibilidad de Liquidation exige ausencia de PendingComposition, cumplimiento resuelto y Estado consistente, sin Liquidation previa. El read de Order expone `functionalAmount`, `isLiquidationEligible` y `liquidationBlockers`: `pending_composition`, `unresolved_fulfillment`, `state_inconsistent` y `already_liquidated`. Cumplimiento resuelto exige `DeliveredQuantity = F` por Content, con Estado consistente según [Q/R/F y Content Correction ordinaria](confirmation.md#q-r-y-f-s7-i2-content-correction-ordinaria-s7-i3d). Por ello una Content Correction ordinaria puede cambiar la elegibilidad de Liquidation; no cambia el Importe funcional. Cancellation sigue pendiente.

- `LiquidateSimple` registra el importe completo y un único medio declarado de texto libre, canonicalizado con trim exterior y longitud de 1 a 200 caracteres. No existe catálogo de medios ni integración de cobro.
- `RecordExternalCollection` registra el modo `ExternalCollection`, sin medio declarado. Ambos modos usan el importe calculado por el backend; el cliente no decide el importe. No hay pago parcial ni mixto.
- Liquidation confirma atómicamente State, `LiquidationHistory` e idempotencia durable, con actor y `occurredAt` UTC. Las intenciones nuevas requieren Session válida, Identity activa, `OrderOperationsAndBasicClosure` y antiforgery. Replay devuelve el resultado original sin duplicar efecto ni History.
- Freeze es consecuencia de Liquidation, derivado de su existencia; no hay comando Freeze. Bloquea nuevas mutaciones ordinarias actuales del mismo Order: Start/Discard de PendingComposition, Confirmación posterior, Start/Ready de Preparation y Delivery. Closure permanece como intención terminal explícita permitida después de Liquidation.
- La coordinación del mismo Order usa `Order FOR UPDATE` antes de los locks de Content/Work/DeliveryState/ContentQuantityState cuando corresponden. PendingComposition, Confirmación posterior, progreso, Liquidation y Closure participan de esa coordinación; así Liquidation evalúa un Estado estabilizado. El replay durable de efectos previos conserva su resultado original incluso después de Freeze o Closure.
- Closure es explícito, separado de Liquidation y terminal. Requiere Liquidation previa, ausencia de Closure y Estado consistente; no hay reapertura ordinaria. Confirma State, `ClosureHistory` y resultado idempotente atómicamente, con actor y `closedAt`, bajo autenticación, capacidad y antiforgery. El read expone `isClosed`, `closedAt` e `isClosureEligible`. La consulta exacta por referencia sigue disponible después del Cierre y conserva las Incorporations.

Liquidation `occurredAt` está persistido y disponible en el resultado del comando y en History, pero el read actual de Order no lo expone después de reload. El frontend no lo fabrica. Esto es una limitación de lectura/presentación pendiente, no ausencia del hecho histórico.

### Liquidation, Closure y completitud

`Delivered != Liquidated != Closed`. Slice 6 materializa Liquidation, Freeze como consecuencia y Closure explícito en OrderOperations. Delivery sigue siendo una dimensión distinta y sus nuevas mutaciones ordinarias quedan bloqueadas tras Liquidation. El read expone `isLiquidated`, `isFrozen` e `isClosed` como dimensiones diferenciadas; no constituyen un Status global único.

Tampoco se persiste `Order.IsDelivered` ni se afirma un Status global implementado. La completitud se evalúa contra la obligación vigente F; esa derivación no crea un Status global ni implementa futuras Corrections.
