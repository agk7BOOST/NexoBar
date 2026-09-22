# Tooling y Development

## Tooling, Development y migraciones

- .NET SDK `10.0.400`, con roll-forward deshabilitado.
- Node.js `22.13.1` y npm `10.9.2`.
- `package-lock.json` es autoritativo para dependencias frontend y la instalación reproducible usa `npm ci`.
- Docker es necesario para las suites de integración. Chromium de Playwright es necesario solo para `--e2e`.
- E2E requiere libres los puertos técnicos `5028` y `5173`; el harness falla si están ocupados y no mata ni reutiliza procesos ajenos.
- `compose.yaml` proporciona un PostgreSQL local descartable para Development; no representa la topología productiva.
- `NexoBar.Host` no modifica el schema al arrancar: no ejecuta `Migrate`, `MigrateAsync` ni `EnsureCreated`. Las migraciones productivas se ejecutan como etapa explícita y separada con `dotnet run --project backend/src/NexoBar.Migrations/NexoBar.Migrations.csproj`; el despliegue debe completar esa etapa antes de iniciar o actualizar la aplicación.
- `NexoBar.Migrations` usa las mismas claves `ConnectionStrings:<módulo>` que Host y comparte sus archivos `appsettings*.json`. En Development, `ASPNETCORE_ENVIRONMENT=Development` activa la configuración local existente. CI/deployment inyecta las claves `ConnectionStrings__<módulo>` como configuración secreta, sin incluir credenciales en scripts.
- El orden canónico es `OperationalConfiguration` → `IdentitiesAndCapabilities` → `Catalog` → `Inventory` → `OrderOperations`. Es secuencial y fail-fast: si falla un módulo, no se intentan los posteriores. Una nueva ejecución aplica sólo migraciones pendientes según el historial EF propio de cada módulo. El despliegue asume una sola etapa de migración activa por base.
- El orden respeta las dependencias de runtime materializadas: Catalog e Identity usan OperationalConfiguration; Inventory usa Identity; OrderOperations usa Catalog e Identity. Además, OperationalConfiguration precede OrderOperations para que futuras migraciones de Context puedan leer su State durante el backfill, manteniendo cada escritura dentro del schema propietario de la migración.
- Fuera de integración y E2E, `provision-initial-admin` y `recover-general-configuration` son modos técnicos Host sin servidor HTTP; requieren una base ya migrada y no ejecutan auto-migrate. El primero sólo inicializa una instalación nueva; el segundo requiere el factor de recovery vigente por stdin redirigido.
- `NexoBar.E2E.DatabaseSetup` es parte del harness E2E, no una herramienta general de Development.

## `InternalsVisibleTo`

`InternalsVisibleTo` existe únicamente para consumidores técnicos/test específicos: las suites de integración, `NexoBar.E2E.DatabaseSetup` y el runner explícito de migraciones para aplicar las migraciones de cada módulo sin hacer públicos sus DbContext. No es un mecanismo normal de colaboración productiva entre módulos.

## Pendientes técnicos

- la igualdad del destino de las connection strings modulares no se valida automáticamente;
- la validación runtime o generación de contratos TypeScript sigue diferida.
