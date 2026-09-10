# Preparation

## PreparationWork, progreso y consulta autorizada

`PreparationWork` es Estado operacional vigente poseído por `OrderOperations` y contiene:

- `Id` UUID v7;
- `IncorporationId`;
- `ContentOrdinal`;
- `PreparationResponsibilityId`;
- `TotalQuantity`;
- `PendingQuantity`;
- `InPreparationQuantity`;
- `ReadyQuantity`.

Cada Work tiene PK por `Id` y una FK compuesta `(incorporation_id, content_ordinal)` hacia `IncorporationContent`, con exactamente un Work por Content preparado. No duplica `ProductId`: Product e instruction se obtienen mediante el Content. No tiene FK hacia `OperationalConfiguration`.

Una Confirmación puede crear varios Work para el mismo Product cuando pertenecen a Contents con instruction diferente. Cada Work conserva el snapshot de responsabilidad y las cantidades `total`, `pending`, `inPreparation` y `ready`. No existe `WorkCreated History`.

Las cantidades son actualmente enteros exactos. Según [Q/R/C/F, Content Correction y Content Cancellation ordinarias](confirmation.md#q-r-c-y-f-s7-i2-content-correction-ordinaria-s7-i3d-content-cancellation-ordinaria-s7-i4d), al confirmar `R = 0`, `C = 0`, `total = F = Q`, `pending = total`, `inPreparation = 0` y `ready = 0`. Correction y Cancellation ordinarias de un Content preparado solo reducen `PendingQuantity` y `TotalQuantity` de forma atómica, respectivamente al incrementar `R` o `C`; no afectan `InPreparationQuantity`, `ReadyQuantity` ni `DeliveredQuantity`. Lectura y progreso validan `TotalQuantity = F`; State faltante o incoherencia con la obligación vigente son inconsistencias. El Estado autoritativo satisface:

```text
TotalQuantity >= 0
PendingQuantity >= 0
InPreparationQuantity >= 0
ReadyQuantity >= 0
PendingQuantity + InPreparationQuantity + ReadyQuantity == TotalQuantity
```

El Work nace con `TotalQuantity > 0`. Puede quedar en cero solo cuando Cancellation ordinaria consume todo su bucket Pending y lleva `F` a cero; no habilita Cancellation ni intervención sobre cantidad `InPreparation` o `Ready`.

No existe un `Status` persistido. Son derivaciones, no columnas:

- completamente listo: `ReadyQuantity == TotalQuantity`;
- hay trabajo activo: `InPreparationQuantity > 0`;
- todavía queda preparación: `PendingQuantity + InPreparationQuantity > 0`.

Un mismo Work puede contener simultáneamente porciones `Pending`, `InPreparation` y `Ready`; por ejemplo, `total = 5`, `pending = 1`, `inPreparation = 1`, `ready = 3` es válido. No existe identidad física por unidad ni sub-items individuales. Todas las cantidades de un Work comparten `IncorporationContent`, Product, instruction y el snapshot de `PreparationResponsibility`.

La consulta materializada es:

```text
GET /api/order-operations/preparation/work
    ?preparationResponsibilityId=...
```

- El parámetro `preparationResponsibilityId` es obligatorio y debe ser un UUID válido; ausencia o formato inválido responde `400`.
- La consulta requiere una Session válida, una Identity activa, `Responsibility.Preparation` y la `PreparationEnablement` exacta. Authentication/Session/Identity no utilizable responde `401`; ausencia de Preparation o de la habilitación exacta responde un `403` común que no revela cuál falta.
- La autorización consulta Estado vigente dentro de la misma transacción PostgreSQL física que la lectura de Work y usa `FOR SHARE` sobre el Estado positivo; no usa capability claims.
- Una consulta autorizada sin Work devuelve `200 []`.
- Cada respuesta incluye `workId`, `preparationResponsibilityId`, `operationalReference` opaca, `context` vigente, `incorporationId`, `incorporationOrdinal`, `contentOrdinal`, `productId`, `productOperationalName`, `instruction` nullable, `TotalQuantity`, `PendingQuantity`, `InPreparationQuantity`, `ReadyQuantity`, `DeliveredQuantity` y `confirmedAt`. `DeliveredQuantity` se incorpora a este read para calcular el límite de Ready Correction sin exigir autorización sobre Delivery. `contentOrdinal`, `productId` e `instruction` proceden de `IncorporationContent`; `(incorporationId, contentOrdinal)` permite al frontend correlacionar exactamente Content Correction.
- `context` procede del `Order` actual. `confirmedAt` procede de la Confirmación que originó la `Incorporation`; no existe un `createdAt` artificial.
- `ProductId` sigue siendo la identidad autoritativa. `productOperationalName` es presentación **actual/vigente** obtenida mediante una capacidad batch estrecha de `Catalog` que entrega solo `ProductId + OperationalName`; no es un snapshot de nombre en Confirmation o Work. Renombrar un Product cambia la presentación futura del Work activo, sin alterar `appliedPrice`, instruction, Preparation Responsibility, cantidades ni Historia. Los Products retirados continúan resolviéndose. Una referencia faltante es inconsistencia técnica y no cae a mostrar el UUID.
- Esta decisión no agregó migración ni snapshot de nombre.
- El lookup de `Order` permanece separado y no incorpora Work.

### Start parcial

```text
POST /api/order-operations/preparation/work/{workId}/start
Intent: StartPreparationQuantity
Body: { "quantity": integer }
Headers: Idempotency-Key UUID v4 + antiforgery
```

Una intención nueva exige `quantity > 0`, `PendingQuantity >= quantity` y autorización vigente. Su transición es:

```text
PendingQuantity -= quantity
InPreparationQuantity += quantity
```

No hay auto clipping, transición directa `Pending -> Ready`, ownership ni flag de Start del Work completo. Abrir o leer un Work no inicia ninguna cantidad.

### Ready parcial

```text
POST /api/order-operations/preparation/work/{workId}/ready
Intent: MarkPreparationQuantityReady
Body: { "quantity": integer }
Headers: Idempotency-Key UUID v4 + antiforgery
```

Una intención nueva exige `quantity > 0`, `InPreparationQuantity >= quantity` y autorización vigente. Su transición es:

```text
InPreparationQuantity -= quantity
ReadyQuantity += quantity
```

Ready es parcial. Cuando `ReadyQuantity == TotalQuantity`, el Work está completamente listo por derivación; no se emite un evento adicional `WorkCompleted`. Ready no implica Delivered, Completed ni cierre del Order.

### Preparation Correction ordinaria

Preparation Progress Correction está implementada verticalmente mediante dos comandos explícitos: Correct Start y Correct Ready.

Preparation Correction significa que el progreso de Preparation registrado fue erróneo. No cambia una obligación que era correcta y después dejó de ser requerida; ese caso corresponde a OperationalIntervention, que permanece pendiente. Tampoco es Content Correction, Content Cancellation ni Delivery Correction.

La corrección es por cantidad sobre el Estado actual del Work. No selecciona ni referencia un evento histórico Start o Ready original. La Historia original de Start/Ready permanece preservada y cada Correction registra su propia Historia semántica distinta, sin requerir una referencia al evento original.

Una Start Correction exige una cantidad entera positiva exacta `x <= InPreparationQuantity` y revierte únicamente el registro erróneo de Start:

```text
InPreparationQuantity -= x
PendingQuantity += x
```

`ReadyQuantity`, `DeliveredQuantity`, `TotalQuantity`, `Q/R/C/F`, Functional Amount, Content y destination no cambian.

Una Ready Correction exige una cantidad entera positiva exacta `x <= ReadyQuantity - DeliveredQuantity` y revierte únicamente el registro erróneo de Ready:

```text
ReadyQuantity -= x
InPreparationQuantity += x
```

`PendingQuantity`, `DeliveredQuantity`, `TotalQuantity`, `Q/R/C/F`, Functional Amount, Content y destination no cambian. El límite preserva `DeliveredQuantity <= ReadyQuantity`; una Preparation Correction no retrae ni cancela Delivery.

Si Ready y Start fueron registrados erróneamente, se ejecutan dos comandos explícitos y ordenados: primero Ready Correction (`Ready -> InPreparation`) y después Start Correction (`InPreparation -> Pending`). No existe una Correction directa `Ready -> Pending`, cascada ni reversión automática de múltiples transiciones históricas.

### Autorización, actores y concurrencia

No existe owner ni assignee de `PreparationWork`. Cualquier Identity actualmente autorizada para el destino puede actuar sobre cantidad elegible. Una intención nueva requiere Session válida, Identity activa, `Responsibility.Preparation` vigente y la `PreparationEnablement` exacta para `Work.PreparationResponsibilityId`. El actor procede de `AuthenticatedContext.IdentityId` y el destino se obtiene del Work; el cliente no aporta actor, destination ni capability. No se usan claims de capability.

Por tanto, es válido que Identity A ejecute `Start(1)` e Identity B ejecute `Ready(1)` si ambas satisfacen la autorización vigente. Preparation Correction usa la misma autorización: cualquier Identity activa con `Responsibility.Preparation` y la `PreparationEnablement` exacta puede corregir cantidad elegible; no queda restringida al actor que registró el progreso original. La Historia conserva el actor de cada acción.

Start y Ready comparten este patrón de transacción corta:

```text
BEGIN READ COMMITTED
→ advisory transaction lock por Idempotency-Key
→ estabilización de Session + Identity
→ replay/conflicto de Preparation command
→ Responsibility.Preparation FOR SHARE
→ Order FOR UPDATE
→ Work FOR UPDATE
→ destination obtenido del Work
→ exact PreparationEnablement FOR SHARE
→ validación de Freeze y coherencia de Content / ContentQuantityState / DeliveryState con Work
→ transición de dominio
→ History
→ command/result durable
→ COMMIT
```

No hay locks de Work de larga duración, distributed lock, lock global de Preparation ni ownership claim. Dos preparadores pueden actuar concurrentemente sobre el mismo Work; `Work FOR UPDATE` serializa las mutaciones y cada intención se valida contra el Estado estabilizado. Con `Pending = 5`, dos `Start(2)` pueden confirmar secuencialmente. Con `Pending = 3`, solo uno confirma y el otro obtiene `409`; la segunda intención no se recorta. Se aplica el mismo criterio a Ready y a Preparation Correction. Start, Ready y sus Corrections revalidan el bucket fuente; Ready Correction además revalida `ReadyQuantity - DeliveredQuantity`. Todas quedan prohibidas después de Liquidation/Freeze.

### Errores de progreso

- `400`: key, body, `workId` o `quantity` inválidos;
- `401`: Session no utilizable o Identity inactiva;
- `403`: falta `Responsibility.Preparation`;
- `404` indistinguible: Work inexistente o falta la `PreparationEnablement` exacta;
- `409`: bucket fuente insuficiente, conflicto de idempotencia o transición no aplicable;
- `500`: Estado inconsistente, incluido ContentQuantityState faltante o cantidades incoherentes; lectura y progreso exponen `order_operations.preparation.state_inconsistent`.

El `404` no revela el destination ni permite distinguir pérdida de habilitación de Work inexistente.

Los [destinos de Preparation de la Identity actual](../identities-and-capabilities/security.md#destinos-de-preparation-de-la-identity-actual) pertenecen a IdentitiesAndCapabilities. Para Historia y matching de replay, leer [contratos e Historia](contracts-and-history.md); para Freeze y pendientes, [terminación](ending.md) y [fronteras abiertas](pending.md).
