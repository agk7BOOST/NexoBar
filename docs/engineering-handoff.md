# NexoBar - Engineering Handoff

## 1. Propósito y autoridad

- Este documento es el baseline técnico actual, versionado y materializado del repositorio.
- No reemplaza las Sources normativas de comportamiento funcional, modelo conceptual, UX, RNF o arquitectura. Ante una discrepancia prevalece la Source aplicable.
- Las decisiones posteriores aprobadas o Adendas sustituyen únicamente el asunto concreto que modifican. Lo explícitamente abierto, parametrizado o provisional continúa abierto.

## 2. Plataforma, repositorio y módulos

- Monorepo Git con backend y frontend como unidades técnicas separadas.
- Backend autoritativo: C# sobre .NET 10 LTS y ASP.NET Core 10. Frontend: React 19, TypeScript estricto y Vite 8.
- El backend es un monolito modular y una unidad principal de despliegue. `NexoBar.Host` es el composition root.
- `Catalog` y `OrderOperations` están implementados. `Inventory`, `IdentitiesAndCapabilities` y `OperationalConfiguration` están presentes como límites, todavía sin implementación funcional.
- La única dependencia modular productiva actual es `OrderOperations -> Catalog`.
- Cada módulo conserva la propiedad de su Estado y colabora mediante capacidades explícitas. No hay ciclos ni un `Shared`/`Common` genérico.
- El flujo materializado soporta Price Change del `Product` vigente, Confirmación inicial y posteriores, múltiples `Incorporations` por `Order`, preservación de precios aplicados históricos distintos por `Incorporation` y consulta completa del `Order`.

## 3. Persistencia

- Una instancia/base PostgreSQL compartida es la persistencia relacional transaccional primaria. EF Core 10 y Npgsql son la estrategia predeterminada.
- Cada módulo que persiste Estado posee su propio `DbContext`: `CatalogDbContext` mapea el schema `catalog` y `OrderOperationsDbContext` mapea `order_operations`. Cada schema mantiene su propio historial de migraciones.
- Las migraciones son explícitas, versionadas y revisables. El Host productivo no ejecuta auto-migrate durante el startup.
- Dinero y cantidades exactas usan `numeric`/`decimal`, nunca coma flotante binaria como representación autoritativa; cuando aplica, su representación HTTP es un decimal string estable.
- Las identidades persistentes principales materializadas usan UUID v7. `Idempotency-Key` usa UUID v4.
- Los timestamps operacionales autoritativos los asigna el backend y se persisten en UTC.
- Estado actual e Historia semántica son conceptos distintos y se confirman atómicamente cuando son consecuencias inseparables. La solución no es CQRS ni Event Sourcing.
- SQL explícito puede usarse cuando una invariante, concurrencia o rendimiento lo justifique. No se usan Repository Pattern genérico ni lazy loading por defecto.

### Invariante de configuración

Las connection strings modulares de `Catalog` y `OrderOperations` DEBEN apuntar a la misma instancia y base PostgreSQL. La colaboración transaccional existente depende de ello. Actualmente es una invariante de configuración documentada, no una validación automatizada.

## 4. Colaboración `OrderOperations -> Catalog`

- `IOrderConfirmationCatalog` es la capacidad pública mínima actual. `Catalog` conserva la propiedad de su Estado; `OrderOperations` no accede a `CatalogDbContext` ni a tablas `catalog.*`.
- Las Confirmaciones comparten una única `DbTransaction` PostgreSQL. `Catalog` reutiliza esa conexión y transacción para su lectura autoritativa, y estabiliza los `Products` mediante `FOR SHARE` hasta commit o rollback.
- Exponer `DbTransaction` en esa interfaz es una excepción técnica deliberada por atomicidad y estabilización; no establece un patrón genérico para todas las colaboraciones entre módulos.

### Price Change y concurrencia con Confirmación

- Price Change es un comando explícito de `Catalog`, no un `PATCH` genérico. Recibe `expectedCurrentPrice` y ejecuta un `UPDATE` condicionado; una expectativa desactualizada responde `409 Conflict`.
- Mantiene idempotencia durable local y el replay devuelve el resultado persistido. No crea Price History ni reescribe `appliedPrice` históricos.
- Confirmación toma los `Products` mediante `FOR SHARE`; Price Change toma un lock de actualización incompatible. El orden efectivo serializa ambas operaciones, y una Confirmación que obtuvo primero el lock conserva como Precio aplicado el valor que leyó.

### Confirmaciones posteriores

- La mutación sobre un `Order` existente expresa la intención de una nueva Confirmación. La `operationalReference` es opaca; el request contiene solo items y no modifica el `Context` del `Order`.
- Cada éxito crea una nueva `Incorporation`, con ordinal sucesivo calculado como `MAX + 1`, una nueva `ConfirmationHistory` y el `appliedPrice` vigente en esa Confirmación.
- `Order FOR UPDATE` es el mecanismo principal para serializar la asignación del ordinal. La restricción única `(order_id, ordinal)` es la defensa física adicional.
- El lock ordering técnico materializado es: advisory idempotency lock → `Order FOR UPDATE` → `Products FOR SHARE` → inserts → commit. Este orden aplica a Confirmaciones posteriores; no debe generalizarse a operaciones futuras sin revisarlas.

## 5. Idempotencia y resultado incierto

- Los comandos materializados reciben un `Idempotency-Key` UUID v4 y mantienen persistencia durable por comando.
- Cada comando/módulo toma un advisory transaction lock local. Efecto e idempotencia se confirman dentro de la misma transacción.
- Misma key y misma intención produce replay; misma key e intención incompatible produce conflicto.
- Ante resultado incierto se reintenta con la misma key. No existe blind retry para mutaciones.
- `Catalog`, Primera Confirmación y Confirmación posterior tienen infraestructura y canonicalización propias; no deben uniformarse sin una decisión explícita.
- En una Confirmación posterior, la intención se define por `Order` e items canonicalizados; el orden del array no la altera. Misma key y misma intención produce replay; misma key e intención diferente produce conflicto.
- El replay posterior reconstruye el resultado desde la Historia de `OrderOperations`; no consulta `Catalog` ni bloquea el `Order`.
- La duplicación local actual es deliberada. No existe infraestructura `Shared` de idempotencia.

## 6. Contratos técnicos materializados

- HTTP ordinario usa HTTPS y JSON; ASP.NET Core Minimal APIs implementa endpoints con handlers delgados.
- Problem Details es la estructura común de errores e incluye códigos estables. OpenAPI describe el contrato técnico implementado, no sustituye su significado normativo.
- Las mutaciones expresan intenciones operacionales específicas, no reemplazos CRUD genéricos del Estado autoritativo.
- `operationalReference` es opaca en HTTP y OpenAPI, aunque actualmente derive internamente del Order ID.
- El lookup de Order se reconstruye exclusivamente desde `OrderOperations`; `Catalog` no reconstruye condiciones históricas.
- Las propiedades JSON autoritativas no reconocidas se rechazan en los comandos donde esta regla está materializada.

## 7. Frontend materializado

- `App` coordina `CatalogPanel`, `OrderWorkflow` y `OrderLookup`; las responsabilidades de catálogo/Price Change, Pedido activo/Composición y consulta están separadas. Un `Order` consultado puede retomarse para una Composición posterior.
- Los recursos se refrescan desde la autoridad: Price Change recarga `Catalog` y las Confirmaciones recargan el `Order`. El cliente no compone manualmente la Historia.
- Ante incertidumbre de un comando, el workflow conserva snapshot e idempotency key y exige una decisión manual de retry o discard.

## 8. Testing y verificación

Existen tres capas:

- backend: suites de integración xUnit con PostgreSQL real mediante Testcontainers;
- frontend: Vitest y React Testing Library;
- recorrido integrado real: Playwright sobre Chromium.

`scripts/verify.cmd` y `scripts/verify.sh` ejecutan la verificación ordinaria. Esta verificación requiere Docker porque las suites backend usan Testcontainers. La opción `--e2e` añade PostgreSQL efímero aislado, backend, Vite y Chromium; no usa la base persistente de `compose.yaml`.

El baseline integrado mantiene dos escenarios Playwright. El recorrido principal crea un `Product` con precio 10, realiza la Primera Confirmación, cambia el precio a 12, realiza una Confirmación posterior y consulta el `Order`, preservando `Incorporation` 1 a 10 e `Incorporation` 2 a 12. El harness usa PostgreSQL aislado y realiza cleanup de sus procesos y recursos.

`NexoBar.E2E.DatabaseSetup` aplica explícitamente las migraciones del entorno E2E y comprueba pending model changes. El estado cerrado verifica backend 93/93, frontend 50/50, Playwright 2/2 y `verify --e2e` completo, sin pending model changes y con cleanup correcto. Chromium se instala manualmente desde `frontend`:

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

## 9. Tooling y prerrequisitos

- .NET SDK `10.0.400`, con roll-forward deshabilitado.
- Node.js `22.13.1` y npm `10.9.2`.
- `package-lock.json` es autoritativo para dependencias frontend y la instalación reproducible usa `npm ci`.
- Docker es necesario para las suites de integración. Chromium de Playwright es necesario solo para `--e2e`.
- E2E requiere libres los puertos técnicos `5028` y `5173`; el harness falla si están ocupados y no mata ni reutiliza procesos ajenos.

## 10. `InternalsVisibleTo`

`InternalsVisibleTo` existe únicamente para consumidores técnicos/test específicos: las suites de integración y `NexoBar.E2E.DatabaseSetup`. No es un mecanismo normal de colaboración productiva entre módulos.

## 11. Development y migraciones

- `compose.yaml` proporciona un PostgreSQL local descartable para Development; no representa la topología productiva.
- Las migraciones permanecen explícitas y el Host no las aplica automáticamente.
- Fuera de integración y E2E, el flujo técnico general para provisionar y aplicar migraciones aún no está estandarizado como tooling del repositorio. Deberá resolverse cuando exista un driver real de onboarding o deployment.
- `NexoBar.E2E.DatabaseSetup` es parte del harness E2E, no una herramienta general de Development.

## 12. Deuda consciente

- La igualdad del destino de las connection strings modulares no se valida automáticamente.
- La validación runtime o generación de contratos TypeScript sigue diferida.

Estas observaciones son deuda técnica conocida; no incluyen funcionalidades deliberadamente diferidas.

## 13. Protocolo de trabajo

- `AGENTS.md` contiene el contexto operacional persistente para Codex. Ante una decisión no resuelta o una contradicción normativa se detiene la parte afectada y se reporta.
- Los cambios permanecen limitados al objetivo de la tarea y se verifican en proporción al riesgo. No se agregan dependencias, alcance o refactors adyacentes sin autorización.
- No se modifican pruebas para acomodar una implementación incorrecta.
- Commit, push y operaciones Git destructivas requieren instrucción explícita.
