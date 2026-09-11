# Confirmation, composición y contenido

## Confirmaciones, Incorporations y nacimiento de Work

La primera Confirmación crea el `Order` y su primera `Incorporation`; cada Confirmación posterior crea una nueva `Incorporation` del mismo `Order`. Ambas aceptan Products preparados.

- La mutación sobre un `Order` existente expresa la intención de una nueva Confirmación. La `operationalReference` es opaca; el request contiene `pendingCompositionId` e items y no modifica el `Context` del `Order`.
- Cada Confirmación posterior exitosa crea una `Incorporation` con ordinal sucesivo, una nueva `ConfirmationHistory` y contenido con el `appliedPrice` vigente estabilizado para esa Confirmación.

### PendingComposition autoritativa y seguridad de Confirmación

- La primera Composición permanece local antes de existir el Order. `PendingComposition` existe únicamente para Orders existentes y persiste un marcador con Id, OrderId, CreatedAt y CreatedByIdentityId; no persiste líneas de borrador.
- Hay como máximo un marcador por Order, respaldado por índice unique. Start y Discard son intenciones explícitas, autorizadas e idempotentes; no hay TTL, expiración ni descarte automático al recargar, salir o cambiar de sesión.
- CAN-01 está implementado: Complete Order Cancellation descarta atómicamente PendingComposition sin Confirmación, Content nuevo ni Discard público previo. CAN-03 rechaza nuevas Incorporations y Start de PendingComposition después de esa terminación. El estado terminal y sus límites se registran en [S7-I7D](ending.md#complete-order-cancellation--implementación-vertical-s7-i7d).
- La Confirmación posterior exige el `pendingCompositionId` exacto vigente y lo consume atómicamente junto con Incorporation, Content, ContentQuantityState, Work cuando corresponde, DeliveryState, History y resultado durable. Un marcador ausente o reemplazado produce conflicto; no se consume otro marcador por aproximación.
- First y Subsequent Confirmation requieren autenticación, Session válida, Identity activa y antiforgery. Una intención nueva exige `OrderOperationsAndBasicClosure`, estabilizada transaccionalmente. El actor procede de la Identity autenticada y se atribuye tanto al comando como a `ConfirmationHistory`.
- Las columnas legacy `actor_identity_id` de comandos de Confirmación e Historia permanecen nullable para preservar la verdad histórica: no se atribuyen actores ficticios a registros anteriores. Los nuevos comandos sí registran actor; el matching de replay lo incluye. El replay exige sesión válida e Identity activa, pero no reexige la responsabilidad para un efecto ya confirmado.

### Q, R, C y F: S7-I2; Content Correction ordinaria: S7-I3D; Content Cancellation ordinaria: S7-I4D

S7-I2 está implementado, verificado y committed. Esta sección es la referencia técnica de la base Q/R/F, conforme a la confirmación vigente del usuario y al código de `790d2f1`.

- `IncorporationContent` conserva condiciones confirmadas inmutables: `Quantity = Q` es la cantidad original confirmada; `AppliedPrice`, instruction y snapshot de preparación no se reinterpretan.
- Cada Content tiene exactamente un `ContentQuantityState`, con la misma identidad `(IncorporationId, ContentOrdinal)`, que conserva `RemovedByCorrectionQuantity = R` y la cantidad cancelada `C`. La obligación vigente es `F = Q - R - C`. INT-04 está implementada: `CancelledQuantity` C incluye Cancellation ordinaria desde Pending y Cancellation mediante OperationalIntervention; no se agrega otra deducción a F. Las transiciones de intervención están [implementadas verticalmente en S7-I6D](preparation.md#operationalintervention--decisiones-aprobadas-s7-int-d).
- First y Subsequent Confirmation crean ese Estado atómicamente con Content y las demás consecuencias, con `R = 0`, `C = 0`; inicialmente `F = Q`. State faltante es inconsistencia: no existe fallback a cero.
- El cumplimiento operacional usa F cuando representa obligación vigente. La cantidad confirmada Q conserva su significado histórico; no se sustituye globalmente por F.
- El Importe funcional usa `DeliveredQuantity × EffectiveAppliedPrice` por Content, sumado exactamente; no usa F como cantidad económica ni precio vigente de Catalog. `AppliedPrice` del Content conserva exclusivamente su significado histórico de precio registrado en Confirmation.
- Content Correction ordinaria corrige una cantidad confirmada erróneamente; no es Cancellation. Se identifica exactamente por `(IncorporationId, ContentOrdinal)`, incrementa atómicamente `R` y nunca modifica `Q`, el Content ni la Historia existente. En un Content preparado, cuando afecta `PendingQuantity`, también reduce atómicamente `PendingQuantity` y `TotalQuantity`; no afecta `InPreparationQuantity`, `ReadyQuantity` ni `DeliveredQuantity`. Puede llevar `F` a cero, preservando esos registros y `PendingComposition`.
- La corrección directa solo puede afectar `F - DeliveredQuantity`. La corrección de un Content preparado solo puede afectar `PendingQuantity`: reduce atómicamente `PendingQuantity` y `TotalQuantity` en la misma cantidad en que aumenta `R`; nunca afecta `InPreparationQuantity`, `ReadyQuantity` ni `DeliveredQuantity`. Preparation Correction corrige progreso registrado erróneamente; la cancelación de trabajo real se rige por las [decisiones de OperationalIntervention](preparation.md#operationalintervention--decisiones-aprobadas-s7-int-d).
- Content Cancellation ordinaria expresa que el Content fue confirmado válidamente pero después dejó de ser requerido; no corrige la Confirmación. Se identifica exactamente por `(IncorporationId, ContentOrdinal)`, incrementa atómicamente `C` y preserva `Q`, `R`, el Content, la Historia y `PendingComposition`. La cantidad es un entero positivo exacto, sin clamp silencioso, y puede llevar `F` a cero.
- En un Content preparado, Cancellation ordinaria solo puede afectar `PendingQuantity`: reduce atómicamente `PendingQuantity` y `TotalQuantity` en la misma cantidad en que aumenta `C`; `InPreparationQuantity`, `ReadyQuantity` y `DeliveredQuantity` no cambian. En un Content directo, la cantidad cancelable está limitada a `F - DeliveredQuantity`; nunca cancela contenido ya entregado. Cancellation mediante OperationalIntervention sobre trabajo real `InPreparation` o `Ready` está implementada verticalmente en S7-I6D conforme a INT-01/02; no amplía la Cancellation ordinaria de Pending.
- Content Correction y Content Cancellation ordinarias no cambian el Importe funcional, que continúa derivándose de Delivery efectiva. Ambas pueden cambiar la elegibilidad de Liquidation y están prohibidas después de Liquidation/Freeze.
- El frontend expone Content Correction y `Cancelar cantidad pendiente` como acciones separadas. Cancellation usa la identidad exacta `(IncorporationId, ContentOrdinal)`, refresca desde el Estado autoritativo tras éxito y, ante incertidumbre, conserva y reintenta la intención exacta. La lectura de Preparation expone `ContentOrdinal` para esa correlación. El recorrido vertical incluye E2E de Cancellation hasta Liquidation/Freeze.
- OperationalIntervention INT-01..INT-07 está implementada verticalmente en S7-I6D. Complete Order Cancellation CAN-01..05 está implementada verticalmente en S7-I7D; su deuda restante se conserva en [pendientes](pending.md).
- Un `PreparationWork` nace con `TotalQuantity > 0`; Cancellation ordinaria puede reducirlo a cero junto con `PendingQuantity` cuando `F` llega a cero. Las transiciones para trabajo real `InPreparation` o `Ready` se definen en INT-01/02 y están implementadas verticalmente en S7-I6D.

S7-I3D está verticalmente implementado y verificado: backend 741/741, OrderOperations 424/424, frontend 265/265 y Playwright 9/9. Evidencia de creación/modelo: [OrderModel](../../backend/src/NexoBar.OrderOperations/OrderModel.cs) y [ConfirmedContentFactory](../../backend/src/NexoBar.OrderOperations/ConfirmedContentFactory.cs). La [migración y sus protecciones](migrations.md#base-qrf-s7-i2) son parte de S7-I2.

S7-I4D está verticalmente implementado. Los totales de verificación no se reiteran aquí porque este documento no conserva un checkpoint verificado de S7-I4D.

INT-05 conserva la procedencia de Cancellation tras trabajo real en [Historia semántica](contracts-and-history.md#operationalintervention--historia-y-estado-aprobados-s7-int-d), sin contadores adicionales por etapa en State. INT-07 permite coexistencia de intervención parcial con PendingComposition sin consumirlo ni descartarlo; no cambia Functional Amount ni permite intervención después de Liquidation/Freeze.

### Applied Price Correction — decisiones aprobadas S7-PRICE-D

**PRICE-CORR-01 — precio confirmado y precio efectivo.** `IncorporationContent.AppliedPrice` permanece inmutable y significa el precio registrado en Confirmation. Cada Content tiene conceptualmente un `ContentAppliedPriceState` estrecho 1:1, identificado por `(IncorporationId, ContentOrdinal)`, con `EffectiveAppliedPrice`. En Confirmation nace con `EffectiveAppliedPrice = IncorporationContent.AppliedPrice`. Los cálculos económicos actuales usan el valor efectivo; Confirmation History y el replay continúan usando el valor original. Los campos históricos existentes llamados `appliedPrice` no cambian silenciosamente de significado.

**PRICE-CORR-02 — intención y colaboración Catalog.** Applied Price Correction apunta a un Content confirmado exacto por `(IncorporationId, ContentOrdinal)`. El cliente no aporta un importe arbitrario. OrderOperations identifica el Product de ese Content y obtiene su precio actual válido mediante colaboración explícita con Catalog, sin acceder a su almacenamiento. Adopta ese valor como `EffectiveAppliedPrice` sólo para ese Content: no modifica Product, precio de Catalog, PriceVersion, otros Contents ni otros Orders. Un cambio de Catalog no repricia automáticamente Orders existentes; aplicar el precio actual de Catalog requiere esta intención explícita.

**PRICE-CORR-03/04 — sucesión y no-op.** Se permiten correcciones sucesivas antes de las fronteras terminales o Freeze; cada una sustituye el precio efectivo vigente y preserva la cadena en Historia, sin alterar el precio original confirmado. Con una intención y key nuevas, si el precio actual válido de Catalog coincide exactamente con `EffectiveAppliedPrice`, se rechaza como «no correction to apply»: no crea Historia, no muta State ni fabrica una corrección. El replay exacto de una corrección ya confirmada devuelve su resultado durable original aunque el precio actual de Catalog haya cambiado después.

**PRICE-CORR-05 — Estado no económico preservado.** La corrección no modifica Q, R, C, F, Pending, InPreparation, Ready, Total, Delivered ni PendingComposition. Puede coexistir con PendingComposition y no ejecuta Delivery Correction, Content Correction, Content Cancellation, Preparation ni una mutación de Catalog.

### Complete Order Cancellation — cantidades implementadas S7-I7D

Un comando explícito Order-level calcula y valida el plan completo antes de mutar, y cancela atómicamente toda la obligación vigente. Requiere D efectivo igual a cero en todos los Contents; una Delivery histórica corregida válidamente hasta D efectivo cero no bloquea por sí sola. No corrige automáticamente Delivery ni niega preparación o Delivery históricas. No es un batch de comandos públicos independientes.

Para cada Content, las cantidades del plan proceden del Estado actual estabilizado:

| Content/etapa | Consecuencia |
| --- | --- |
| Direct | `C += F`; `F -> 0`. |
| Prepared Pending | `C += P`; `P -> 0`; `T -= P_anterior`. |
| Prepared InPreparation | Semántica INT-01: `C += I`; `I -> 0`; `T -= I_anterior`; Start original verdadero. |
| Prepared Ready | Con D=0, semántica INT-02: `C += Y`; `Y -> 0`; `T -= Y_anterior`; Start/Ready originales verdaderos. |

Cada reducción de T reduce F en la misma cantidad. Al finalizar, por Content se preservan Q y R, `C_final = C_anterior + F_anterior = Q - R`, `F=0` y `D=0`. En cada Prepared Content queda `P=I=Y=T=0`. C es la única deducción por cancelación; no se agregan contadores de cancelación por etapa. Se conservan Content y Work, y la [Historia semántica](contracts-and-history.md#complete-order-cancellation--historia-e-idempotencia-implementadas-s7-i7d) explica la procedencia de las cantidades.

CAN-04 permite todos F=0 antes de la decisión sin fabricar hechos de cantidad cero. Todos F=0 no son por sí solos un marcador de Complete Cancellation: la [terminación CAN-03](ending.md#complete-order-cancellation--implementación-vertical-s7-i7d) tiene Estado propio y no pasa por Liquidation/Closure.

### IncorporationContent, snapshot histórico y líneas homogéneas

`IncorporationContent` tiene PK compuesta `(incorporation_id, content_ordinal)` y contiene `product_id`, `quantity`, `requires_preparation_at_confirmation`, `applied_price` e `instruction` nullable. `contentOrdinal` es una identidad técnica local a la `Incorporation`, positiva y estable. Delivery lo expone junto con `incorporationId` para identificar el Content target; no representa posición UX, unidad física, unidad conceptual de cumplimiento ni orden de captura.

`RequiresPreparationAtConfirmation : bool` es el snapshot histórico, ordinariamente inmutable, de si el Product requería Preparation cuando ese Content fue confirmado. Su Source es el snapshot estabilizado de Product usado por Confirmation; no representa la configuración vigente de Catalog, la existencia actual del Work ni el `Ready` actual. La existencia de Work dejó de ser el único discriminador histórico.

La configuración de Catalog es prospectiva y no reinterpreta Contents existentes:

- un Content confirmado direct conserva `RequiresPreparationAtConfirmation = false` aunque después el Product pase a requerir Preparation; una nueva Confirmation usa `true`;
- un Content confirmado prepared conserva `RequiresPreparationAtConfirmation = true` y su Work/snapshot de destino original aunque después el Product deje de requerir Preparation o cambie del destino A al B; una nueva Confirmation usa la configuración nueva.

La clasificación materializada exige exactamente una de estas combinaciones:

```text
flag = true  + PreparationWork presente → Prepared Content
flag = false + PreparationWork ausente  → Direct Content
```

`flag = true` sin Work y `flag = false` con Work son inconsistencias técnicas. Delivery no usa Catalog vigente para clasificar un Content.

Ya no existe unicidad `(incorporation_id, product_id)`: una `Incorporation` puede contener múltiples líneas del mismo Product con instrucciones distintas. Cada Content representa una cantidad homogénea respecto de `ProductId + canonical instruction`. Por ejemplo, son líneas válidas dentro de una misma Confirmación:

```text
Product A x1 / null
Product A x1 / "sin cebolla"
Product A x2 / "sin tomate"
```

No existe individualización de unidades físicas. `Quantity` permanece agregada y sus cantidades operacionales pueden evolucionar parcialmente en Preparation y Delivery. No se equipara `IncorporationContent` con una unidad conceptual de cumplimiento; una identidad más fuerte para esas unidades queda fuera de alcance salvo que aparezca un nuevo driver.

`instruction` es información operacional puntual asociada al consumo confirmado. Su source of truth es `IncorporationContent`; es nullable e inmutable después de la Confirmación en el alcance actual. No se duplica en `PreparationWork` y no existe `InstructionAdded History`: `ConfirmationHistory` junto con el Content persistido explican su existencia.

La canonicalización de instruction aplica estas reglas:

- omitted, `null`, empty o solo whitespace → `null`;
- `CRLF` y `CR` → `LF`;
- trim exterior;
- case y punctuation preservados;
- whitespace interno preservado;
- comparación ordinal.

Una instruction no es Product variant, modifier ni recipe, y no tiene efecto sobre precio o inventario.

Una Confirmación no puede contener dos líneas con el mismo `(ProductId, canonicalInstruction)`. El backend responde `order_operations.confirmation.duplicate_line`; no agrupa silenciosamente sus cantidades.

El lock ordering y la escritura transaccional materializados son:

```text
First Confirmation
advisory idempotency
→ Session + Identity / replay / capability vigente para intención nueva
→ Products FOR SHARE
→ Order / Incorporation / Content / ContentQuantityState(R=0) / Work condicional / DeliveryState / History / command
→ commit

Subsequent Confirmation
advisory idempotency
→ Session + Identity / replay / capability vigente para intención nueva
→ Order FOR UPDATE
→ validación de Freeze y marcador exacto
→ Products FOR SHARE
→ Incorporation / Content / ContentQuantityState(R=0) / Work condicional / DeliveryState / History / command / consumo del marcador
→ commit
```

- En Confirmaciones posteriores, `Order FOR UPDATE` serializa la asignación del ordinal `MAX + 1`; la restricción única `(order_id, ordinal)` es la defensa física adicional.
- Un factory interno estrecho y común a First y Subsequent centraliza la creación de `IncorporationContent`, la creación condicional de `PreparationWork` y la creación de `DeliveryState` y `ContentQuantityState`; no es un framework ni un pipeline genérico.
- Todo Content crea atómicamente `DeliveryState(0)` y `ContentQuantityState(R=0)` durante Confirmation. Un Direct Content crea Content + ningún Work + ambos Estados; un Prepared Content crea Content + un Work + ambos Estados. El replay no duplica ninguno de ellos.
- La responsabilidad aplicada al Work procede del snapshot estabilizado del `Product`. Los cambios posteriores de configuración del `Product` no modifican Work existente.
- No existe un evento `WorkCreated`.

### Replay de Confirmación

- El replay se resuelve antes de consultar `Catalog` y reconstruye la respuesta desde la persistencia original de `OrderOperations`; en una Confirmación posterior tampoco bloquea el `Order`.
- No crea nuevos Work y no requiere `workId` en las tablas de comandos.
- El nacimiento de Work no introduce una idempotencia adicional: queda cubierto por la idempotencia y transacción de la Confirmación que lo origina.

Composición no es Pedido. Corrección no significa Cancelación; las [fronteras abiertas](pending.md) conservan esa separación.
