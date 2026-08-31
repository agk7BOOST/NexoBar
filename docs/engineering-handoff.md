# NexoBar - Engineering Handoff

## 1. Propósito y autoridad

- Este documento es el baseline técnico actual, versionado y materializado del repositorio.
- No reemplaza las Sources normativas de comportamiento funcional, modelo conceptual, UX, RNF o arquitectura. Ante una discrepancia prevalece la Source aplicable.
- Las decisiones posteriores aprobadas o Adendas sustituyen únicamente el asunto concreto que modifican. Lo explícitamente abierto, parametrizado o provisional continúa abierto.

## 2. Plataforma, repositorio y módulos

- Monorepo Git con backend y frontend como unidades técnicas separadas.
- Backend autoritativo: C# sobre .NET 10 LTS y ASP.NET Core 10. Frontend: React 19, TypeScript estricto y Vite 8.
- El backend es un monolito modular y una unidad principal de despliegue. `NexoBar.Host` compone los módulos y es el composition root.
- Los módulos superiores son `OrderOperations`, `Catalog`, `Inventory`, `IdentitiesAndCapabilities` y `OperationalConfiguration`. `Inventory` permanece presente como límite sin implementación funcional; `IdentitiesAndCapabilities` ya posee Estado, sesiones, administración y capacidades públicas de autorización materializadas.
- `OperationalConfiguration` ya es un módulo persistente y funcional en el alcance de `PreparationResponsibility`; no tiene todavía un lifecycle completo.
- `Preparation` es una frontera interna de `OrderOperations`, no un módulo top-level.
- Cada módulo conserva la propiedad de su Estado y colabora mediante capacidades explícitas. No hay ciclos ni un `Shared`/`Common` genérico.

### Dependencias modulares materializadas

```text
Host
├─ OrderOperations
├─ Catalog
├─ OperationalConfiguration
└─ IdentitiesAndCapabilities

OrderOperations ──→ Catalog
OrderOperations ──→ IdentitiesAndCapabilities
Catalog ──────────→ OperationalConfiguration
IdentitiesAndCapabilities ──→ OperationalConfiguration
```

- `Catalog` consume una capacidad pública estrecha de `OperationalConfiguration` para validar la existencia de una `PreparationResponsibility`; no accede a su `DbContext`, schema ni tablas.
- `OrderOperations` depende de `Catalog` y de capacidades públicas estrechas de `IdentitiesAndCapabilities`; no depende directamente de `OperationalConfiguration` ni lo consulta durante una Confirmación.
- `IdentitiesAndCapabilities` consume una capacidad pública estrecha de `OperationalConfiguration` para resolver o validar destinos de Preparation.
- No existen las dependencias inversas `OperationalConfiguration → IdentitiesAndCapabilities`, `Catalog → OrderOperations` ni `IdentitiesAndCapabilities → OrderOperations`.
- No existen foreign keys ni accesos a `DbContext`, schema o tablas ajenos cross-module. El Host compone las capacidades y sus implementaciones.

## 3. Persistencia

- Una instancia/base PostgreSQL compartida es la persistencia relacional transaccional primaria. EF Core 10 y Npgsql son la estrategia predeterminada.
- Cada módulo que persiste Estado posee su propio `DbContext`, schema e historial de migraciones:
  - `CatalogDbContext` mapea `catalog`;
  - `OrderOperationsDbContext` mapea `order_operations`;
  - `OperationalConfigurationDbContext` mapea `operational_configuration`;
  - `IdentitiesAndCapabilitiesDbContext` mapea `identities_and_capabilities`.
- Las migraciones son explícitas, versionadas y revisables. El Host productivo no ejecuta auto-migrate durante el startup.
- Dinero y cantidades exactas usan `numeric`/`decimal`, nunca coma flotante binaria como representación autoritativa; cuando aplica, su representación HTTP es un decimal string estable.
- Las identidades persistentes principales materializadas usan UUID v7. `Idempotency-Key` usa UUID v4.
- Los timestamps operacionales autoritativos los asigna el backend y se persisten en UTC.
- Estado vigente e Historia semántica son conceptos distintos y se confirman atómicamente cuando son consecuencias inseparables. La solución no es CQRS ni Event Sourcing.
- SQL explícito puede usarse cuando una invariante, concurrencia o rendimiento lo justifique. No se usan Repository Pattern genérico ni lazy loading por defecto.

### Invariante de configuración

Las connection strings modulares de `Catalog`, `OrderOperations`, `OperationalConfiguration` e `IdentitiesAndCapabilities` deben apuntar a la misma instancia y base PostgreSQL. Las colaboraciones transaccionales entre módulos dependen de ello. Actualmente es una invariante de configuración documentada, no una validación automatizada.

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

## 8. Colaboración `OrderOperations -> Catalog` y concurrencia

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

## 9. Confirmaciones, Incorporations y nacimiento de Work

La primera Confirmación crea el `Order` y su primera `Incorporation`; cada Confirmación posterior crea una nueva `Incorporation` del mismo `Order`. Ambas aceptan Products preparados.

- La mutación sobre un `Order` existente expresa la intención de una nueva Confirmación. La `operationalReference` es opaca; el request contiene solo items y no modifica el `Context` del `Order`.
- Cada Confirmación posterior exitosa crea una `Incorporation` con ordinal sucesivo, una nueva `ConfirmationHistory` y contenido con el `appliedPrice` vigente estabilizado para esa Confirmación.

### IncorporationContent y líneas homogéneas

`IncorporationContent` tiene PK compuesta `(incorporation_id, content_ordinal)` y contiene `product_id`, `quantity`, `applied_price` e `instruction` nullable. `contentOrdinal` es una identidad técnica local a la `Incorporation`, positiva y estable. No se expone públicamente y no representa posición UX, unidad física, unidad conceptual de cumplimiento ni orden de captura.

Ya no existe unicidad `(incorporation_id, product_id)`: una `Incorporation` puede contener múltiples líneas del mismo Product con instrucciones distintas. Cada Content representa una cantidad homogénea respecto de `ProductId + canonical instruction`. Por ejemplo, son líneas válidas dentro de una misma Confirmación:

```text
Product A x1 / null
Product A x1 / "sin cebolla"
Product A x2 / "sin tomate"
```

No existe individualización de unidades físicas. `Quantity` permanece agregada y podrá evolucionar parcialmente en Preparation. No se equipara `IncorporationContent` con una unidad conceptual de cumplimiento; una identidad más fuerte para esas unidades queda fuera de alcance salvo que aparezca un nuevo driver.

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
→ Order / Incorporation / Content / Work / History / command
→ commit

Subsequent Confirmation
advisory idempotency
→ Order FOR UPDATE
→ Products FOR SHARE
→ Incorporation / Content / Work / History / command
→ commit
```

- En Confirmaciones posteriores, `Order FOR UPDATE` serializa la asignación del ordinal `MAX + 1`; la restricción única `(order_id, ordinal)` es la defensa física adicional.
- Un helper interno estrecho centraliza únicamente la creación de `IncorporationContent` y la creación condicional de `PreparationWork`; no es un framework ni un pipeline genérico.
- El contenido no preparado no crea Work. El contenido preparado crea exactamente un Work dentro de la misma transacción de la Confirmación, tanto First como Subsequent.
- La responsabilidad aplicada al Work procede del snapshot estabilizado del `Product`. Los cambios posteriores de configuración del `Product` no modifican Work existente.
- No existe un evento `WorkCreated`.

### Replay de Confirmación

- El replay se resuelve antes de consultar `Catalog` y reconstruye la respuesta desde la persistencia original de `OrderOperations`; en una Confirmación posterior tampoco bloquea el `Order`.
- No crea nuevos Work y no requiere `workId` en las tablas de comandos.
- El nacimiento de Work no introduce una idempotencia adicional: queda cubierto por la idempotencia y transacción de la Confirmación que lo origina.

## 10. PreparationWork y consulta autorizada

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

Las cantidades iniciales son `total = confirmed quantity`, `pending = total`, `inPreparation = 0` y `ready = 0`. La base impone `total > 0`, cantidades de estado no negativas y `pending + inPreparation + ready = total`. El progreso ordinario futuro no deberá alterar el total; futuras Correcciones todavía no están materializadas, por lo que no se afirma que `TotalQuantity` sea eternamente inmutable.

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

## 11. Estado e Historia

- `ConfirmationHistory` explica la Confirmación que originó el contenido y conserva `confirmedContext` histórico.
- `ConfirmationHistory` y el `IncorporationContent` persistido explican conjuntamente la existencia de una instruction confirmada; no existe `InstructionAdded History`.
- `PreparationWork` representa Estado operacional vigente.
- La query de Preparation usa `Order.Context` vigente; no debe confundirse con `ConfirmationHistory.confirmedContext`.
- Todavía no existen `PreparationWorkHistory` ni evento `WorkCreated`, porque aún no existe progreso humano materializado.
- Tampoco está materializado Change Context.
- Esta separación no constituye Event Sourcing.

## 12. Idempotencia y resultado incierto

- Los comandos materializados reciben un `Idempotency-Key` UUID v4 y mantienen persistencia durable por comando.
- Cada comando/módulo toma un advisory transaction lock local. Efecto e idempotencia se confirman dentro de la misma transacción.
- Misma key y misma intención produce replay; misma key e intención incompatible produce conflicto.
- Ante resultado incierto se reintenta con la misma key. No existe blind retry para mutaciones.
- `Catalog`, `OperationalConfiguration`, Primera Confirmación y Confirmación posterior tienen infraestructura y canonicalización propias; no deben uniformarse sin una decisión explícita.
- En una Confirmación posterior, la intención se define por `Order` e items canonicalizados; el orden del array no la altera.
- La duplicación local actual es deliberada. No existe infraestructura `Shared` de idempotencia.

Los contenidos durables de los comandos First y Subsequent tienen PK `(idempotency_key, line_ordinal)` y persisten `product_id`, `quantity` e `instruction` canonical. `lineOrdinal` es técnico y canonical: se deriva después de ordenar las líneas semánticas y no depende del orden HTTP.

El matching de intención incluye `ProductId`, `Quantity` y canonical instruction, además del resto de la intención ya existente. La misma key con instruction diferente produce conflicto; whitespace o line endings equivalentes y distinto orden del array producen replay. El replay no reconsulta `Catalog`, no recrea Work y reproduce el estado persistido.

## 13. Contratos técnicos materializados

- HTTP ordinario usa HTTPS y JSON; ASP.NET Core Minimal APIs implementa endpoints con handlers delgados.
- Problem Details es la estructura común de errores e incluye códigos estables. OpenAPI describe el contrato técnico implementado, no sustituye su significado normativo.
- Las mutaciones expresan intenciones operacionales específicas, no reemplazos CRUD genéricos del Estado autoritativo.
- `operationalReference` es opaca en HTTP y OpenAPI, aunque actualmente derive internamente del Order ID.
- El lookup de Order se reconstruye exclusivamente desde `OrderOperations`; `Catalog` no reconstruye condiciones históricas.
- Las propiedades JSON autoritativas no reconocidas se rechazan en los comandos donde esta regla está materializada.

Los items de request de First y Subsequent contienen `productId`, `quantity` e `instruction` optional/nullable. Los items de la respuesta confirmada contienen `productId`, `quantity`, `appliedPrice` e `instruction`; el lookup de Order devuelve también `instruction` nullable por item. La consulta autorizada de Preparation Work devuelve `productOperationalName` vigente e `instruction` nullable, y obtiene `productId` e instruction desde Content. Ningún contrato público expone `contentOrdinal` ni `draftLineId`.

## 14. Frontend materializado

- `App` coordina `CatalogPanel`, `OrderWorkflow` y `OrderLookup`; las responsabilidades de catálogo/Price Change, Pedido activo/Composición y consulta están separadas. Un `Order` consultado puede retomarse para una Composición posterior.
- Los recursos se refrescan desde la autoridad: Price Change recarga `Catalog` y las Confirmaciones recargan el `Order`. El cliente no compone manualmente la Historia.
- La Composición usa `CompositionLine { draftLineId, productId, quantity, instruction }`. `draftLineId` se crea con `crypto.randomUUID()`, es estable mientras vive la línea y existe solo en frontend: no se envía, no pertenece al dominio y no es el `Idempotency-Key`.
- Cantidad `+/-`, remove e instruction editable operan por `draftLineId`, por lo que pueden coexistir múltiples líneas del mismo Product. “Agregar” incrementa la línea existente sin instruction canonical; “Agregar otra línea” crea una nueva línea del mismo Product.
- El frontend detecta líneas duplicadas por `(ProductId, canonicalInstruction)` y bloquea la Confirmación sin combinar cantidades.
- Ante incertidumbre de First o Subsequent, el workflow congela exactamente la key, destination u order reference relevante, context donde corresponde, y cada `productId`, `quantity` e instruction canonical. Mientras existe incertidumbre no permite editar la Composición ni cambiar destination; retry reenvía el mismo request exacto con la misma key. Discard desbloquea y la próxima Confirmación usa una key nueva.
- Un `409` conocido no se trata como incertidumbre: la Composición permanece editable y la siguiente intención usa una key nueva.
- El lookup de Order muestra Product actual, quantity, `appliedPrice` histórico e instruction confirmada; cuando es null muestra “Sin instrucción”. Las líneas del mismo Product permanecen visualmente distinguibles.
- El Estado de autenticación es explícito: `loading`, `unauthenticated` o `authenticated(currentIdentity)`. Login envía `loginIdentifier + secret` con antiforgery, muestra el `401` genérico y limpia el secret al tener éxito. La barra de sesión muestra el `OperationalName` actual y permite logout/cambiar persona.
- `PreparationPanel` carga los destinos habilitados por nombre: con cero informa que no hay destinos, con uno lo selecciona automáticamente y con varios presenta selector. Renderiza Work en solo lectura con nombre vigente del Product, instruction, contexto/referencia y cantidades/Estado. No implementa Start ni Ready.
- Un `401` devuelve el frontend a `unauthenticated` y limpia el antiforgery token en memoria. Un `403` conserva la Identity autenticada y muestra la falla de autorización.
- Todavía no existe `OperationalConfigurationPanel` productivo ni frontend administrativo completo.

## 15. Testing y verificación

Existen tres capas:

- backend: suites de integración xUnit con PostgreSQL real mediante Testcontainers;
- frontend: Vitest y React Testing Library;
- recorrido integrado real: Playwright sobre Chromium.

`scripts/verify.cmd` y `scripts/verify.sh` ejecutan la verificación ordinaria. Esta verificación requiere Docker porque las suites backend usan Testcontainers. La opción `--e2e` añade PostgreSQL efímero aislado, backend, Vite y Chromium; no usa la base persistente de `compose.yaml`.

El estado cerrado después de S3-SEC-I4 verifica backend 217/217, frontend 80/80 y Playwright 3/3. `HasPendingModelChanges` es false para los cuatro `DbContext` (4/4). Tanto `scripts\verify.cmd` como `scripts\verify.cmd --e2e` pasan.

El baseline integrado mantiene tres escenarios Playwright. El recorrido principal crea un `Product` con precio 10, realiza la Primera Confirmación, cambia el precio a 12, realiza una Confirmación posterior y consulta el `Order`, preservando `Incorporation` 1 a 10 e `Incorporation` 2 a 12. El nuevo escenario de seguridad provisiona un preparer mediante setup E2E, hace login, muestra su Identity y el destino habilitado por nombre, muestra Work autorizado, no muestra otro destino, hace logout y vuelve al login. Ese fixture no implica bootstrap productivo. El harness usa PostgreSQL aislado y realiza cleanup de sus procesos y recursos.

`NexoBar.E2E.DatabaseSetup` aplica explícitamente y verifica los cuatro `DbContext`: `OperationalConfigurationDbContext`, `CatalogDbContext`, `OrderOperationsDbContext` e `IdentitiesAndCapabilitiesDbContext`. `HasPendingModelChanges` debe ser `false` para los cuatro. Chromium se instala manualmente desde `frontend`:

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

## 16. Tooling, Development y migraciones

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

## 17. `InternalsVisibleTo`

`InternalsVisibleTo` existe únicamente para consumidores técnicos/test específicos: las suites de integración y `NexoBar.E2E.DatabaseSetup`. No es un mecanismo normal de colaboración productiva entre módulos.

## 18. Deuda consciente y fronteras no materializadas

Deuda técnica conocida:

- la igualdad del destino de las connection strings modulares no se valida automáticamente;
- la validación runtime o generación de contratos TypeScript sigue diferida;
- el tooling general de migraciones para Development sigue pendiente.

Fronteras todavía no materializadas, sin que esta enumeración diseñe su solución:

- retrofit de autenticación/autorización para endpoints todavía anónimos, según corresponda: Catalog, OperationalConfiguration, Confirmaciones, lookup de Order y otros endpoints funcionales actuales no pertenecientes a Preparation;
- bootstrap productivo de Identity y credenciales;
- implementación de recovery extraordinario (`AD-SEC-01`) y UX de recovery ordinario;
- decisión normativa de parámetros de timeout (`PAR-SEC-02`) y política cuantitativa de brute-force/lockout;
- frontend administrativo completo;
- elegibilidad de Delete Identity y coordinación con Historia;
- auditoría global de seguridad y consumo de autenticación en SSE;
- comandos `Start` y `MarkReady`, progreso parcial y excepciones de Preparation;
- profundidad de Historia administrativa según los OPEN-TRA aplicables;
- Correcciones de Content o instruction;
- edición de una instruction ya confirmada;
- SSE.

El checkpoint de seguridad requerido para acciones humanas de Preparation está cerrado, pero eso no significa que la seguridad global esté cerrada ni que todos los endpoints backend estén protegidos. `Quantity` en Content no implica identidad física individual. Actualmente solo la Confirmación origina Work automáticamente; Preparation no permite `Start`, `MarkReady`, progreso parcial ni excepciones. Las Correcciones de Content/instruction no están implementadas y la instruction confirmada no es editable. SSE permanece pendiente.

## 19. Estado de S3-SEC-I1..I4 y próximo checkpoint

El checkpoint de Identity, autenticación y capabilities requerido antes del progreso humano de Preparation ya está materializado:

- `a6e6f1a feat: add identities and capabilities state`;
- `74b941c feat: add opaque identity sessions`;
- `66d0ab3 feat: add identity administration`;
- `f82e0c7 feat: secure preparation work access`.

S3-SEC-I1..I4 materializa Estado de Identity y capabilities, credencial y Session opacas, administración e invariante de `GeneralConfiguration`, autorización vigente de la lectura de Work, destinos habilitados, nombre vigente de Product y la superficie frontend mínima autenticada de Preparation.

**NEXT:** diseño funcional de transiciones de progreso de Preparation. Este handoff no define todavía la semántica de Start, Ready, progreso parcial ni sus consecuencias de Estado e Historia.

## 20. Protocolo de trabajo

- `AGENTS.md` contiene el contexto operacional persistente para Codex. Ante una decisión no resuelta o una contradicción normativa se detiene la parte afectada y se reporta.
- Los cambios permanecen limitados al objetivo de la tarea y se verifican en proporción al riesgo. No se agregan dependencias, alcance o refactors adyacentes sin autorización.
- No se modifican pruebas para acomodar una implementación incorrecta.
- Commit, push y operaciones Git destructivas requieren instrucción explícita.
