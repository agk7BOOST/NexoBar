# Liquidation, Freeze y Closure

### Complete Order Cancellation — decisiones aprobadas S7-CAN-D

Complete Order Cancellation es una intención explícita de alcance Order: «toda la obligación de cumplimiento actual de este Order deja de ser requerida». Es una terminación excepcional distinta del recorrido ordinario Liquidation/Closure. S7-CAN-D registra decisiones aprobadas; no afirma su implementación.

El éxito exige ausencia de Delivery efectiva en todos los Contents y un Order abierto, sin Liquidation/Freeze, Closure ni Complete Cancellation previa. No invoca Delivery Correction automáticamente. Las [transiciones de cantidades](confirmation.md#complete-order-cancellation--cantidades-aprobadas-s7-can-d) preservan Q y R, dejan todos F y D efectivos en cero y, para cada Content preparado, P=I=Y=T=0. C contabiliza exactamente la obligación restante cancelada. Functional Amount es cero porque Delivery efectiva es cero; Cancellation no crea movimientos financieros ni refunds.

**CAN-01 — PendingComposition.** La cancelación completa descarta atómicamente cualquier PendingComposition existente como parte de la decisión terminal. No lo confirma, no crea Content, no exige un comando público Discard previo ni emite Cancellation ficticia de cantidad por ediciones no confirmadas. Su existencia y descarte forman parte del resultado/Historia semánticos según [CAN-01 e Historia](contracts-and-history.md#complete-order-cancellation--historia-e-idempotencia-aprobadas-s7-can-d).

**CAN-02 — Autorización.** Exige `OrderOperationsAndBasicClosure` y, cuando el plan incluye cantidad actual InPreparation o Ready, también `OperationalIntervention` del mismo actor. La condición se evalúa después de estabilizar/bloquear el Estado vigente. Session, Identity, antiforgery y key, junto con la exclusión de Preparation/PreparationEnablement, se detallan en [seguridad](../identities-and-capabilities/security.md#complete-order-cancellation--autoridad-aprobada-can-02).

**CAN-03 — Estado terminal explícito.** Se introduce conceptualmente un `OrderCancellationState` estrecho que representa la terminación por Complete Order Cancellation. No es un `OrderStatus` genérico, Liquidation, Closure ni Freeze. Todos F=0 por sí solos no identifican Complete Cancellation. Una vez existe ese Estado, el Order ya no está abierto: no admite nuevas Incorporations, Start de PendingComposition ni mutaciones operacionales ordinarias. El acceso histórico y de lectura permanece disponible.

Después del éxito no se liquida ni se cierra el Order, no se crea Freeze, no se requiere Liquidation de importe cero ni se crea Closure automáticamente. El Order es terminal por `OrderCancellationState` mismo; tampoco procede posteriormente por el recorrido ordinario Liquidation/Closure.

**CAN-04 — F ya puede ser cero.** Un Order todavía abierto y no terminal, sin Delivery efectiva, Liquidation, Closure ni Complete Cancellation previa, puede cancelarse completamente aunque todos los F actuales sean cero. Se crea el Estado/Historia terminal Order-level y se descarta PendingComposition atómicamente si existe. No se crean hechos de Content Cancellation o intervención de cantidad cero.

**CAN-05 — Repetición terminal.** Replay exacto con la misma key durable devuelve el resultado original sin nuevo Estado ni Historia. Una nueva intención con otra key contra un Order ya completamente cancelado se rechaza por terminalidad; no crea una segunda cancelación. Véase [idempotencia](contracts-and-history.md#complete-order-cancellation--historia-e-idempotencia-aprobadas-s7-can-d).

### Functional Amount, Liquidation, Freeze y Closure — Slice 6

Este apartado describe el recorrido ordinario implementado. La terminación excepcional aprobada en S7-CAN-D queda fuera de ese recorrido: un Order completamente cancelado no es elegible para Liquidation ni Closure, aunque D=F=0.

`OrderEconomicStateReader` deriva el Importe funcional del Estado vigente: suma de Delivery efectiva (`DeliveredQuantity`) × `AppliedPrice` histórico de cada Content. Usa aritmética `decimal` exacta y comprobada, con representación HTTP decimal string; no usa el precio actual de Catalog ni replay de History. Al liquidar se persiste un snapshot del importe completo.

La elegibilidad de Liquidation exige ausencia de PendingComposition, cumplimiento resuelto y Estado consistente, sin Liquidation previa. El read de Order expone `functionalAmount`, `isLiquidationEligible` y `liquidationBlockers`: `pending_composition`, `unresolved_fulfillment`, `state_inconsistent` y `already_liquidated`. Cumplimiento resuelto exige `DeliveredQuantity = F` por Content, con Estado consistente según [Q/R/C/F, Content Correction y Content Cancellation ordinarias](confirmation.md#q-r-c-y-f-s7-i2-content-correction-ordinaria-s7-i3d-content-cancellation-ordinaria-s7-i4d). Por ello Content Correction, Content Cancellation ordinaria u OperationalIntervention pueden cambiar la elegibilidad de Liquidation; ninguna cambia el Importe funcional.

- `LiquidateSimple` registra el importe completo y un único medio declarado de texto libre, canonicalizado con trim exterior y longitud de 1 a 200 caracteres. No existe catálogo de medios ni integración de cobro.
- `RecordExternalCollection` registra el modo `ExternalCollection`, sin medio declarado. Ambos modos usan el importe calculado por el backend; el cliente no decide el importe. No hay pago parcial ni mixto.
- Liquidation confirma atómicamente State, `LiquidationHistory` e idempotencia durable, con actor y `occurredAt` UTC. Las intenciones nuevas requieren Session válida, Identity activa, `OrderOperationsAndBasicClosure` y antiforgery. Replay devuelve el resultado original sin duplicar efecto ni History.
- Freeze es consecuencia de Liquidation, derivado de su existencia; no hay comando Freeze. Bloquea nuevas mutaciones ordinarias actuales del mismo Order: Start/Discard de PendingComposition, Confirmación posterior, Content Correction, Content Cancellation, Start/Ready y Preparation Correction de Preparation, OperationalIntervention y Delivery. Closure permanece como intención terminal explícita permitida después de Liquidation.
- La coordinación del mismo Order usa `Order FOR UPDATE` antes de los locks de Content/Work/DeliveryState/ContentQuantityState cuando corresponden. PendingComposition, Confirmación posterior, progreso, Liquidation y Closure participan de esa coordinación; así Liquidation evalúa un Estado estabilizado. El replay durable de efectos previos conserva su resultado original incluso después de Freeze o Closure.
- Closure es explícito, separado de Liquidation y terminal. Requiere Liquidation previa, ausencia de Closure y Estado consistente; no hay reapertura ordinaria. Confirma State, `ClosureHistory` y resultado idempotente atómicamente, con actor y `closedAt`, bajo autenticación, capacidad y antiforgery. El read expone `isClosed`, `closedAt` e `isClosureEligible`. La consulta exacta por referencia sigue disponible después del Cierre y conserva las Incorporations.

Liquidation `occurredAt` está persistido y disponible en el resultado del comando y en History, pero el read actual de Order no lo expone después de reload. El frontend no lo fabrica. Esto es una limitación de lectura/presentación pendiente, no ausencia del hecho histórico.

### OperationalIntervention — límite aprobado INT-07

La intervención parcial aprobada en [S7-INT-D](preparation.md#operationalintervention--decisiones-aprobadas-s7-int-d), implementada verticalmente en S7-I6D, está prohibida después de Liquidation/Freeze. No cambia Delivery efectiva ni Functional Amount. Puede coexistir con PendingComposition sin consumirlo ni descartarlo; el marcador sigue bloqueando Liquidation bajo las reglas vigentes. La intervención no liquida ni cierra automáticamente el Order.

INT-07 no implementa Cancellation completa de Order ni reparación post-Liquidation. Tampoco implica desperdicio, descarte o recuperación físicos ni modifica Inventory.

### Liquidation, Closure y completitud

`Delivered != Liquidated != Closed`. Slice 6 materializa Liquidation, Freeze como consecuencia y Closure explícito en OrderOperations. Delivery sigue siendo una dimensión distinta y sus nuevas mutaciones ordinarias quedan bloqueadas tras Liquidation. El read expone `isLiquidated`, `isFrozen` e `isClosed` como dimensiones diferenciadas; no constituyen un Status global único.

Tampoco se persiste `Order.IsDelivered` ni se afirma un Status global implementado. La completitud se evalúa contra la obligación vigente F; esa derivación no crea un Status global ni implementa futuras Corrections.
