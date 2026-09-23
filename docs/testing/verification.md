# Testing y verificación

## Testing y verificación

## MVP-FC-TOH — Terminal Order History

Terminal Order History — **CLOSED**. Evidencia backend registrada en I1: proyección terminal focalizada **5/5**, pruebas terminales existentes **10/10**, suite final de OrderOperations **812/812**; `HasPendingModelChanges` ya había pasado en I1 y `git diff --check` pasó. Evidencia frontend registrada en I2: corrida focalizada **4 archivos / 55 pruebas**, `npx tsc -p tsconfig.app.json --noEmit` pasó exactamente y `git diff --check` pasó. Esos resultados backend/frontend son del checkpoint I1/I2 y no se reejecutaron para este cierre.

I3 ejecutó sólo los escenarios Playwright focalizados de [Terminal Order History](../../frontend/e2e/terminal-order-history.spec.ts): **2 discovered, 2 passed, 0 failed, 0 skipped**. Closure usa OABC para crear, entregar, liquidar y cerrar; demuestra el 404 del endpoint activo y consulta History por referencia exacta. Complete Cancellation aparece como terminación distinta, sin Liquidation ni Closure, y sin controles mutantes. GeneralConfiguration sin OABC no recibe el entry point. No representa la suite Playwright completa ni la suite frontend completa.

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

## MVP-FC-AVAIL — Product Availability Intervention

MVP-FC-AVAIL-I1 backend quedó verificado con la cobertura focalizada de Availability:

- `ProductAvailabilityApiTests`: **5 discovered, 5 passed, 0 failed, 0 skipped**;
- Availability-vs-Availability: **5/5**;
- S10 / Confirmation focalizado: **13/13**;
- Confirmation↔Availability focalizado: **4/4**;
- Catalog module: **84 discovered, 84 passed, 0 failed, 0 skipped**.

La evidencia incluye el read estrecho autorizado sólo por `OperationalIntervention`, el comando actor-aware, replay exacto y conflicto de intención, no-op durable, conflicto de concurrencia, Product no actual/inexistente, antiforgery, migración Up/Down, preservación de `IsAvailable` y `HasPendingModelChanges=false`.

MVP-FC-AVAIL-I2 frontend quedó verificado con **28/28** pruebas enfocadas y `npx tsc -p tsconfig.app.json --noEmit` **PASS**. La cobertura incluye cliente estrecho, cambios true→false y false→true, gating independiente en App, reload autoritativo, stale refresh, Products no actuales/inexistentes, retry incierto con intención exacta y separación de lifecycle/S10.

MVP-FC-AVAIL-I3 ejecutó un único escenario Playwright dirigido: **1 discovered, 1 passed, 0 failed, 0 skipped**. Probó el flujo completo sobre PostgreSQL aislado: `OperationalIntervention` marca P no disponible; el actor ordinario deja de verlo y recibe `product_unavailable` sin referencia de Order; el actor dual usa la acción S10 explícita y confirma; el estado comprometido contiene `UnavailableProductExceptionApplied=true`; I vuelve a marcar P disponible; el browse ordinario lo muestra nuevamente; y la lectura posterior del Order conserva el marcador histórico en `true`.

No se afirma éxito de la suite completa de Playwright ni de la suite completa del frontend.

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


## MVP-FC-INV-MC — Inventory Movement Correction

MVP-FC-INV-MC — **CLOSED**. PostgreSQL real: pruebas focalizadas de corrección, autoridad, replay/intención cambiada, secuencia/delta, cero, Balance, Retire, Reconciliation posterior, Count invalidado y competencia obsoleta **8/8**; migración Inventory Up/Down y `HasPendingModelChanges=false` incluidas. Suite completo del módulo Inventory: **222/222**, proceso finalizado correctamente con código **0**.

Frontend: `InventoryHistory.test.tsx` **12/12** y `npx tsc -p tsconfig.app.json --noEmit` **PASS**. E2E dirigido `inventory-movement-correction.spec.ts`: **1 discovered, 1 passed, 0 failed, 0 skipped** con PostgreSQL aislado y migraciones canónicas; el harness verificó `HasPendingModelChanges=false` para los cinco módulos. `git diff --check` **PASS**.

## MVP-FC-INV-LU — Inventory Element Lifecycle, Unit Correction y eligible Delete

MVP-FC-INV-LU-I1A verificó lifecycle y corrección de Unit en backend: Inventory **201 discovered, 201 passed, 0 failed, 0 skipped**; migración Up/Down **PASS**; `HasPendingModelChanges=false`.

MVP-FC-INV-LU-I1B verificó Delete: API **10/10**, concurrencia real PostgreSQL **2/2**, Inventory **213 discovered, 213 passed, 0 failed, 0 skipped**; migración Up/Down **PASS**; `HasPendingModelChanges=false`.

MVP-FC-INV-LU-I2 verificó frontend focalizado: `inventoryClient` **11/11**, Configuration **26/26**, Operation/Freshness/History **26/26**, `tsconfig.app.json` **PASS**. MVP-FC-INV-LU-I2-FIX verificó la inclusión de Elements retirados en el read autoritativo de Configuration: **8/8**.

MVP-FC-INV-LU-I3 ejecutó dos escenarios Playwright dirigidos: **2 discovered, 2 passed, 0 failed, 0 skipped**. C, con sólo `InventoryConfiguration`, creó E en U1 sin existencia, corrigió U1→U2 antes de History, comprobó la guía de reemplazo después de History, retiró y reactivó el mismo ID con recargas autoritativas de Configuration. O, con sólo `InventoryOperation`, vio E en U2 mientras esperaba Reconciliation, estableció 8 U2, registró Entry y llegó a 10 U2; después de Retire dejó de verlo y, tras Reactivate, estableció existencia de nuevo en 11 U2 con un Count nuevo y llegó a 12 U2 con otra Entry. History se mantuvo entendible bajo U2. El escenario separado creó D sin Movement, mostró Retire y Delete como acciones distintas, completó confirmación explícita, confirmó su desaparición persistente en Configuration y su ausencia en Operation. No se probó reutilización de nombre por E2E; su cobertura es backend y no se atribuye aquí al navegador.

Durante I3 se corrigió una omisión de UI: el card operacional no mostraba la Unit mientras la existencia estaba sin establecer. Después del cambio, `InventoryPanel.test.tsx` quedó **26/26** y la ejecución dirigida Playwright quedó **2/2**. No se afirma un typecheck `tsconfig.app.json` específico de I3; el PASS registrado corresponde al checkpoint I2. La corrida fue focalizada: no se afirma éxito de Playwright completo ni del frontend completo.

## MVP-FC-CTX — Context Configuration and Order Context Change: CLOSED

La evidencia siguiente corresponde a checkpoints focalizados previamente ejecutados; esta actualización documental no ejecutó builds, tests ni Playwright.

**CTX-I1A — backend y upgrade.** OperationalConfiguration: **9/9**; OrderOperations final: **790/790**. Pasó la prueba PostgreSQL real de upgrade/import/replay de Context legacy, el runner productivo de migraciones sobre una base vacía y `HasPendingModelChanges=false` para los modelos afectados.

**CTX-I1B — Context Change backend.** Pruebas PostgreSQL focalizadas: **5/5**; OrderOperations final: **795/795**. Pasaron la concurrencia A→B contra A→C, ambos ordenamientos de carrera Context Change/Liquidation, migration Down/Up y `HasPendingModelChanges=false`.

**CTX-I2 — frontend.** `OrderWorkflow` **50 passed**; cliente/UI de administración de Context **5 passed**; cliente OrderOperations **13 passed**; UI de Context Change **9 passed**; freshness de Preparation **1 passed**; gates de App **4 passed**; `npx tsc -p tsconfig.app.json --noEmit` **PASS**; `git diff --check` **PASS**. No son resultados del frontend completo.

**CTX-I3 — fixture y E2E dirigido.** DatabaseSetup actualizado para sembrar Context configurado antes de sus Orders; los Orders usan ID/nombre actuales y ConfirmationHistory guarda ID/nombre snapshot, sin compatibilidad agregada a producción. El proyecto DatabaseSetup compiló con **0 errores y 0 warnings**; el setup aislado terminó y los cinco DbContext quedaron con `HasPendingModelChanges=false`. El único spec Playwright descubrió **1**, pasó **1**, falló **0** y omitió **0**.

El E2E usa G sólo con `GeneralConfiguration`, O sólo con `OrderOperationsAndBasicClosure` y P con `Preparation`. G crea A/B por la UI real; O hace First Confirmation en A por selector configurado y el request no usa texto libre; P observa el Work bajo A. O cambia ese mismo Order A→B manteniendo referencia y Content; P observa B en el mismo Work y conserva Product, cantidad, destino donde la UI lo expone y progreso. O confirma un segundo Order distinto que también usa B. La Liquidation congela el Order original y retira la acción Context Change. La aserción explícita de la versión final espera Context B en P después de la invalidación/refresh. Esto es evidencia E2E dirigida: no afirma una suite Playwright completa ni History terminal o lifecycle de Context.
