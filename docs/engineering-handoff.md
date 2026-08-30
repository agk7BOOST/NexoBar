# NexoBar - Engineering Handoff

## 1. Propósito y autoridad

- Este documento es el baseline técnico actual, versionado y materializado del repositorio.
- No reemplaza las Sources normativas de comportamiento funcional, modelo conceptual, UX, RNF o arquitectura. Ante una discrepancia prevalece la Source aplicable.
- Las decisiones posteriores aprobadas o Adendas sustituyen únicamente el asunto concreto que modifican. Lo explícitamente abierto, parametrizado o provisional continúa abierto.

## 2. Plataforma, repositorio y módulos

- Monorepo Git con backend y frontend como unidades técnicas separadas.
- Backend autoritativo: C# sobre .NET 10 LTS y ASP.NET Core 10. Frontend: React 19, TypeScript estricto y Vite 8.
- El backend es un monolito modular y una unidad principal de despliegue. `NexoBar.Host` compone los módulos y es el composition root.
- Los módulos superiores son `OrderOperations`, `Catalog`, `Inventory`, `IdentitiesAndCapabilities` y `OperationalConfiguration`. `Inventory` e `IdentitiesAndCapabilities` están presentes como límites, todavía sin implementación funcional.
- `OperationalConfiguration` ya es un módulo persistente y funcional en el alcance de `PreparationResponsibility`; no tiene todavía un lifecycle completo.
- `Preparation` es una frontera interna de `OrderOperations`, no un módulo top-level.
- Cada módulo conserva la propiedad de su Estado y colabora mediante capacidades explícitas. No hay ciclos ni un `Shared`/`Common` genérico.

### Dependencias modulares materializadas

```text
OrderOperations
    ↓
Catalog
    ↓
OperationalConfiguration
```

- `Catalog` consume una capacidad pública estrecha de `OperationalConfiguration` para validar la existencia de una `PreparationResponsibility`; no accede a su `DbContext`, schema ni tablas.
- `OrderOperations` depende de `Catalog`, pero no depende directamente de `OperationalConfiguration` ni lo consulta durante una Confirmación.
- No existen foreign keys cross-module. El Host compone las capacidades y sus implementaciones.

## 3. Persistencia

- Una instancia/base PostgreSQL compartida es la persistencia relacional transaccional primaria. EF Core 10 y Npgsql son la estrategia predeterminada.
- Cada módulo que persiste Estado posee su propio `DbContext`, schema e historial de migraciones:
  - `CatalogDbContext` mapea `catalog`;
  - `OrderOperationsDbContext` mapea `order_operations`;
  - `OperationalConfigurationDbContext` mapea `operational_configuration`.
- Las migraciones son explícitas, versionadas y revisables. El Host productivo no ejecuta auto-migrate durante el startup.
- Dinero y cantidades exactas usan `numeric`/`decimal`, nunca coma flotante binaria como representación autoritativa; cuando aplica, su representación HTTP es un decimal string estable.
- Las identidades persistentes principales materializadas usan UUID v7. `Idempotency-Key` usa UUID v4.
- Los timestamps operacionales autoritativos los asigna el backend y se persisten en UTC.
- Estado vigente e Historia semántica son conceptos distintos y se confirman atómicamente cuando son consecuencias inseparables. La solución no es CQRS ni Event Sourcing.
- SQL explícito puede usarse cuando una invariante, concurrencia o rendimiento lo justifique. No se usan Repository Pattern genérico ni lazy loading por defecto.

### Invariante de configuración

Las connection strings modulares de `Catalog`, `OrderOperations` y `OperationalConfiguration` deben apuntar a la misma instancia y base PostgreSQL. La colaboración transaccional entre `OrderOperations` y `Catalog` depende de ello. Actualmente es una invariante de configuración documentada, no una validación automatizada.

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

## 5. Catalog y configuración de preparación

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

## 6. Colaboración `OrderOperations -> Catalog` y concurrencia

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

## 7. Confirmaciones, Incorporations y nacimiento de Work

La primera Confirmación crea el `Order` y su primera `Incorporation`; cada Confirmación posterior crea una nueva `Incorporation` del mismo `Order`. Ambas aceptan Products preparados.

- La mutación sobre un `Order` existente expresa la intención de una nueva Confirmación. La `operationalReference` es opaca; el request contiene solo items y no modifica el `Context` del `Order`.
- Cada Confirmación posterior exitosa crea una `Incorporation` con ordinal sucesivo, una nueva `ConfirmationHistory` y contenido con el `appliedPrice` vigente estabilizado para esa Confirmación.

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

## 8. PreparationWork y consulta

`PreparationWork` es Estado operacional vigente poseído por `OrderOperations` y contiene:

- `Id` UUID v7;
- `IncorporationId`;
- `ProductId`;
- `PreparationResponsibilityId`;
- `TotalQuantity`;
- `PendingQuantity`;
- `InPreparationQuantity`;
- `ReadyQuantity`.

Cada Work tiene PK por `Id`, `UNIQUE (incorporation_id, product_id)` y una FK compuesta interna hacia `IncorporationContent`. No tiene FK hacia `OperationalConfiguration`.

Las cantidades iniciales son `total = confirmed quantity`, `pending = total`, `inPreparation = 0` y `ready = 0`. La base impone `total > 0`, cantidades de estado no negativas y `pending + inPreparation + ready = total`. El progreso ordinario futuro no deberá alterar el total; futuras Correcciones todavía no están materializadas, por lo que no se afirma que `TotalQuantity` sea eternamente inmutable.

La consulta materializada es:

```text
GET /api/order-operations/preparation/work
    ?preparationResponsibilityId=...
```

- El filtro por responsabilidad es obligatorio y opera exclusivamente sobre Estado poseído por `OrderOperations`; no consulta `OperationalConfiguration`.
- Un UUID válido sin Work devuelve una lista vacía.
- Cada respuesta incluye `workId`, `preparationResponsibilityId`, `operationalReference` opaca, `context` vigente, `incorporationId`, `incorporationOrdinal`, `productId`, las cuatro cantidades y `confirmedAt`.
- `context` procede del `Order` actual. `confirmedAt` procede de la Confirmación que originó la `Incorporation`; no existe un `createdAt` artificial.
- La respuesta no incorpora el nombre vigente del `Product`.
- El lookup de `Order` permanece separado y no incorpora Work.

## 9. Estado e Historia

- `ConfirmationHistory` explica la Confirmación que originó el contenido y conserva `confirmedContext` histórico.
- `PreparationWork` representa Estado operacional vigente.
- La query de Preparation usa `Order.Context` vigente; no debe confundirse con `ConfirmationHistory.confirmedContext`.
- Todavía no existen `PreparationWorkHistory` ni evento `WorkCreated`, porque aún no existe progreso humano materializado.
- Tampoco está materializado Change Context.
- Esta separación no constituye Event Sourcing.

## 10. Idempotencia y resultado incierto

- Los comandos materializados reciben un `Idempotency-Key` UUID v4 y mantienen persistencia durable por comando.
- Cada comando/módulo toma un advisory transaction lock local. Efecto e idempotencia se confirman dentro de la misma transacción.
- Misma key y misma intención produce replay; misma key e intención incompatible produce conflicto.
- Ante resultado incierto se reintenta con la misma key. No existe blind retry para mutaciones.
- `Catalog`, `OperationalConfiguration`, Primera Confirmación y Confirmación posterior tienen infraestructura y canonicalización propias; no deben uniformarse sin una decisión explícita.
- En una Confirmación posterior, la intención se define por `Order` e items canonicalizados; el orden del array no la altera.
- La duplicación local actual es deliberada. No existe infraestructura `Shared` de idempotencia.

## 11. Contratos técnicos materializados

- HTTP ordinario usa HTTPS y JSON; ASP.NET Core Minimal APIs implementa endpoints con handlers delgados.
- Problem Details es la estructura común de errores e incluye códigos estables. OpenAPI describe el contrato técnico implementado, no sustituye su significado normativo.
- Las mutaciones expresan intenciones operacionales específicas, no reemplazos CRUD genéricos del Estado autoritativo.
- `operationalReference` es opaca en HTTP y OpenAPI, aunque actualmente derive internamente del Order ID.
- El lookup de Order se reconstruye exclusivamente desde `OrderOperations`; `Catalog` no reconstruye condiciones históricas.
- Las propiedades JSON autoritativas no reconocidas se rechazan en los comandos donde esta regla está materializada.

## 12. Frontend materializado

- `App` coordina `CatalogPanel`, `OrderWorkflow` y `OrderLookup`; las responsabilidades de catálogo/Price Change, Pedido activo/Composición y consulta están separadas. Un `Order` consultado puede retomarse para una Composición posterior.
- Los recursos se refrescan desde la autoridad: Price Change recarga `Catalog` y las Confirmaciones recargan el `Order`. El cliente no compone manualmente la Historia.
- Ante incertidumbre de un comando, el workflow conserva snapshot e idempotency key y exige una decisión manual de retry o discard.
- No existen todavía `OperationalConfigurationPanel` productivo, `PreparationPanel`, UI de Work ni progreso de Preparation en frontend.

## 13. Testing y verificación

Existen tres capas:

- backend: suites de integración xUnit con PostgreSQL real mediante Testcontainers;
- frontend: Vitest y React Testing Library;
- recorrido integrado real: Playwright sobre Chromium.

`scripts/verify.cmd` y `scripts/verify.sh` ejecutan la verificación ordinaria. Esta verificación requiere Docker porque las suites backend usan Testcontainers. La opción `--e2e` añade PostgreSQL efímero aislado, backend, Vite y Chromium; no usa la base persistente de `compose.yaml`.

El estado cerrado verifica backend 137/137 y frontend 50/50. Playwright 2/2 continúa siendo el último baseline E2E ejecutado y versionado; ese baseline no cubre S3-I2. S3-I2 fue validado mediante integración backend y la verificación ordinaria.

El baseline integrado mantiene dos escenarios Playwright. El recorrido principal crea un `Product` con precio 10, realiza la Primera Confirmación, cambia el precio a 12, realiza una Confirmación posterior y consulta el `Order`, preservando `Incorporation` 1 a 10 e `Incorporation` 2 a 12. El harness usa PostgreSQL aislado y realiza cleanup de sus procesos y recursos.

`NexoBar.E2E.DatabaseSetup` aplica explícitamente y verifica los tres `DbContext`: `OperationalConfigurationDbContext`, `CatalogDbContext` y `OrderOperationsDbContext`. `HasPendingModelChanges` debe ser `false` para los tres. Chromium se instala manualmente desde `frontend`:

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

## 14. Tooling, Development y migraciones

- .NET SDK `10.0.400`, con roll-forward deshabilitado.
- Node.js `22.13.1` y npm `10.9.2`.
- `package-lock.json` es autoritativo para dependencias frontend y la instalación reproducible usa `npm ci`.
- Docker es necesario para las suites de integración. Chromium de Playwright es necesario solo para `--e2e`.
- E2E requiere libres los puertos técnicos `5028` y `5173`; el harness falla si están ocupados y no mata ni reutiliza procesos ajenos.
- `compose.yaml` proporciona un PostgreSQL local descartable para Development; no representa la topología productiva.
- Fuera de integración y E2E, el flujo técnico general para provisionar y aplicar migraciones aún no está estandarizado como tooling del repositorio. Deberá resolverse cuando exista un driver real de onboarding o deployment.
- `NexoBar.E2E.DatabaseSetup` es parte del harness E2E, no una herramienta general de Development.

## 15. `InternalsVisibleTo`

`InternalsVisibleTo` existe únicamente para consumidores técnicos/test específicos: las suites de integración y `NexoBar.E2E.DatabaseSetup`. No es un mecanismo normal de colaboración productiva entre módulos.

## 16. Deuda consciente y fronteras no materializadas

Deuda técnica conocida:

- la igualdad del destino de las connection strings modulares no se valida automáticamente;
- la validación runtime o generación de contratos TypeScript sigue diferida;
- el tooling general de migraciones para Development sigue pendiente.

Fronteras todavía no materializadas, sin que esta enumeración diseñe su solución:

- progreso de Preparation y sus comandos humanos;
- auth, sesión y autorización;
- instrucciones puntuales de Preparation;
- Correcciones;
- frontend de Preparation.

Actualmente solo la Confirmación origina Work automáticamente; no existen comandos humanos `Start` ni `MarkReady`, y no se afirma que Preparation esté autorizada operacionalmente. Antes de introducir mutaciones humanas de Preparation deberá materializarse la frontera de autorización aprobada. No se definen aquí storage, granularidad, API ni identidad de línea para instrucciones puntuales, ni se diseña S3-I3.

## 17. Protocolo de trabajo

- `AGENTS.md` contiene el contexto operacional persistente para Codex. Ante una decisión no resuelta o una contradicción normativa se detiene la parte afectada y se reporta.
- Los cambios permanecen limitados al objetivo de la tarea y se verifican en proporción al riesgo. No se agregan dependencias, alcance o refactors adyacentes sin autorización.
- No se modifican pruebas para acomodar una implementación incorrecta.
- Commit, push y operaciones Git destructivas requieren instrucción explícita.
