# Testing y verificación

## Testing y verificación

Existen tres capas:

- backend: suites de integración xUnit con PostgreSQL real mediante Testcontainers;
- frontend: Vitest y React Testing Library;
- recorrido integrado real: Playwright sobre Chromium.

`scripts/verify.cmd` y `scripts/verify.sh` ejecutan la verificación ordinaria. Esta verificación requiere Docker porque las suites backend usan Testcontainers. La opción `--e2e` añade PostgreSQL efímero aislado, backend, Vite y Chromium; no usa la base persistente de `compose.yaml`.

S9-I0 queda evidenciado de forma focalizada, sin afirmar una regresión completa de módulo: persistencia/migración de `InstallationProvisioningFact` `8/8`, incluido backfill Legacy, singleton, Down y `HasPendingModelChanges`; servicio de provisioning `11/11`, incluido transacción, login normal, replay, conflicto, duplicado, fallo inyectado, concurrencia, no reactivación y seguridad de secret/result; modo Host `3/3`, incluido selección de subcomando, falta de stdin y startup web ordinario. El checkpoint de proceso dirigido ejecuta el Host real con stdin redirigido sobre PostgreSQL aislado, comprueba `exit 0`, autenticación posterior y `already_initialized` para un segundo command ID, sin servidor web.

S9-I3 queda evidenciado por un checkpoint de proceso dirigido sobre PostgreSQL aislado. Ejecuta `provision-initial-admin` real y comprueba factor de recovery en generación 1; prepara una Identity administradora inicial inactiva para simular que no queda una vía ordinaria utilizable y observa que su login ordinario es rechazado. Ejecuta el Host real `recover-general-configuration` con las dos líneas de stdin redirigido, obtiene `success` con exit 0, autentica con la credencial nueva, verifica que `/api/identity-sessions/current` contiene `GeneralConfiguration` y que `GET /api/identities` autenticado funciona. También comprueba que la credencial y Session anteriores fallan, y que el replay Host exacto devuelve `replayed_success` sin un segundo command durable. El resultado fue **1 checkpoint dirigido: 1 passed, 0 failed, 0 skipped**; no afirma una ejecución del suite backend completo.

El checkpoint vertical dirigido S9-I2 de General Configuration usa dos administradores `GeneralConfiguration` con Sessions independientes, la proyección real `/api/identity-sessions/current`, APIs aseguradas de Identity y el lookup real de `OperationalConfiguration`. Recorre create/rename de una Identity, una responsabilidad representativa, una habilitación de Preparation, configuración de credencial, activate/deactivate, revocación de la segunda vía administrativa, rechazo real de última vía, auto-reemplazo de credencial, transición forzada a login y reingreso con el secret nuevo. El resultado fue **1 spec Playwright dirigido: 1 passed, 0 failed, 0 skipped**; no afirma una ejecución exitosa de la suite Playwright completa.

S7-I2 está implementado, verificado y committed: **OrderOperations 362/362; backend 679/679; frontend último verificado 235/235**, según confirmación del usuario para esta revisión. Son resultados de S7-I2, no pruebas ejecutadas durante esta revisión documental. El resultado anterior consignado era backend 673/673 y Playwright 8/8; este último no se reejecutó aquí. El harness conserva la comprobación `HasPendingModelChanges = false` para los cinco DbContext (5/5).

El baseline integrado conserva recorridos Playwright y checkpoints focalizados. El recorrido principal crea un `Product` con precio 10, realiza la Primera Confirmación, cambia el precio a 12, inicia el marcador autoritativo, realiza una Confirmación posterior y consulta el `Order`, preservando `Incorporation` 1 a 10 e `Incorporation` 2 a 12. Un E2E focalizado de Applied Price Correction (`1/1 passed`) entrega un Content direct de cantidad 1 a precio original/efectivo 10; un actor Catalog cambia el precio a 8 y un actor Order separado comprueba que el Order permanece en 10. La corrección explícita adopta 8, conserva el original, recalcula el Importe funcional a 8 y permite Liquidation con Freeze por 8. Otro escenario verifica que un contexto autorizado ve el marcador remoto y no puede iniciar un segundo; se conserva también el lookup de Pedido inexistente. El escenario operacional de Preparation/Delivery conserva sus actores y cantidades parciales, y Delivery Correction tiene cobertura vertical de Estado efectivo, Historia, Functional Amount y cantidad nuevamente entregable. El escenario de Inventory inicia sesión con una Identity que sólo posee `InventoryConfiguration`, crea un Item y comprueba que no puede operar; luego usa otra Identity que sólo posee `InventoryOperation`, establece la cantidad mediante Count/Reconciliation, registra Entry, ManualExit atravesando cero y Waste, comprueba el saldo vigente negativo y consulta una Historia que contiene los cuatro Movimientos. Esto demuestra capacidades separadas, `null != 0`, balance autoritativo e integración real navegador/backend/PostgreSQL. El fixture no implica bootstrap productivo. El harness usa PostgreSQL aislado y realiza cleanup de sus procesos y recursos.

El cierre de Slice 7 conserva checkpoints E2E focalizados para sus fronteras verticales principales: Delivery Correction, correcciones/cancelaciones de Content y Preparation, OperationalIntervention, Complete Order Cancellation y Applied Price Correction. Son evidencia del recorrido integrado; no reemplazan las reglas de dominio documentadas en sus áreas propietarias.

El vertical SSE de destino de Preparation tiene evidencia focalizada de publicación backend post-commit `7/7`, regresión OrderOperations `687/687`, freshness frontend `14/14` más pruebas afectadas y Playwright de dos navegadores `1/1`. Este último usa dos Identities habilitadas para el mismo destino: B ejecuta Start y A, con su vista y stream ya abiertos, observa los buckets autoritativos actualizados sin reload ni refresh manual. La decisión, alcance y scopes diferidos están en [SSE y frescura multiusuario](../architecture/sse-and-freshness.md).

El vertical SSE de Order activo registra autorización de reads `13/13`, regresión read/API afectada `169/169`, autorización OrderOperations `674/674`, expectativas de migración `2/2`, transporte active Order `49/49`, regresión de transporte Preparation `39/39`, publicación Order `13/13`, regresión publicación Preparation `7/7`, frontend active Order `100 passed` y E2E dirigido `1/1`. El checkpoint de publicación OrderOperations fue `760/762`; los dos reads de migración originalmente fallidos se verificaron individualmente green, sin afirmar una segunda corrida completa `762/762`. El E2E usa A con sólo `OrderOperationsAndBasicClosure` y Delivery abierto/`Deliverable=0`, y B con Preparation/enablement exacto que ejecuta MarkReady; A observa `Deliverable=1` sin reload, Refresh ni reselección y entrega la cantidad 1.

El vertical SSE de Inventory queda evidenciado proporcionalmente por: transporte/autorización backend focalizado `16/16`; regresión Preparation durante el cambio de transporte compartido `39/39`; regresión active Order `49/49`; publicación de Inventory `9/9`; regresión afectada de Count/Reconciliación `50/50`; checkpoint del módulo Inventory `194/194 passed`, TRX `Completed`; freshness frontend de Inventory `46/46`; typecheck frontend `passed`; y E2E multiusuario de Inventory `1/1 passed`.

El E2E de Inventory usa Item aislado con existencia establecida en 10 y dos Identities/Sessions independientes, ambas sólo con `InventoryOperation`. A abre el listado y confirma el stream real `inventory.operation` antes de que B registre Entry +5 por UI. B observa 15 y, sin reload, refresh manual ni re-navegación, A renderiza 15 mediante `commit → inventory.operation.changed → EventSource real → GET /api/inventory/operations/items → UI`. El resultado dirigido fue Playwright `1 discovered`, `1 passed`, `0 failed`, `0 skipped`; no se afirma un exit code no capturado.

Los dos escenarios terminales de Slice 6 recorren Confirmation → Delivery completa de Content directo → Functional Amount 20 → Liquidation → Freeze → Closure explícito. La rama simple declara un medio libre y verifica su trim; la segunda registra cobro gestionado externamente sin medio. Ambas verifican que Liquidation todavía no es Closure, muestran el timestamp recibido, bloquean Composición ordinaria tras Freeze y realizan lookup exacto después de Closure: el Pedido y su Incorporation siguen visibles, sin acciones para continuar, liquidar, cerrar de nuevo o reabrir.

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

## Slice 10 — Operational Intervention on Unavailable Products

S10-I1 tiene evidencia focalizada de la Confirmation excepcional: First `5 passed`, Subsequent `2 passed`, autoridad/idempotencia/replay `5 passed`, migración `2 passed`, `HasPendingModelChanges` `1 passed` e invalidación `1 passed`. La evidencia incluye la distinción `Requested=true` con snapshot no disponible → `Applied=true`, y `Requested=true` con snapshot disponible → `Applied=false`, además de inmutabilidad de Catalog y replay tras revocación de responsabilidades.

El checkpoint final de OrderOperations fue **778 discovered, 778 passed, 0 failed, 0 skipped**. No se afirma éxito de la suite backend completa.

S10-I2 tiene evidencia frontend focalizada: `OrderWorkflow 48/48`, `OrderLookup 13/13`, `App 11/11`, `orderOperationsClient 11/11` y `tsconfig.app.json` typecheck passed. No se afirma un typecheck frontend completo fuera de ese proyecto.

S10-I3 tiene un escenario Playwright focalizado: **1 discovered, 1 passed, 0 failed, 0 skipped**. Actor A, con sólo `OrderOperationsAndBasicClosure`, no ve el Product no disponible y recibe `403 order_operations.confirmation.operational_intervention_required` en un intento HTTP directo. Actor B, con `OrderOperationsAndBasicClosure` + `OperationalIntervention`, lo ve como `isAvailable=false`, no puede usar Add ordinario, usa la acción explícita de intervención, confirma una Composition mixta, obtiene `Applied=true` sólo para esa línea, ve el marcador histórico y deja el Product no disponible en Catalog. No fueron necesarios fixes de producción.

## MVP-FC-PREP — Preparation Configuration Completion

MVP-FC-PREP-I1 deja evidencia frontend focalizada: `GeneralConfigurationPanel` **29 passed**; `CatalogPanel` **18 passed**; cliente general **10 passed**; cliente Catalog **10 passed**; capability gate de `App` **12 passed**; y `tsconfig.app.json` **passed**. Estos resultados no afirman una corrida completa del frontend.

MVP-FC-PREP-I2 ejecutó un escenario Playwright dirigido: **1 discovered, 1 passed, 0 failed, 0 skipped**. Usó actores distintos: G con `GeneralConfiguration`, C con `CatalogConfiguration`, O con `OrderOperationsAndBasicClosure` y P con `Preparation`; probó la propagación del destino exacto desde la configuración hasta el Work visible para P. No afirma éxito de la suite Playwright completa.

## MVP-FC-CAT — Catalog Structure and Product Lifecycle

MVP-FC-CAT-I1 backend evidence:

- Catalog module: **63 discovered, 63 passed, 0 failed, 0 skipped**.
- OrderOperations after lifecycle integration: **783 discovered, 783 passed, 0 failed, 0 skipped**.
- Both deterministic Retire-versus-Confirmation orderings passed.
- PostgreSQL migration Up/Down passed.
- `HasPendingModelChanges` passed.

MVP-FC-CAT-I2 frontend evidence:

- `CatalogPanel`: **32/32 passed**.
- `catalogClient`: **14/14 passed**.
- Exact application typecheck: `npx tsc -p tsconfig.app.json --noEmit` — **PASS**.
- The broader `tsc -b` command is not claimed clean; it retains pre-existing unused `Browser`/`BrowserContext` imports in E2E files.

MVP-FC-CAT-I3 executed one targeted Playwright scenario: **1 discovered, 1 passed, 0 failed, 0 skipped**. It proved Group create, Product Group assignment, rename, active operational browse and Confirmation, Retire, operational exclusion and direct `product_not_current` rejection, administrative persistence, Reactivate with `Available=true`, and operational reappearance.
