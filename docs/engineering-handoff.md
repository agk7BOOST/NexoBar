# NexoBar - Engineering Handoff

## 1. Propósito y autoridad

- Este documento es el baseline técnico actual, versionado y materializado del repositorio.
- No reemplaza las Sources normativas de comportamiento funcional, modelo conceptual, UX, RNF o arquitectura. Ante una discrepancia prevalece la Source aplicable.
- Las decisiones posteriores aprobadas o Adendas sustituyen únicamente el asunto concreto que modifican. Lo explícitamente abierto, parametrizado o provisional continúa abierto.

## 2. Plataforma, repositorio y módulos

- Monorepo Git con backend y frontend como unidades técnicas separadas.
- Backend autoritativo: C# sobre .NET 10 LTS y ASP.NET Core 10. Frontend: React 19, TypeScript estricto y Vite 8.
- El backend es un monolito modular y una unidad principal de despliegue. `NexoBar.Host` compone los módulos y es el composition root.
- Los módulos superiores son `OrderOperations`, `Catalog`, `Inventory`, `IdentitiesAndCapabilities` y `OperationalConfiguration`. Los cinco están materializados; `Inventory` posee su Estado, operaciones físicas, Historia y superficies web de configuración y operación, mientras `IdentitiesAndCapabilities` posee Estado, sesiones, administración y capacidades públicas de autorización.
- `OperationalConfiguration` ya es un módulo persistente y funcional en el alcance de `PreparationResponsibility`; no tiene todavía un lifecycle completo.
- `Preparation` y `Delivery` son fronteras internas de `OrderOperations`, no módulos top-level. Son dimensiones distintas: `Ready != Delivered`.
- Cada módulo conserva la propiedad de su Estado y colabora mediante capacidades explícitas. No hay ciclos ni un `Shared`/`Common` genérico.

### Dependencias modulares materializadas

```text
Host
├─ OrderOperations
├─ Catalog
├─ Inventory
├─ OperationalConfiguration
└─ IdentitiesAndCapabilities

OrderOperations ──→ Catalog
OrderOperations ──→ IdentitiesAndCapabilities
Catalog ──────────→ OperationalConfiguration
Inventory ────────→ IdentitiesAndCapabilities
IdentitiesAndCapabilities ──→ OperationalConfiguration
```

- `Catalog` consume una capacidad pública estrecha de `OperationalConfiguration` para validar la existencia de una `PreparationResponsibility`; no accede a su `DbContext`, schema ni tablas.
- `OrderOperations` depende de `Catalog` y de capacidades públicas estrechas de `IdentitiesAndCapabilities`; no depende directamente de `OperationalConfiguration` ni lo consulta durante una Confirmación.
- `IdentitiesAndCapabilities` consume una capacidad pública estrecha de `OperationalConfiguration` para resolver o validar destinos de Preparation.
- `Inventory` consume capacidades públicas estrechas de `IdentitiesAndCapabilities` para estabilizar autorización y resolver el nombre operacional vigente de los actores de Movimientos; no accede a su `DbContext`, schema ni tablas.
- No existen las dependencias inversas `OperationalConfiguration → IdentitiesAndCapabilities`, `Catalog → OrderOperations` ni `IdentitiesAndCapabilities → OrderOperations`.
- No existen foreign keys ni accesos a `DbContext`, schema o tablas ajenos cross-module. El Host compone las capacidades y sus implementaciones.

## 3. Persistencia

- Una instancia/base PostgreSQL compartida es la persistencia relacional transaccional primaria. EF Core 10 y Npgsql son la estrategia predeterminada.
- Cada módulo que persiste Estado posee su propio `DbContext`, schema e historial de migraciones:
  - `CatalogDbContext` mapea `catalog`;
  - `OrderOperationsDbContext` mapea `order_operations`;
  - `InventoryDbContext` mapea `inventory`;
  - `OperationalConfigurationDbContext` mapea `operational_configuration`;
  - `IdentitiesAndCapabilitiesDbContext` mapea `identities_and_capabilities`.
- Las migraciones son explícitas, versionadas y revisables. El Host productivo no ejecuta auto-migrate durante el startup.
- Dinero y las cantidades exactas fraccionarias materializadas usan `numeric`/`decimal`, nunca coma flotante binaria como representación autoritativa; cuando aplica, su representación HTTP es un decimal string estable. Las cantidades actuales de `PreparationWork` son enteros exactos; las cantidades fraccionarias de Preparation permanecen abiertas.
- Las identidades persistentes principales materializadas usan UUID v7. `Idempotency-Key` usa UUID v4.
- Los timestamps operacionales autoritativos los asigna el backend y se persisten en UTC.
- Estado vigente e Historia semántica son conceptos distintos y se confirman atómicamente cuando son consecuencias inseparables. La solución no es CQRS ni Event Sourcing.
- SQL explícito puede usarse cuando una invariante, concurrencia o rendimiento lo justifique. No se usan Repository Pattern genérico ni lazy loading por defecto.

### Invariante de configuración

Las connection strings modulares de `Catalog`, `OrderOperations`, `Inventory`, `OperationalConfiguration` e `IdentitiesAndCapabilities` deben apuntar a la misma instancia y base PostgreSQL. Las colaboraciones transaccionales entre módulos dependen de ello. Actualmente es una invariante de configuración documentada, no una validación automatizada.

## 4. OperationalConfiguration

`OperationalConfiguration` posee `OperationalConfigurationDbContext`, el schema `operational_configuration` y migration history propia sobre la PostgreSQL primaria compartida.

El Estado materializado de `PreparationResponsibility` contiene:

- `Id` UUID v7;
- `OperationalName`;
- `NormalizedOperationalName`.

La API materializada es:

```text
POST /api/operational-configuration/preparation-responsibilities
GET  /api/operational-configuration/preparation-responsibilities
```

La creación es un comando explícito con `Idempotency-Key` UUID v4, idempotencia durable local, identidad UUID v7 asignada por backend, unicidad case-insensitive del nombre operacional y replay desde el resultado persistido.

No están materializados `IsActive`, retiro, reactivación, delete ni un lifecycle completo de `PreparationResponsibility`.

## 5. Identities & Capabilities

`IdentitiesAndCapabilities` posee `IdentitiesAndCapabilitiesDbContext`, el schema `identities_and_capabilities` y migration history propia. No adopta roles genéricos ni ASP.NET Identity completo.

### Identity, responsabilidades y habilitaciones

El Estado vigente de `Identity` contiene:

- `Id` UUID v7;
- `OperationalName` y `NormalizedOperationalName`;
- `IsActive`.

`OperationalName` no es unique. `FunctionalResponsibility` es un repertorio cerrado de siete códigos:

```text
OrderOperationsAndBasicClosure
OperationalIntervention
Preparation
CatalogConfiguration
InventoryOperation
InventoryConfiguration
GeneralConfiguration
```

`ResponsibilityAssignment` tiene PK compuesta `(IdentityId, ResponsibilityCode)`. `PreparationEnablement` tiene PK compuesta `(IdentityId, PreparationResponsibilityId)`; el destino es un UUID externo opaco, sin FK cross-module, y su vigencia se representa por existencia. La habilitación es independiente de la asignación `Preparation`: preparar o actuar sobre un destino requiere simultáneamente una Identity activa, `Responsibility.Preparation` y la `PreparationEnablement` exacta.

### Credencial local

`LocalCredential` está separada de `Identity` en una relación 1:1 (como máximo una credencial por Identity). Contiene `LoginIdentifier`, `NormalizedLoginIdentifier` unique y `SecretVerifier`; el locator no exige email. El login identifier aplica trim exterior y normalización invariant de case. El secret no se normaliza.

`SecretVerifier` es un wrapper estrecho sobre `PasswordHasher<T>`, admite rehash cuando el verificador lo requiere y nunca persiste el secret crudo.

### Sesiones opacas y política provisional

`IdentitySession` contiene `Id` UUID v7, `IdentityId`, `TokenHash`, `CreatedAt`, `LastActivityAt`, `AbsoluteExpiresAt` y `RevokedAt` nullable. El token entregado al cliente usa 256 bits de RNG y Base64URL; solo se persiste su SHA-256, nunca el token crudo. Una Identity puede mantener varias sesiones.

Logout revoca únicamente la sesión actual. Desactivar una Identity invalida su autoridad y revoca sus sesiones activas. Set/Replace Local Credential conserva la Identity y sus capacidades, y revoca todas las sesiones del target.

La implementación usa como **hipótesis técnica provisional**, no como requisito normativo: inactividad máxima de 30 minutos, lifetime absoluto de 12 horas, expiración exacta cuando `now >= límite`, refresh throttled de `LastActivityAt` aproximadamente cada minuto y `AbsoluteExpiresAt` fijado al crear la sesión. `PAR-SEC-02` continúa abierto normativamente. El frontend no duplica timers; el backend es la autoridad de expiración.

### Cookie, antiforgery y contratos de sesión

En configuración production-like, la cookie de sesión es `__Host-nexobar-session`, `HttpOnly`, `Secure`, `SameSite=Strict`, `Path=/`, sin `Domain` y sin `Max-Age`/`Expires`: es una session cookie. Development y tests usan nombre y configuración explícitos compatibles con HTTP local y no simulan el prefijo `__Host-` cuando `Secure=false`.

`GET /api/security/antiforgery` es un endpoint técnico anónimo que entrega el request token para el header `X-NexoBar-CSRF`; no crea una sesión. El frontend conserva ese token solo en memoria. El session token nunca está disponible a JavaScript ni se guarda en `localStorage` o `sessionStorage`.

Los contratos materializados son:

- `POST /api/identity-sessions`: recibe `loginIdentifier` y `secret`, requiere antiforgery, responde un `401` genérico `invalid_credentials` cuando las credenciales no pueden usarse, revoca solo la sesión actual que ya estuviera representada por el cookie jar al reemplazarla, crea una sesión nueva y no afecta otras sesiones;
- `GET /api/identity-sessions/current`: autenticado; devuelve solo `identityId` y `operationalName`, sin `sessionId`, capabilities ni token;
- `DELETE /api/identity-sessions/current`: requiere antiforgery, revoca la sesión actual, limpia la cookie y devuelve `204`; es idempotente según la implementación actual;
- `GET /api/security/antiforgery`: anónimo y puramente técnico.

El pipeline de autenticación es:

```text
request
→ cookie
→ SHA-256
→ lookup de IdentitySession
→ RevokedAt
→ expiración absoluta
→ inactividad
→ Identity.IsActive
→ principal/contexto del request
```

`AuthenticatedContext` contiene únicamente `IdentityId` y `SessionId`. No hay responsibility claims, enablement claims, roles ni snapshots de capabilities en la sesión.

### Estabilización transaccional de autorización

Para operaciones que requieren autoridad estabilizada, el módulo caller abre una transacción PostgreSQL, `IdentitiesAndCapabilities` adopta su `DbTransaction`, revalida y bloquea selectivamente Session e Identity y, cuando corresponde, filas de capabilities; luego el caller ejecuta la consulta o mutación autorizada. Se usa `READ COMMITTED`, transacciones cortas y `FOR SHARE` en el Estado positivo materializado. No hay transacción distribuida.

## 6. Administración de Identity

El backend implementa intenciones específicas para Create Identity, Change Operational Name, Activate, Deactivate, Set/Replace Local Credential, Assign/Revoke Functional Responsibility y Grant/Revoke Preparation Enablement. No expone CRUD genérico ni Delete Identity.

```text
POST /api/identities
POST /api/identities/{identityId}/change-operational-name
POST /api/identities/{identityId}/activate
POST /api/identities/{identityId}/deactivate
POST /api/identities/{identityId}/credential
POST /api/identities/{identityId}/responsibilities/{code}/assign
POST /api/identities/{identityId}/responsibilities/{code}/revoke
POST /api/identities/{identityId}/preparation-enablement/{responsibilityId}/grant
POST /api/identities/{identityId}/preparation-enablement/{responsibilityId}/revoke
```

`GET /api/identities` requiere `GeneralConfiguration` y devuelve Estado de Identity, capabilities, enablements y login identifier; no devuelve verifier, tokens ni detalles de sesiones.

La administración ordinaria debe preservar al menos un camino operacional vigente de `GeneralConfiguration`. La definición técnica actual del camino es: Identity activa + assignment `GeneralConfiguration` + `LocalCredential` utilizable. No requiere una sesión activa. Un advisory lock estable, transaction-scoped, serializa las mutaciones administrativas relevantes y evita carreras de revocación/desactivación que dejen cero caminos. Esto no implementa recovery extraordinario: `AD-SEC-01` continúa pendiente productivamente.

Los comandos administrativos durables usan `Idempotency-Key` UUID v4 y persisten `ActorIdentityId`, command kind, fingerprint estructural y result payload. La misma combinación actor/key/intención hace replay; una key reutilizada con actor o intención diferentes produce conflicto. `SessionId` no es actor durable.

El replay exige todavía una sesión actual válida y una Identity activa. Si el resultado ya fue confirmado, se reproduce antes de revalidar `GeneralConfiguration`: revocar una capability después del éxito no reinterpreta el efecto histórico de ese comando. Para una intención de credencial, la comparación durable guarda un verifier lento de la intención y usa el mismo verificador de secretos; no guarda el secret crudo ni un digest rápido sin salt. Estos registros técnicos de comandos no equivalen a Historia funcional.

## 7. Catalog y configuración de preparación

El Estado vigente de `Product` incluye `requiresPreparation` y `preparationResponsibilityId` nullable, con la invariante física y de aplicación:

```text
RequiresPreparation = false ↔ PreparationResponsibilityId = null
RequiresPreparation = true  ↔ PreparationResponsibilityId = UUID
```

`PreparationResponsibilityId` es una referencia externa y lógica; no existe FK física desde `Catalog` hacia `OperationalConfiguration`.

La mutación materializada es el comando explícito:

```text
POST /api/catalog/products/{productId}/preparation-configuration-changes
```

Su semántica es:

- `null → UUID`: habilitar preparación;
- `UUID A → UUID B`: reasignar prospectivamente;
- `UUID → null`: deshabilitar prospectivamente;
- `null → null`: no-op válido cuando el valor esperado coincide.

El comando recibe `expectedCurrentPreparationResponsibilityId` y realiza un `UPDATE` condicionado atómico con comparación nullable; no aplica last-write-wins. Mantiene idempotencia durable local y resuelve el replay antes de consultar `OperationalConfiguration`. Para una intención nueva cuyo destino no es `null`, `Catalog` usa la capacidad pública estrecha de existencia de `PreparationResponsibility`.

Price Change continúa siendo otro comando explícito de `Catalog`, no un `PATCH` genérico: recibe `expectedCurrentPrice`, ejecuta un `UPDATE` condicionado, responde `409 Conflict` ante una expectativa desactualizada, mantiene idempotencia durable local y no crea Price History ni reescribe `appliedPrice` históricos.

## 8. Inventory mínimo operativo

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

## 9. Colaboración `OrderOperations -> Catalog` y concurrencia

- `IOrderConfirmationCatalog` es la capacidad pública mínima de Confirmación. `Catalog` conserva la propiedad de su Estado; `OrderOperations` no accede a `CatalogDbContext` ni a tablas `catalog.*`.
- Su snapshot de `Product` incluye `ProductId`, `Price`, `IsActive`, `IsAvailable`, `RequiresPreparation` y `PreparationResponsibilityId`.
- Todos esos campos se leen bajo el mismo `FOR SHARE`, dentro de la transacción modular existente. `Catalog` reutiliza la conexión y transacción PostgreSQL de `OrderOperations`.
- Exponer `DbTransaction` en esa interfaz es una excepción técnica deliberada por atomicidad y estabilización; no establece un patrón genérico para todas las colaboraciones entre módulos.

El guardrail de concurrencia materializado es:

```text
Confirmation                         Price Change / Preparation Configuration
Products FOR SHARE                   Product UPDATE incompatible
```

El orden efectivo serializa las operaciones. Una Confirmación que estabilizó primero un `Product` conserva como snapshot aplicado `appliedPrice`, `RequiresPreparation` y `PreparationResponsibilityId`, aunque una mutación concurrente espere y se aplique después. El precio queda en `IncorporationContent`; la decisión de crear Work y su responsabilidad proceden de ese mismo snapshot.

Una Confirmación deduplica los `ProductId`, estabiliza una sola vez cada `Product` distinto mediante `Catalog` y reutiliza ese snapshot para todas sus líneas. Por tanto, todas las líneas del mismo Product dentro de una Confirmación comparten coherentemente `appliedPrice`, `RequiresPreparation` y `PreparationResponsibilityId`, sin alterar el lock ordering ni la atomicidad existentes.

Si el Product estabilizado tiene `RequiresPreparation = false` y una línea contiene `instruction != null`, la Confirmación responde `409 Conflict` con `order_operations.confirmation.instruction_requires_preparation` y no produce efectos. La instrucción no se descarta, no fuerza Preparation y no crea Work por sí sola. Si una Confirmación estabiliza primero un Product preparado y luego espera una configuración concurrente `true → false`, puede completar y su Work conserva el snapshot anterior; si `false` ya estaba aplicado al estabilizar, la Confirmación falla con ese `409`.

## 10. Confirmaciones, Incorporations y nacimiento de Work

La primera Confirmación crea el `Order` y su primera `Incorporation`; cada Confirmación posterior crea una nueva `Incorporation` del mismo `Order`. Ambas aceptan Products preparados.

- La mutación sobre un `Order` existente expresa la intención de una nueva Confirmación. La `operationalReference` es opaca; el request contiene solo items y no modifica el `Context` del `Order`.
- Cada Confirmación posterior exitosa crea una `Incorporation` con ordinal sucesivo, una nueva `ConfirmationHistory` y contenido con el `appliedPrice` vigente estabilizado para esa Confirmación.

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
→ Products FOR SHARE
→ Order / Incorporation / Content / Work condicional / DeliveryState / History / command
→ commit

Subsequent Confirmation
advisory idempotency
→ Order FOR UPDATE
→ Products FOR SHARE
→ Incorporation / Content / Work condicional / DeliveryState / History / command
→ commit
```

- En Confirmaciones posteriores, `Order FOR UPDATE` serializa la asignación del ordinal `MAX + 1`; la restricción única `(order_id, ordinal)` es la defensa física adicional.
- Un factory interno estrecho y común a First y Subsequent centraliza la creación de `IncorporationContent`, la creación condicional de `PreparationWork` y la creación de `DeliveryState`; no es un framework ni un pipeline genérico.
- Todo Content crea atómicamente `DeliveryState(0)` durante Confirmation. Un Direct Content crea Content + ningún Work + DeliveryState; un Prepared Content crea Content + un Work + DeliveryState. El replay no duplica ninguno de ellos.
- La responsabilidad aplicada al Work procede del snapshot estabilizado del `Product`. Los cambios posteriores de configuración del `Product` no modifican Work existente.
- No existe un evento `WorkCreated`.

### Replay de Confirmación

- El replay se resuelve antes de consultar `Catalog` y reconstruye la respuesta desde la persistencia original de `OrderOperations`; en una Confirmación posterior tampoco bloquea el `Order`.
- No crea nuevos Work y no requiere `workId` en las tablas de comandos.
- El nacimiento de Work no introduce una idempotencia adicional: queda cubierto por la idempotencia y transacción de la Confirmación que lo origina.

## 11. PreparationWork, progreso y consulta autorizada

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

Las cantidades son actualmente enteros exactos. Las cantidades iniciales son `total = confirmed quantity`, `pending = total`, `inPreparation = 0` y `ready = 0`. El Estado autoritativo satisface:

```text
TotalQuantity > 0
PendingQuantity >= 0
InPreparationQuantity >= 0
ReadyQuantity >= 0
PendingQuantity + InPreparationQuantity + ReadyQuantity == TotalQuantity
```

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
- Cada respuesta incluye `workId`, `preparationResponsibilityId`, `operationalReference` opaca, `context` vigente, `incorporationId`, `incorporationOrdinal`, `productId`, `productOperationalName`, `instruction` nullable, las cuatro cantidades y `confirmedAt`. `productId` e `instruction` proceden de `IncorporationContent`.
- `context` procede del `Order` actual. `confirmedAt` procede de la Confirmación que originó la `Incorporation`; no existe un `createdAt` artificial.
- `ProductId` sigue siendo la identidad autoritativa. `productOperationalName` es presentación **actual/vigente** obtenida mediante una capacidad batch estrecha de `Catalog` que entrega solo `ProductId + OperationalName`; no es un snapshot de nombre en Confirmation o Work. Renombrar un Product cambia la presentación futura del Work activo, sin alterar `appliedPrice`, instruction, Preparation Responsibility, cantidades ni Historia. Los Products retirados continúan resolviéndose. Una referencia faltante es inconsistencia técnica y no cae a mostrar el UUID.
- Esta decisión no agregó migración ni snapshot de nombre.
- El lookup de `Order` permanece separado y no incorpora Work.

### Destinos de Preparation de la Identity actual

`GET /api/identity-sessions/current/preparation-destinations` devuelve únicamente las habilitaciones de la Identity actual como `preparationResponsibilityId + operationalName`. Requiere autenticación, Identity activa y `Responsibility.Preparation`; Preparation sin habilitaciones devuelve `200 []`.

La resolución de nombres usa un batch lookup estrecho de `OperationalConfiguration`; no concede lectura universal de ese módulo.

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

### Autorización, actores y concurrencia

No existe owner ni assignee de `PreparationWork`. Cualquier Identity actualmente autorizada para el destino puede actuar sobre cantidad elegible. Una intención nueva requiere Session válida, Identity activa, `Responsibility.Preparation` vigente y la `PreparationEnablement` exacta para `Work.PreparationResponsibilityId`. El actor procede de `AuthenticatedContext.IdentityId` y el destino se obtiene del Work; el cliente no aporta actor, destination ni capability. No se usan claims de capability.

Por tanto, es válido que Identity A ejecute `Start(1)` e Identity B ejecute `Ready(1)` si ambas satisfacen la autorización vigente. La Historia conserva el actor de cada acción; quien inició una cantidad no necesita ser quien la marca Ready.

Start y Ready comparten este patrón de transacción corta:

```text
BEGIN READ COMMITTED
→ advisory transaction lock por Idempotency-Key
→ estabilización de Session + Identity
→ replay/conflicto de Preparation command
→ Responsibility.Preparation FOR SHARE
→ Work FOR UPDATE
→ destination obtenido del Work
→ exact PreparationEnablement FOR SHARE
→ transición de dominio
→ History
→ command/result durable
→ COMMIT
```

No hay locks de Work de larga duración, distributed lock, lock global de Preparation ni ownership claim. Dos preparadores pueden actuar concurrentemente sobre el mismo Work; `Work FOR UPDATE` serializa las mutaciones y cada intención se valida contra el Estado estabilizado. Con `Pending = 5`, dos `Start(2)` pueden confirmar secuencialmente. Con `Pending = 3`, solo uno confirma y el otro obtiene `409`; la segunda intención no se recorta. Se aplica el mismo criterio a Ready. Start y Ready concurrentes también se serializan coherentemente por Work.

### Errores de progreso

- `400`: key, body, `workId` o `quantity` inválidos;
- `401`: Session no utilizable o Identity inactiva;
- `403`: falta `Responsibility.Preparation`;
- `404` indistinguible: Work inexistente o falta la `PreparationEnablement` exacta;
- `409`: bucket fuente insuficiente, conflicto de idempotencia o transición no aplicable.

El `404` no revela el destination ni permite distinguir pérdida de habilitación de Work inexistente.

## 12. Delivery mínima operativa

Delivery pertenece a `OrderOperations`; no es un módulo top-level. Delivery y Preparation son dimensiones distintas. Entregar no decrementa ni modifica `PendingQuantity`, `InPreparationQuantity` o `ReadyQuantity`, y `Ready != Delivered`. Delivery tampoco implica Liquidation, Payment ni Closure.

### DeliveryState y clasificación

`DeliveryState` mantiene una relación técnica 1:1 con `IncorporationContent`; ambos comparten la identidad `(IncorporationId, ContentOrdinal)`. El único Estado persistido propio es `DeliveredQuantity : int`, inicialmente `0`. No existen `DeliveryId` UUID, `DeliveryWork`, `Delivery Status`, `IsDelivered` persistido, `Remaining` persistido, `Deliverable` persistido ni una copia de `Ready`.

La clasificación histórica usa `IncorporationContent.RequiresPreparationAtConfirmation` y valida su coherencia con `PreparationWork`:

- prepared: flag `true` y Work presente;
- direct: flag `false` y Work ausente;
- cualquiera de las dos combinaciones contrarias es inconsistencia técnica.

Catalog vigente no participa de esta clasificación.

### Read model autorizado

```text
GET /api/order-operations/orders/{operationalReference}/delivery
```

La consulta exige Session válida, Identity activa y `OrderOperationsAndBasicClosure` vigente. No requiere `Preparation` ni `PreparationEnablement`. Session/Identity no utilizable responde `401`, Responsibility faltante responde `403` y Order inexistente responde `404`.

La lectura abre una transacción corta `READ COMMITTED`, estabiliza Session, Identity y Responsibility, y obtiene el Estado de `OrderOperations` mediante una proyección coherente. No usa `FOR UPDATE` para la lectura ordinaria. Después resuelve en batch el `ProductOperationalName` vigente mediante la capacidad estrecha de Catalog, reutilizando la transacción; no existe SQL ni acceso a `DbContext` cross-module.

El contrato relevante por Content contiene:

- `incorporationId`;
- `incorporationOrdinal`;
- `contentOrdinal`;
- `productId`;
- `productOperationalName`;
- `instruction`;
- `totalQuantity`;
- `requiresPreparationAtConfirmation`;
- `readyQuantity` nullable;
- `deliveredQuantity`;
- `deliverableQuantity`;
- `remainingQuantity`.

No expone Status, WorkId, destination ni `appliedPrice`.

`ProductId` conserva la identidad autoritativa. `productOperationalName` es el nombre actual de Catalog, no un snapshot histórico: un rename posterior a Confirmation cambia la presentación de Delivery, y un Product inactive/retired continúa resolviéndose. Un Product referenciado que ya no existe es inconsistencia técnica y produce `500`; no existe fallback al UUID ni snapshot histórico del nombre en Delivery.

### Fórmulas y Delivery parcial

Para Prepared Content:

```text
total       = Content.Quantity
ready       = PreparationWork.ReadyQuantity
delivered   = DeliveryState.DeliveredQuantity
deliverable = ready - delivered
remaining   = total - delivered
```

Ready no disminuye al entregar. Por ejemplo, `total = 5`, `ready = 3`, `delivered = 2` produce `deliverable = 1`, `remaining = 3` y `ready` sigue siendo `3`.

Para Direct Content:

```text
total       = Content.Quantity
ready       = null / no aplicable
delivered   = DeliveryState.DeliveredQuantity
deliverable = total - delivered
remaining   = total - delivered
```

No se inventan `Ready = Total` ni un `PreparationWork` ficticio.

Delivery parcial está materializada: un Content puede tener `total = 5`, `delivered = 2`, `remaining = 3` sin identidad física por unidad, sub-items ni unidades físicas persistentes. Contents diferentes no se mezclan por `ProductId`.

### DeliverQuantity, autorización y transición

```text
POST /api/order-operations/incorporations/{incorporationId}/contents/{contentOrdinal}/deliver
Intent: DeliverQuantity
Body: { "quantity": integer }
Headers: Idempotency-Key UUID v4 + antiforgery
Target: (IncorporationId, ContentOrdinal)
```

El request no aporta `ProductId`, `WorkId`, actor, claim de Ready ni destination. Una intención nueva exige Session válida, Identity activa y `OrderOperationsAndBasicClosure` vigente; no exige Responsibility `Preparation` ni `PreparationEnablement`. Por ello una Identity puede entregar Prepared Content sin poder prepararlo. El actor procede exclusivamente de `AuthenticatedContext.IdentityId`.

Para Direct Content, `deliverable = Content.Quantity - DeliveredQuantity`. Para Prepared Content, `deliverable = ReadyQuantity - DeliveredQuantity`. En ambos casos la precondición es `0 < quantity <= deliverable` y la mutación es `DeliveredQuantity += quantity`; no hay clipping. La variante direct no crea un Ready ficticio y la prepared no modifica contadores de Preparation.

La transición está encapsulada en el método explícito `DeliveryState.Deliver(quantity, deliverableQuantity)`, que valida cantidad positiva, límite exacto e incremento. El service calcula la elegibilidad desde Content y, cuando corresponde, Work; `DeliveryState` no conoce Catalog ni el modelo completo de Preparation y no expone un setter genérico.

### Transacción, locks y concurrencia

Una intención nueva sigue este orden materializado:

```text
BEGIN READ COMMITTED
→ advisory transaction lock del namespace Delivery por Idempotency-Key
→ estabilización de Session + Identity
→ replay/conflicto de Delivery command
→ OrderOperationsAndBasicClosure FOR SHARE
→ IncorporationContent FOR SHARE
→ PreparationWork FOR UPDATE, si Prepared
→ DeliveryState FOR UPDATE
→ validación y transición
→ QuantityDelivered History
→ DeliveryCommand
→ COMMIT
```

Cuando intervienen los tres Estados, el orden es Content → PreparationWork → DeliveryState; no se invierte a DeliveryState → Work. No existen locks de larga duración, un lock global de Delivery ni espera automática hasta que exista Ready suficiente.

Ready y Delivery serializan sobre `PreparationWork` para Prepared Content. Si Ready bloquea primero, incrementa Ready y Delivery espera y observa el nuevo valor. Si Delivery bloquea primero, evalúa el Ready ya estabilizado, confirma o responde `409`, y Ready progresa después. Ambos órdenes conservan coherencia.

Direct y Prepared bloquean `DeliveryState FOR UPDATE`. En direct con `total = 5`, dos `Deliver(2)` pueden confirmar secuencialmente y dejar `Delivered = 4`; con `total = 3`, uno confirma y el otro responde `409`, sin clipping. Prepared aplica la misma semántica respecto de la cantidad Ready disponible.

### Historia, idempotencia y replay

Delivery confirma Historia separada de State mediante el evento `QuantityDelivered`, con:

- `HistoryId` UUID v7;
- `IncorporationId`;
- `ContentOrdinal`;
- `Quantity`;
- `ActorIdentityId`;
- `OccurredAt` UTC;
- `ResultingDeliveredQuantity`.

No guarda `SessionId`, Product name, instruction duplicada, snapshot de Ready, destination ni `appliedPrice`. No existen eventos redundantes `OrderDelivered` o `ContentCompleted`, y esta separación no constituye Event Sourcing.

`delivery_commands` persiste la intención comparable: `IdempotencyKey`, `ActorIdentityId`, `CommandKind = DeliverQuantity`, `IncorporationId`, `ContentOrdinal` y `Quantity`, además del resultado original. Mismo actor/key/intención hace replay; la misma key con actor o intención diferentes responde `409`. Delivery tiene namespace advisory propio, no reutiliza `preparation_commands` ni introduce un framework genérico de comandos.

El replay devuelve el resultado original persistido. Si key A dejó `delivered = 1`, key B dejó `delivered = 2` y luego se repite A, responde el `delivered = 1` original; no devuelve el Estado actual, no crea History y no muta Estado.

Un replay confirmado todavía exige Session utilizable e Identity activa, pero no reautoriza `OrderOperationsAndBasicClosure` después del efecto. Así, éxito con key X seguido de revocación de la Responsibility permite replay X con `200`, mientras una intención nueva con key Y recibe `403`. Identity desactivada o Session inválida produce `401`. El actor durable es `IdentityId`, nunca `SessionId`.

### Errores de Delivery

- `400`: key, body, quantity o target estructuralmente inválido;
- `401`: Session no utilizable o Identity inactiva;
- `403`: intención nueva sin `OrderOperationsAndBasicClosure`;
- `404`: Content target inexistente;
- `409`: cantidad entregable insuficiente, Content completamente entregado bajo una key nueva, conflicto de idempotencia u otro conflicto de dominio conocido;
- `500`: Estado inconsistente, incluido flag/Work contradictorio, `DeliveryState` faltante, `delivered > total`, Prepared `delivered > Ready` o inconsistencia estructural del Work.

No hay clipping ni auto-repair.

## 13. Estado e Historia

- `ConfirmationHistory` explica la Confirmación que originó el contenido y conserva `confirmedContext` histórico.
- `ConfirmationHistory` y el `IncorporationContent` persistido explican conjuntamente la existencia de una instruction confirmada; no existe `InstructionAdded History`.
- `PreparationWork` representa Estado operacional vigente y no se reconstruye ordinariamente desde Historia.
- `DeliveryState` representa Estado operacional vigente y `QuantityDelivered` explica cada incremento sin reconstruirlo ordinariamente desde Historia.
- La query de Preparation usa `Order.Context` vigente; no debe confundirse con `ConfirmationHistory.confirmedContext`.
- El progreso humano materializa Historia separada con los eventos `PreparationQuantityStarted` y `PreparationQuantityReady`. Cada registro conserva `HistoryId` UUID v7, `WorkId`, `Quantity`, `ActorIdentityId`, `OccurredAt` UTC y el resultado de las cuatro cantidades: `TotalQuantity`, `PendingQuantity`, `InPreparationQuantity` y `ReadyQuantity`.
- No existen eventos `WorkCreated`, `Progress` genérico ni `WorkCompleted`. La Historia de Preparation no conserva `SessionId`, snapshot de nombre del Product ni duplicación de instruction.
- No existe todavía query, API ni UI de Historia de Preparation.
- No existe todavía query ni UI de Historia de Delivery.
- Tampoco está materializado Change Context.
- Esta separación no constituye Event Sourcing.

## 14. Idempotencia y resultado incierto

- Los comandos materializados reciben un `Idempotency-Key` UUID v4 y mantienen persistencia durable por comando.
- Cada comando/módulo toma un advisory transaction lock local. Efecto e idempotencia se confirman dentro de la misma transacción.
- Misma key y misma intención produce replay; misma key e intención incompatible produce conflicto.
- Ante resultado incierto se reintenta con la misma key. No existe blind retry para mutaciones.
- `Catalog`, `OperationalConfiguration`, Primera Confirmación y Confirmación posterior tienen infraestructura y canonicalización propias; no deben uniformarse sin una decisión explícita.
- En una Confirmación posterior, la intención se define por `Order` e items canonicalizados; el orden del array no la altera.
- La duplicación local actual es deliberada. No existe infraestructura `Shared` de idempotencia.

Los comandos humanos de Preparation usan un namespace durable local de `OrderOperations`. La intención persistida contiene `IdempotencyKey` UUID v4, `ActorIdentityId`, `CommandKind`, `WorkId`, `Quantity` y un resultado estable. Los kinds actuales son `StartPreparationQuantity` y `MarkPreparationQuantityReady`.

- mismo actor/key/kind/work/quantity produce replay;
- la misma key con actor, kind, work o quantity incompatible produce `409`;
- `SessionId` no es actor durable;
- el replay exige Session válida e Identity activa, pero no vuelve a exigir la capability vigente cuando el efecto ya fue confirmado;
- el replay no duplica Estado ni Historia y devuelve el resultado original persistido, no el Estado posterior actual del Work.

Delivery usa su propio namespace y registro durable `delivery_commands`, con `DeliverQuantity` como único kind actual. Conserva actor, target `(IncorporationId, ContentOrdinal)`, quantity y resultado original; aplica las semánticas de replay y reautorización descritas en la sección 12. No reutiliza `preparation_commands`.

Los contenidos durables de los comandos First y Subsequent tienen PK `(idempotency_key, line_ordinal)` y persisten `product_id`, `quantity` e `instruction` canonical. `lineOrdinal` es técnico y canonical: se deriva después de ordenar las líneas semánticas y no depende del orden HTTP.

El matching de intención incluye `ProductId`, `Quantity` y canonical instruction, además del resto de la intención ya existente. La misma key con instruction diferente produce conflicto; whitespace o line endings equivalentes y distinto orden del array producen replay. El replay no reconsulta `Catalog`, no recrea Work y reproduce el estado persistido.

## 15. Contratos técnicos materializados

- HTTP ordinario usa HTTPS y JSON; ASP.NET Core Minimal APIs implementa endpoints con handlers delgados.
- Problem Details es la estructura común de errores e incluye códigos estables. OpenAPI describe el contrato técnico implementado, no sustituye su significado normativo.
- Las mutaciones expresan intenciones operacionales específicas, no reemplazos CRUD genéricos del Estado autoritativo.
- `operationalReference` es opaca en HTTP y OpenAPI, aunque actualmente derive internamente del Order ID.
- El lookup de Order se reconstruye exclusivamente desde `OrderOperations`; `Catalog` no reconstruye condiciones históricas.
- Las propiedades JSON autoritativas no reconocidas se rechazan en los comandos donde esta regla está materializada.

Los items de request de First y Subsequent contienen `productId`, `quantity` e `instruction` optional/nullable. Los items de la respuesta confirmada contienen `productId`, `quantity`, `appliedPrice` e `instruction`; el lookup de Order devuelve también `instruction` nullable por item. La consulta autorizada de Preparation Work devuelve `productOperationalName` vigente e `instruction` nullable, y obtiene `productId` e instruction desde Content. Delivery expone `contentOrdinal` únicamente junto con `incorporationId` como identidad técnica del target. Ningún contrato público expone `draftLineId`.

## 16. Frontend materializado

- `App` coordina `CatalogPanel`, `OrderWorkflow` y `OrderLookup`; las responsabilidades de catálogo/Price Change, Pedido activo/Composición y consulta están separadas. Un `Order` consultado puede retomarse para una Composición posterior.
- Los recursos se refrescan desde la autoridad: Price Change recarga `Catalog` y las Confirmaciones recargan el `Order`. El cliente no compone manualmente la Historia.
- La Composición usa `CompositionLine { draftLineId, productId, quantity, instruction }`. `draftLineId` se crea con `crypto.randomUUID()`, es estable mientras vive la línea y existe solo en frontend: no se envía, no pertenece al dominio y no es el `Idempotency-Key`.
- Cantidad `+/-`, remove e instruction editable operan por `draftLineId`, por lo que pueden coexistir múltiples líneas del mismo Product. “Agregar” incrementa la línea existente sin instruction canonical; “Agregar otra línea” crea una nueva línea del mismo Product.
- El frontend detecta líneas duplicadas por `(ProductId, canonicalInstruction)` y bloquea la Confirmación sin combinar cantidades.
- Ante incertidumbre de First o Subsequent, el workflow congela exactamente la key, destination u order reference relevante, context donde corresponde, y cada `productId`, `quantity` e instruction canonical. Mientras existe incertidumbre no permite editar la Composición ni cambiar destination; retry reenvía el mismo request exacto con la misma key. Discard desbloquea y la próxima Confirmación usa una key nueva.
- Un `409` conocido no se trata como incertidumbre: la Composición permanece editable y la siguiente intención usa una key nueva.
- El lookup de Order muestra Product actual, quantity, `appliedPrice` histórico e instruction confirmada; cuando es null muestra “Sin instrucción”. Las líneas del mismo Product permanecen visualmente distinguibles.
- El Estado de autenticación es explícito: `loading`, `unauthenticated` o `authenticated(currentIdentity)`. Login envía `loginIdentifier + secret` con antiforgery, muestra el `401` genérico y limpia el secret al tener éxito. La barra de sesión muestra el `OperationalName` actual y permite logout/cambiar persona.
- `PreparationPanel` carga los destinos habilitados por nombre: con cero informa que no hay destinos, con uno lo selecciona automáticamente y con varios presenta selector. Muestra el `ProductOperationalName` vigente, context/reference, instruction y los contadores Total, Pending, En preparación y Ready; no presenta un Status único.
- Cada Work con cantidad Pending ofrece un input integer de Start, inicialmente igual al Pending actual, y el botón “Iniciar”. Cada Work con cantidad InPreparation ofrece un input integer de Ready, inicialmente igual al InPreparation actual, y el botón “Marcar listo”. Ambas acciones admiten la totalidad o una parte del bucket elegible.
- Cuando `ReadyQuantity == TotalQuantity`, el panel muestra “Todo listo”. No usa lenguaje de Delivery.
- Cada Work admite como máximo una mutación frontend activa, en fase mínima `submitting` o `uncertain`; los demás Work siguen operables. Una intención nueva obtiene `crypto.randomUUID()` y congela kind, `WorkId`, quantity y key.
- En `200`, aplica los contadores autoritativos del response, resuelve la intención y refresca después. No realiza mutación optimista.
- Un `409` conocido limpia la intención, informa que cambió el Estado o que existe conflicto de idempotencia y refresca.
- Ante network/timeout con resultado incierto no cambia cantidades locales: conserva exactamente key, body y endpoint, ofrece retry exacto y bloquea una segunda mutación sobre ese Work hasta resolver. No genera una key nueva durante el retry ni ofrece descarte ordinario.
- Un `401` devuelve el frontend a `unauthenticated`, limpia antiforgery e intents y vuelve al login. Un `403` conserva la Identity autenticada y muestra la falla de autorización. Un `404` informa “trabajo ya no disponible” y refresca sin revelar una posible pérdida de enablement.

### Delivery frontend

- Delivery se abre desde `Consultar Pedido` mediante `Abrir entrega de este Pedido`. Reutiliza el lookup existente: no agregó router ni buscador duplicado.
- Todos los Contents permanecen visibles y no se mezclan por Product. Direct presenta Total, Delivered, Deliverable, Remaining y “Preparación no requerida”; Prepared presenta Total, Ready, Delivered, Deliverable y Remaining.
- Un Content completamente entregado muestra la presentación derivada “Entregado”. No significa Closed, Liquidated ni Paid.
- Cuando `deliverable > 0`, el Content ofrece un input integer con `min = 1`, `max = deliverable` observado y valor inicial igual a ese máximo, más el botón “Entregar”. Permite la cantidad elegible completa o una parcial; “todo” es el número exacto observado, no una intención dinámica.
- Cada Content admite como máximo una mutación activa y los demás Contents siguen operables. El intent congela `incorporationId`, `contentOrdinal`, quantity e idempotency key, con fases `submitting` y `uncertain`; no existe loading global de Delivery.
- En `200`, resuelve el intent, no calcula Delivered de forma optimista, refresca el GET de Delivery y renderiza Estado autoritativo. No decrementa Ready localmente.
- Un `409` es una falla conocida: limpia el intent, informa cambio de Estado o conflicto de idempotencia, refresca y reserva una key nueva para una intención futura. No se trata como éxito.
- Ante network, timeout o `5xx` conservadoramente incierto no modifica quantities: conserva target, quantity y key, marca `uncertain` y el retry usa exactamente el mismo endpoint, body y key. No ofrece descarte ordinario que habilite una Delivery incompatible.
- Un `401` vuelve a `unauthenticated`, limpia antiforgery e intents y retorna al login. Un `403` conserva la Identity autenticada y muestra la falla de autorización. Un `404` del GET informa Pedido no encontrado; un `404` del POST rechaza el intent conocido y refresca. Un `400` es un error conocido, no outcome incierto.

### Inventory frontend

- `InventoryPanel` presenta superficies independientes de **Configuración de Inventario** y **Estado actual de Inventario**. Cada una carga su read model y expone por separado su `403`; una Identity de configuración puede crear/listar Items sin aparentar autoridad operacional y una Identity de operación puede consultar/actuar sin aparentar autoridad de configuración.
- La vista de configuración crea Items con nombre y unidad operacional. La vista operacional distingue “Existencia no establecida” (`null`) de una existencia establecida en `0`, muestra cantidad y unidad autoritativas y emite una advertencia visible de inconsistencia cuando el saldo es negativo.
- Cada Item operacional permite registrar un Conteo y luego reconciliar exactamente esa observación. También expone Entry, ManualExit y Waste sólo una vez establecida la cantidad. No calcula aritmética optimista: tras una mutación usa el resultado autoritativo y refresca el listado.
- Cada intención nueva congela kind, Item, body e `Idempotency-Key`. Ante network, timeout o `5xx` incierto conserva esos mismos datos, bloquea otra acción sobre el Item y ofrece reintentar la misma operación con la misma key. Un conflicto conocido resuelve la intención y refresca; no se presenta como éxito.
- **Movimientos** muestra la Historia paginada, separa Reconciliation de Entry, ManualExit y Waste, muestra actor con su nombre operacional vigente, timestamp, efecto y saldos, y distingue el establecimiento inicial sin diferencia ficticia. Puede actualizarse manualmente y se refresca automáticamente después de una mutación autoritativa del Item abierto.
- No hay SSE ni actualización activa. Los intents inciertos viven sólo en memoria y una recarga puede perder la key; no existe cola offline ni retry automático en background.

- Todavía no existe `OperationalConfigurationPanel` productivo ni frontend administrativo completo.

## 17. Testing y verificación

Existen tres capas:

- backend: suites de integración xUnit con PostgreSQL real mediante Testcontainers;
- frontend: Vitest y React Testing Library;
- recorrido integrado real: Playwright sobre Chromium.

`scripts/verify.cmd` y `scripts/verify.sh` ejecutan la verificación ordinaria. Esta verificación requiere Docker porque las suites backend usan Testcontainers. La opción `--e2e` añade PostgreSQL efímero aislado, backend, Vite y Chromium; no usa la base persistente de `compose.yaml`.

El estado verificado del baseline implementado al cierre del núcleo operativo mínimo de Inventory es backend 561/561, frontend 170/170 y Playwright 4/4. `HasPendingModelChanges` es false para los cinco `DbContext` (5/5). Tanto `scripts\verify.cmd` como `scripts\verify.cmd --e2e` pasan en ese baseline.

El baseline integrado mantiene cuatro escenarios Playwright. El recorrido principal crea un `Product` con precio 10, realiza la Primera Confirmación, cambia el precio a 12, realiza una Confirmación posterior y consulta el `Order`, preservando `Incorporation` 1 a 10 e `Incorporation` 2 a 12. El escenario operacional de Preparation/Delivery conserva sus actores y cantidades parciales. El escenario de Inventory inicia sesión con una Identity que sólo posee `InventoryConfiguration`, crea un Item y comprueba que no puede operar; luego usa otra Identity que sólo posee `InventoryOperation`, establece la cantidad mediante Count/Reconciliation, registra Entry, ManualExit atravesando cero y Waste, comprueba el saldo vigente negativo y consulta una Historia que contiene los cuatro Movimientos. Esto demuestra capacidades separadas, `null != 0`, balance autoritativo e integración real navegador/backend/PostgreSQL. El fixture no implica bootstrap productivo. El harness usa PostgreSQL aislado y realiza cleanup de sus procesos y recursos.

`NexoBar.E2E.DatabaseSetup` aplica explícitamente y verifica los cinco `DbContext`: `OperationalConfigurationDbContext`, `CatalogDbContext`, `InventoryDbContext`, `OrderOperationsDbContext` e `IdentitiesAndCapabilitiesDbContext`. `HasPendingModelChanges` debe ser `false` para los cinco. Chromium se instala manualmente desde `frontend`:

```text
npm exec playwright install chromium
```

Se ejecuta `--e2e` cuando un cambio afecta el recorrido vertical integrado, contratos entre frontend y backend, el harness, o antes de aceptar un slice que atraviesa navegador, backend y persistencia. No se exige para todo cambio mecánico o local cubierto por la verificación ordinaria.

```text
scripts\verify.cmd
scripts\verify.cmd --e2e
./scripts/verify.sh
./scripts/verify.sh --e2e
```

## 18. Tooling, Development y migraciones

- .NET SDK `10.0.400`, con roll-forward deshabilitado.
- Node.js `22.13.1` y npm `10.9.2`.
- `package-lock.json` es autoritativo para dependencias frontend y la instalación reproducible usa `npm ci`.
- Docker es necesario para las suites de integración. Chromium de Playwright es necesario solo para `--e2e`.
- E2E requiere libres los puertos técnicos `5028` y `5173`; el harness falla si están ocupados y no mata ni reutiliza procesos ajenos.
- `compose.yaml` proporciona un PostgreSQL local descartable para Development; no representa la topología productiva.
- Fuera de integración y E2E, el flujo técnico general para provisionar y aplicar migraciones aún no está estandarizado como tooling del repositorio. Deberá resolverse cuando exista un driver real de onboarding o deployment.
- `NexoBar.E2E.DatabaseSetup` es parte del harness E2E, no una herramienta general de Development.

Las migraciones relevantes de S3-I3 son:

- `20260830210000_ReidentifyIncorporationContent`;
- `20260830230000_AddConfirmationInstructions`.

Ambas tienen Designer completo, snapshots coherentes y `HasPendingModelChanges = false`. La segunda agrega `instruction` a Contents y a los contenidos durables de comandos, y elimina las uniques temporales por Product. Su `Down` protege los datos y falla explícitamente si ya existen duplicados incompatibles con el schema anterior; nunca fusiona ni elimina líneas silenciosamente.

Preparation Start agregó la migración `20260831063910_AddPreparationStart`, que materializa la Historia y los comandos durables de Preparation. Ready reutiliza ese modelo y no agregó una migración adicional.

Las migraciones vigentes de Slice 4 son:

- `AddDeliveryState`, que crea el Estado 1:1 y hace backfill `DeliveredQuantity = 0` para Contents previos;
- `CapturePreparationRequirementAtConfirmation`, que agrega el snapshot histórico: hace backfill `true` cuando existía el Work exacto y `false` cuando no existía, y luego deja la columna `NOT NULL` sin default persistente;
- `AddDeliveryProgress`, que materializa `delivery_history` y `delivery_commands`.

Las migraciones vigentes de Inventory en Slice 5 son:

- `InitialInventory`, que crea Items y comandos durables de creación;
- `AddInventoryCountReconciliation`, que agrega CountObservation, Reconciliation, Historia y comandos durables;
- `AddEverydayInventoryMovements`, que materializa Entry, ManualExit y Waste sobre el mismo Estado, Historia y namespace durable de Movimientos.

No se modificaron migraciones durante esta consolidación documental.

## 19. `InternalsVisibleTo`

`InternalsVisibleTo` existe únicamente para consumidores técnicos/test específicos: las suites de integración y `NexoBar.E2E.DatabaseSetup`. No es un mecanismo normal de colaboración productiva entre módulos.

## 20. Deuda consciente y fronteras no materializadas

Deuda técnica conocida:

- la igualdad del destino de las connection strings modulares no se valida automáticamente;
- la validación runtime o generación de contratos TypeScript sigue diferida;
- el tooling general de migraciones para Development sigue pendiente.
- un intent incierto de Preparation vive actualmente solo en memoria: reload o unmount puede perder su key. No hay persistencia en `localStorage` o `sessionStorage`, offline queue ni automatic background retry. El backend conserva idempotencia durable, pero el frontend no garantiza continuidad cross-reload del intent. Es deuda técnica/UX consciente, no una norma.
- los intents inciertos de Delivery también viven solo en memoria: reload o unmount puede perder target, quantity e idempotency key. No existen `localStorage`, `sessionStorage`, IndexedDB, offline queue ni background retry. El backend conserva idempotencia durable, pero el frontend no garantiza continuidad cross-reload. Esta deuda es especialmente relevante porque Delivery representa un hecho físico; se registra sin convertirla aquí en requisito normativo ni resolverla.
- los intents inciertos de Inventory también viven sólo en memoria: reload o unmount puede perder kind, Item, body e idempotency key. El backend conserva idempotencia durable, pero el frontend no garantiza continuidad cross-reload.
- una auditoría realizada durante I3B detectó seis nombres de identificadores EF preexistentes de más de 63 bytes en `OrderOperations`. Son ajenos a los cambios de Inventory, no se corrigen en esta unidad documental y quedan señalados para una futura revisión de higiene de schema/migraciones.

Fronteras todavía no materializadas, sin que esta enumeración diseñe su solución:

- retrofit global de autenticación/autorización para endpoints todavía anónimos, según corresponda: Catalog, OperationalConfiguration, Confirmaciones, lookup de Order y otros endpoints funcionales actuales no cubiertos por Preparation o Delivery;
- bootstrap productivo de Identity y credenciales;
- implementación de recovery extraordinario (`AD-SEC-01`) y UX de recovery ordinario;
- decisión normativa de parámetros de timeout (`PAR-SEC-02`) y política cuantitativa de brute-force/lockout;
- frontend administrativo completo;
- elegibilidad de Delete Identity y coordinación con Historia;
- auditoría global de seguridad y consumo de autenticación en SSE;
- Correction ordinaria sobre cantidad todavía Pending/elegible, conforme a reglas aún no definidas;
- excepciones sobre Work iniciado y Correction de progreso: una Correction no debe reinterpretar silenciosamente cantidades InPreparation o Ready, y modificar trabajo ya iniciado requiere tratamiento excepcional;
- Delivery Correction y su distinción entre Correction ordinaria y excepcional conforme a la Source aplicable;
- reversal y exception handling de Delivery;
- interacción completa entre Delivery y Corrections de Content;
- Liquidation, Settlement, Payment y Closure;
- Cancellation;
- query/API/UI de Historia de Delivery;
- cantidades fraccionarias de Preparation;
- cantidades fraccionarias de Delivery, mientras continúen abiertas;
- persistencia cross-reload de intents inciertos de Preparation;
- persistencia cross-reload de intents inciertos de Delivery;
- prioridad/SLA y owner/assignment de Preparation;
- query/API/UI de Historia de Preparation;
- profundidad de Historia administrativa según los OPEN-TRA aplicables;
- Correcciones de Content o instruction;
- edición de una instruction ya confirmada;
- Movement Correction de Inventory; la forma final de su relación con Movimientos previos no está decidida aquí;
- `retire/reactivate/delete` de InventoryItem;
- Unit Correction de InventoryItem y sus reglas antes/después de existir Historia;
- actualización activa de Inventory mediante SSE;
- persistencia cross-reload de intents inciertos de Inventory;
- política final de reutilización de nombres antes de materializar lifecycle de InventoryItem;
- SSE.

El checkpoint de seguridad requerido para acciones humanas de Preparation y Delivery está cerrado, pero eso no significa que la seguridad global esté cerrada ni que todos los endpoints backend estén protegidos. `Quantity` en Content no implica identidad física individual. La instruction confirmada no es editable y SSE permanece pendiente.

### Correction y coordinación Preparation/Delivery

Delivery Correction no está implementada. Una futura Correction deberá modificar State explícitamente y preservar `QuantityDelivered` histórico; no podrá borrar ni reinterpretar silenciosamente History. La distinción entre Correction ordinaria y excepcional seguirá la Source aplicable, y este documento no diseña endpoint ni política adicional.

Una futura Correction de Preparation tampoco puede crear silenciosamente `DeliveredQuantity > ReadyQuantity` para un Prepared Content. Las Corrections que afecten cantidad ya Ready o Delivered deberán coordinarse con Delivery; esa política permanece abierta y no se resuelve aquí.

### Liquidation, Closure y completitud

`Delivered != Liquidated != Closed`. Slice 4 no implementa Liquidation, Settlement, Payment ni Closure, y Delivery no contiene State materializado de esos procesos. Las futuras reglas que congelen Delivery después de Liquidation deberán integrarse cuando ese State exista; hoy no se inventan `IsLiquidated` ni `IsClosed`.

Tampoco se persiste `Order.IsDelivered` ni se afirma un Status global implementado. La completitud puede derivarse técnicamente del Estado vigente, pero futuras Corrections afectan el concepto de cantidad requerida; no se eleva esa derivación a una decisión adicional.

## 21. Estado de Slice 3

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

La Preparation mínima operativa ordinaria está materializada para nacimiento de Work, lectura segura, Start parcial, Ready parcial, operación multi-actor, Historia, idempotencia, acciones frontend y recorrido E2E. **Preparation mínima operativa del slice cerrada.** Esto no equivale a Preparation completa del MVP ni resuelve Correction, excepciones, SSE, consulta de Historia u otros pendientes de la sección 20.

## 22. Estado de Slice 4

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

**Delivery mínima operativa del Slice 4 cerrada.** Esto no equivale a Delivery completa del MVP ni materializa Correction, reversals, Liquidation, Payment, Closure, History UI, SSE o las demás fronteras de la sección 20.

## 23. Estado de Slice 5 — Inventory

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

## 24. Protocolo de trabajo

- `AGENTS.md` contiene el contexto operacional persistente para Codex. Ante una decisión no resuelta o una contradicción normativa se detiene la parte afectada y se reporta.
- Los cambios permanecen limitados al objetivo de la tarea y se verifican en proporción al riesgo. No se agregan dependencias, alcance o refactors adyacentes sin autorización.
- No se modifican pruebas para acomodar una implementación incorrecta.
- Commit, push y operaciones Git destructivas requieren instrucción explícita.
