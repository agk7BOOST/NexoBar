# Composición, consulta y terminación frontend

- La Composición usa `CompositionLine { draftLineId, productId, quantity, instruction }`. `draftLineId` se crea con `crypto.randomUUID()`, es estable mientras vive la línea y existe solo en frontend: no se envía, no pertenece al dominio y no es el `Idempotency-Key`.
- Cantidad `+/-`, remove e instruction editable operan por `draftLineId`, por lo que pueden coexistir múltiples líneas del mismo Product. “Agregar” incrementa la línea existente sin instruction canonical; “Agregar otra línea” crea una nueva línea del mismo Product.
- El frontend detecta líneas duplicadas por `(ProductId, canonicalInstruction)` y bloquea la Confirmación sin combinar cantidades.
- Ante incertidumbre de First o Subsequent, el workflow congela exactamente la key, destination u order reference relevante, context donde corresponde, marcador de Subsequent y cada `productId`, `quantity` e instruction canonical. Mientras existe incertidumbre no permite editar la Composición ni cambiar destination; retry reenvía el mismo request exacto con la misma key. No ofrece descarte ordinario de la intención incierta.
- Un `409` conocido no se trata como incertidumbre: la Composición permanece editable y la siguiente intención usa una key nueva.
- El lookup de Order muestra Product actual, quantity, `appliedPrice` histórico e instruction confirmada; cuando es null muestra “Sin instrucción”. Las líneas del mismo Product permanecen visualmente distinguibles.

### Terminación de Order y PendingComposition frontend

- Para un Order existente, el workflow inicia y descarta explícitamente el marcador autoritativo. Presenta marcadores remotos sin inventar sus líneas locales y evita iniciar un segundo marcador. La primera Composición sigue siendo local.
- `OrderEnding` muestra Functional Amount, elegibilidad y blockers autoritativos. Ofrece Liquidation simple con un medio libre o registro de cobro gestionado externamente, y advierte la consecuencia de Freeze antes de liquidar.
- Tras el éxito refresca el Order desde backend, sin cálculo económico optimista. La presentación Frozen muestra importe liquidado, modo y medio cuando corresponde, y bloquea operaciones ordinarias. Liquidation no muestra por sí sola el Pedido como cerrado.
- Closure tiene una acción explícita `Cerrar Pedido`, disponible según elegibilidad después de Liquidation. Closed muestra el Cierre y su fecha, conserva la consulta y no ofrece continuar, liquidar de nuevo ni reabrir.
- Ante network, timeout o `5xx` incierto se conserva en memoria el mismo endpoint, body e Idempotency-Key para retry exacto; se bloquean acciones incompatibles. Un conflicto conocido refresca Estado y no se presenta como éxito. Si falla el refresco, se exige actualizar antes de continuar.
- El timestamp de Liquidation recibido por comando se muestra mientras está disponible localmente. Después de reload, el GET de Order no devuelve ese `occurredAt`: la UI informa que la fecha no está disponible en esa consulta, sin fabricar un timestamp.
- No hay SSE activo ni persistencia de la intención incierta entre recargas; no hay cola offline ni retry automático en background.
