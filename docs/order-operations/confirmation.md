# Confirmation, composición y contenido

## Confirmaciones, Incorporations y nacimiento de Work

La primera Confirmación crea el `Order` y su primera `Incorporation`; cada Confirmación posterior crea una nueva `Incorporation` del mismo `Order`. Ambas aceptan Products preparados.

- La mutación sobre un `Order` existente expresa la intención de una nueva Confirmación. La `operationalReference` es opaca; el request contiene `pendingCompositionId` e items y no modifica el `Context` del `Order`.
- Cada Confirmación posterior exitosa crea una `Incorporation` con ordinal sucesivo, una nueva `ConfirmationHistory` y contenido con el `appliedPrice` vigente estabilizado para esa Confirmación.

### PendingComposition autoritativa y seguridad de Confirmación

- La primera Composición permanece local antes de existir el Order. `PendingComposition` existe únicamente para Orders existentes y persiste un marcador con Id, OrderId, CreatedAt y CreatedByIdentityId; no persiste líneas de borrador.
- Hay como máximo un marcador por Order, respaldado por índice unique. Start y Discard son intenciones explícitas, autorizadas e idempotentes; no hay TTL, expiración ni descarte automático al recargar, salir o cambiar de sesión.
- La Confirmación posterior exige el `pendingCompositionId` exacto vigente y lo consume atómicamente junto con Incorporation, Content, ContentQuantityState, Work cuando corresponde, DeliveryState, History y resultado durable. Un marcador ausente o reemplazado produce conflicto; no se consume otro marcador por aproximación.
- First y Subsequent Confirmation requieren autenticación, Session válida, Identity activa y antiforgery. Una intención nueva exige `OrderOperationsAndBasicClosure`, estabilizada transaccionalmente. El actor procede de la Identity autenticada y se atribuye tanto al comando como a `ConfirmationHistory`.
- Las columnas legacy `actor_identity_id` de comandos de Confirmación e Historia permanecen nullable para preservar la verdad histórica: no se atribuyen actores ficticios a registros anteriores. Los nuevos comandos sí registran actor; el matching de replay lo incluye. El replay exige sesión válida e Identity activa, pero no reexige la responsabilidad para un efecto ya confirmado.

### Q, R y F: S7-I2; Content Correction ordinaria: S7-I3D

S7-I2 está implementado, verificado y committed. Esta sección es la referencia técnica de la base Q/R/F, conforme a la confirmación vigente del usuario y al código de `790d2f1`.

- `IncorporationContent` conserva condiciones confirmadas inmutables: `Quantity = Q` es la cantidad original confirmada; `AppliedPrice`, instruction y snapshot de preparación no se reinterpretan.
- Cada Content tiene exactamente un `ContentQuantityState`, con la misma identidad `(IncorporationId, ContentOrdinal)`, que contiene `RemovedByCorrectionQuantity = R`. La obligación vigente es `F = Q - R`.
- First y Subsequent Confirmation crean ese Estado atómicamente con Content y las demás consecuencias, con `R = 0`; inicialmente `F = Q`. State faltante es inconsistencia: no existe fallback a `R = 0`.
- El cumplimiento operacional usa F cuando representa obligación vigente. La cantidad confirmada Q conserva su significado histórico; no se sustituye globalmente por F.
- El Importe funcional sigue siendo `DeliveredQuantity × AppliedPrice` por Content, sumado exactamente; no usa F como cantidad económica ni precio vigente de Catalog.
- Content Correction ordinaria corrige una cantidad confirmada erróneamente; no es Cancellation. Se identifica exactamente por `(IncorporationId, ContentOrdinal)`, incrementa atómicamente `R` y nunca modifica `Q`, el Content, el Work ni la Historia existente. Puede llevar `F` a cero, preservando esos registros y `PendingComposition`.
- La corrección directa solo puede afectar `F - DeliveredQuantity`. La corrección de un Content preparado solo puede afectar `PendingQuantity`: reduce atómicamente `PendingQuantity` y `TotalQuantity` en la misma cantidad en que aumenta `R`; nunca afecta `InPreparationQuantity`, `ReadyQuantity` ni `DeliveredQuantity`. Las correcciones de Preparation sobre trabajo iniciado siguen pendientes.
- La corrección ordinaria no cambia el Importe funcional, que continúa derivándose de Delivery efectiva. Sí puede cambiar la elegibilidad de Liquidation. Está prohibida después de Freeze.
- El frontend expone Content Correction explícita con esa identidad exacta, maneja incertidumbre/retry y refresca desde el Estado autoritativo. La lectura de Preparation expone `ContentOrdinal` para esa correlación. El recorrido vertical está cubierto por E2E.
- Cancellation, cantidad de Cancellation y Estado de precio efectivo no existen todavía. Sus límites futuros viven en [pendientes](pending.md).
- `PreparationWork.TotalQuantity > 0` permanece vigente. La base Q/R/F no habilita Work de total cero ni decide una futura transición para ese caso.

S7-I3D está verticalmente implementado y verificado: backend 741/741, OrderOperations 424/424, frontend 265/265 y Playwright 9/9. Evidencia de creación/modelo: [OrderModel](../../backend/src/NexoBar.OrderOperations/OrderModel.cs) y [ConfirmedContentFactory](../../backend/src/NexoBar.OrderOperations/ConfirmedContentFactory.cs). La [migración y sus protecciones](migrations.md#base-qrf-s7-i2) son parte de S7-I2.

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
