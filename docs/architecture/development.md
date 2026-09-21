# Tooling y Development

## Tooling, Development y migraciones

- .NET SDK `10.0.400`, con roll-forward deshabilitado.
- Node.js `22.13.1` y npm `10.9.2`.
- `package-lock.json` es autoritativo para dependencias frontend y la instalación reproducible usa `npm ci`.
- Docker es necesario para las suites de integración. Chromium de Playwright es necesario solo para `--e2e`.
- E2E requiere libres los puertos técnicos `5028` y `5173`; el harness falla si están ocupados y no mata ni reutiliza procesos ajenos.
- `compose.yaml` proporciona un PostgreSQL local descartable para Development; no representa la topología productiva.
- Fuera de integración y E2E, `provision-initial-admin` y `recover-general-configuration` son modos técnicos Host sin servidor HTTP; requieren una base ya migrada y no ejecutan auto-migrate. El primero sólo inicializa una instalación nueva; el segundo requiere el factor de recovery vigente por stdin redirigido. El tooling general para aplicar migraciones continúa pendiente de estandarización para onboarding/deployment.
- `NexoBar.E2E.DatabaseSetup` es parte del harness E2E, no una herramienta general de Development.

## `InternalsVisibleTo`

`InternalsVisibleTo` existe únicamente para consumidores técnicos/test específicos: las suites de integración y `NexoBar.E2E.DatabaseSetup`. No es un mecanismo normal de colaboración productiva entre módulos.

## Pendientes técnicos

- la igualdad del destino de las connection strings modulares no se valida automáticamente;
- la validación runtime o generación de contratos TypeScript sigue diferida;
- el tooling general de migraciones para Development sigue pendiente.
