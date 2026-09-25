# Composición, consulta y terminación frontend

## Context configurado y Context Change — MVP-FC-CTX

GeneralConfiguration lista y crea Contextos mediante su UI administrativa, hace reload autoritativo y conserva una creación incierta para retry explícito con la misma key. No presenta UUID crudo ni controles de lifecycle. La autoridad es `GeneralConfiguration`.

OrderOperations carga opciones configuradas para First Confirmation, permite seleccionar Context por `operationalName` y envía sólo `contextId`; no ofrece texto libre. El lookup de Order muestra el Context actual por nombre operacional, sin UUID. La Identity con `OrderOperationsAndBasicClosure` puede seleccionar Context en First Confirmation y ejecutar un cambio explícito sobre un Order operativo; eso no habilita administrar Contextos. Preparation lee el Context actual sólo para coordinación y no administra ni cambia Context. El contrato backend, las reglas de no-op/stale y los límites de mutabilidad están en [Context actual, cambio e Historia](contracts-and-history.md#context-actual-cambio-e-historia-mvp-fc-ctx).

Después de una mutación exitosa, el frontend relee el Order y la preparación vigente desde sus autoridades mediante las invalidaciones existentes. Un cambio stale, no-op o target faltante se informa y se resuelve con reload/selección autoritativos; no se aplica optimistic state ni se rebasea el comando. Después de Freeze, Closure o Complete Cancellation no ofrece Context Change.

- La Composición usa `CompositionLine { draftLineId, productId, quantity, instruction, unavailableProductExceptionRequested }`. `draftLineId` se crea con `crypto.randomUUID()`, es estable mientras vive la línea y existe solo en frontend: no se envía, no pertenece al dominio y no es el `Idempotency-Key`. La intención excepcional sí forma parte del contenido de Confirmation y se conserva por línea.
- Cantidad `+/-`, remove e instruction editable operan por `draftLineId`, por lo que pueden coexistir múltiples líneas del mismo Product. “Agregar” incrementa la línea existente sin instruction canonical; “Agregar otra línea” crea una nueva línea del mismo Product.
- El frontend detecta líneas duplicadas por `(ProductId, canonicalInstruction)` y bloquea la Confirmación sin combinar cantidades.
- Ante incertidumbre de First o Subsequent, el workflow congela exactamente la key, destination u order reference relevante, context donde corresponde, marcador de Subsequent y cada `productId`, `quantity` e instruction canonical. Mientras existe incertidumbre no permite editar la Composición ni cambiar destination; retry reenvía el mismo request exacto con la misma key. No ofrece descarte ordinario de la intención incierta.
- Un `409` conocido no se trata como incertidumbre: la Composición permanece editable y la siguiente intención usa una key nueva.
- El lookup de Order muestra Product actual, quantity, `appliedPrice` histórico e instruction confirmada; cuando es null muestra “Sin instrucción”. Las líneas del mismo Product permanecen visualmente distinguibles.

### Terminación de Order y PendingComposition frontend

- Para un Order existente, el workflow inicia y descarta explícitamente el marcador autoritativo. Presenta marcadores remotos sin inventar sus líneas locales y evita iniciar un segundo marcador. La primera Composición sigue siendo local.
- `OrderEnding` muestra Functional Amount, elegibilidad y blockers autoritativos. Ofrece Liquidation simple con un medio libre o registro de cobro gestionado externamente, y advierte la consecuencia de Freeze antes de liquidar.
- Tras el éxito de Liquidation refresca el Order desde backend, sin cálculo económico optimista. La invalidación SSE no sustituye ni invalida ese read propio mientras el comando está ocupado. La presentación Frozen muestra importe liquidado, modo y medio cuando corresponde, y bloquea operaciones ordinarias. Liquidation no muestra por sí sola el Pedido como cerrado.
- Closure tiene una acción explícita `Cerrar Pedido`, disponible según elegibilidad después de Liquidation. Tras la confirmación HTTP autoritativa, muestra el Cierre y `closedAt` del resultado y retira el read activo, que pasa a 404 por AD-SEC-05. La Historia terminal conserva las Incorporations bajo una consulta separada. No ofrece continuar, liquidar de nuevo ni reabrir.
- Complete Cancellation usa el resultado HTTP terminal confirmado para mostrar la cancelación, su fecha y el descarte de PendingComposition, y retira la vista activa. Un read operacional nuevo de ese Order da 404; la Historia terminal es la consulta separada de sus hechos.
- Ante network, timeout o `5xx` incierto se conserva en memoria el mismo endpoint, body e Idempotency-Key para retry exacto; se bloquean acciones incompatibles. Un conflicto conocido refresca Estado y no se presenta como éxito. Si falla el refresco, se exige actualizar antes de continuar.
- El timestamp de Liquidation recibido por comando se muestra mientras está disponible localmente. Después de reload, el GET de Order no devuelve ese `occurredAt`: la UI informa que la fecha no está disponible en esa consulta, sin fabricar un timestamp.
- Para un Order abierto activamente, SSE usa sólo `order.active:<orderId>` en el único EventSource de App/Session. Sus owners montados refrescan reads autoritativos por invalidación y fencing; la señal no resuelve intenciones inciertas, que conservan retry exacto. Un `404` tras invalidación final retira la vista activa sin reabrir el Order. No hay cola offline ni retry automático de comandos.

### Content Cancellation frontend

- El frontend ofrece `Cancelar cantidad pendiente` separadamente de Content Correction y dirige ambas acciones al Content exacto mediante `(IncorporationId, ContentOrdinal)`.
- Tras una Cancellation exitosa refresca el Estado autoritativo; no ajusta cantidades, Importe funcional ni elegibilidad de Liquidation de forma optimista.
- Ante network, timeout o `5xx` incierto conserva endpoint, body e `Idempotency-Key` de la Cancellation y ofrece retry de esa intención exacta. Mientras exista la incertidumbre no inicia una segunda mutación incompatible sobre el mismo Content.

### Applied Price Correction frontend — S7-I8D

- La acción distinta «Corregir precio aplicado» identifica el Content exacto por `(IncorporationId, ContentOrdinal)`. Muestra separadamente precio confirmado original, precio aplicado efectivo actual y precio vigente de Catalog; no ofrece input monetario libre.
- La evaluación autoritativa determina disponibilidad y bloqueadores. Tras éxito refresca la evaluación de precio y el Estado económico/de terminación del Order; no trata una mutación optimista de Delivery o importe como autoritativa. Puede reutilizar el read de Delivery para la identidad exacta, pero la elegibilidad de precio procede de su evaluación autoritativa.
- Una invalidación SSE pendiente no cierra una confirmación explícita de precio mientras la persona decide. El comando revalida el precio vigente en Catalog y, después de responder, la UI relee precios e importe.

### Terminal Order History — MVP-FC-TOH: CLOSED

Order Lookup ofrece una acción separada de History para escribir la referencia operacional exacta. Active lookup permanece en el endpoint activo; History usa exclusivamente `GET /api/order-operations/order-history/{operationalReference}` y un Order no terminal/ausente se muestra como History no disponible. No se hace probe cruzado.

La superficie dedicada «Historial del pedido» indica «Solo lectura» y no monta el workflow activo ni controles de mutación. App la ofrece únicamente a una Identity cuya proyección actual contiene `OrderOperationsAndBasicClosure`; GeneralConfiguration o Preparation por sí solas no la muestran.

Se presentan los snapshots de Context de confirmación/final y cambios de Context, Incorporations separadas y sus Contents, preparación, Delivery y sus correcciones, Content Corrections/Cancellations, Applied Price corrections y, según la rama terminal, Liquidation/pago seguido de Closure o Complete Cancellation con sus consecuencias. Se conserva el orden backend dentro de cada sección, sin fabricar una cronología global.

El Product visible se deriva únicamente de `productOperationalNameSnapshot`; un snapshot nulo muestra exactamente «Nombre histórico no disponible». No se consulta Catalog, Identity administration ni OperationalConfiguration en runtime. Los actores se atribuyen mediante el `ActorIdentityId` estable. History carga bajo demanda y no usa SSE ni polling. El contrato y la semántica de los hechos están en [Contratos, Historia e idempotencia](contracts-and-history.md#terminal-order-history--mvp-fc-toh-closed).

### RF-PED-024/025 — intervención explícita sobre Product no disponible (S10)

La UI deriva una señal de presentación sólo cuando la Identity actual tiene conjuntamente `OrderOperationsAndBasicClosure` y `OperationalIntervention`. El backend sigue siendo la autoridad: la señal no es una autorización persistida ni sustituye la comprobación de Confirmation.

El browse operacional conserva sus fronteras: el actor con sólo `OrderOperationsAndBasicClosure` no ve Products no disponibles; el actor dual sí los ve con `isAvailable = false`. Para un Product no disponible, el Add ordinario permanece deshabilitado y aparece una acción distinta, «Agregar mediante intervención», únicamente para el actor dual. Esa acción crea una línea con `unavailableProductExceptionRequested = true`; el Add ordinario crea `false`.

La intención vive en cada línea de Composition y no se recalcula desde disponibilidad o responsabilidades actuales. Una línea excepcional muestra «Intervención solicitada». Si la Composition contiene alguna, la Confirmation usa CTA y aviso explícitos de intervención. Antes de la respuesta autoritativa no se muestra el marcador histórico «Incorporado mediante intervención».

Tras una Confirmation exitosa, el lookup de Order muestra «Incorporado mediante intervención» sólo cuando el backend devuelve `unavailableProductExceptionApplied = true`. Una solicitud excepcional frente a un Product que ya está disponible puede completar con `Applied = false` y no muestra ese marcador.

Si se pierde `OperationalIntervention` mientras la Composition está abierta, la línea y su intención se conservan; un `403 order_operations.confirmation.operational_intervention_required` informa que debe recuperarse la autoridad. Si una línea ordinaria recibe `product_unavailable`, se refresca el browse sin convertirla automáticamente en excepcional.

Ante una respuesta incierta de First o Subsequent se conserva exactamente Idempotency-Key, contenido canonical, quantity, instruction y `unavailableProductExceptionRequested`. El retry no vuelve a calcular la intención desde Catalog ni desde las responsabilidades actuales.
