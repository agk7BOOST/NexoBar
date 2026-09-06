# Testing y verificación

## Testing y verificación

Existen tres capas:

- backend: suites de integración xUnit con PostgreSQL real mediante Testcontainers;
- frontend: Vitest y React Testing Library;
- recorrido integrado real: Playwright sobre Chromium.

`scripts/verify.cmd` y `scripts/verify.sh` ejecutan la verificación ordinaria. Esta verificación requiere Docker porque las suites backend usan Testcontainers. La opción `--e2e` añade PostgreSQL efímero aislado, backend, Vite y Chromium; no usa la base persistente de `compose.yaml`.

S7-I2 está implementado, verificado y committed: **OrderOperations 362/362; backend 679/679; frontend último verificado 235/235**, según confirmación del usuario para esta revisión. Son resultados de S7-I2, no pruebas ejecutadas durante esta revisión documental. El resultado anterior consignado era backend 673/673 y Playwright 8/8; este último no se reejecutó aquí. El harness conserva la comprobación `HasPendingModelChanges = false` para los cinco DbContext (5/5).

El baseline integrado mantiene ocho escenarios Playwright. El recorrido principal crea un `Product` con precio 10, realiza la Primera Confirmación, cambia el precio a 12, inicia el marcador autoritativo, realiza una Confirmación posterior y consulta el `Order`, preservando `Incorporation` 1 a 10 e `Incorporation` 2 a 12. Otro escenario verifica que un contexto autorizado ve el marcador remoto y no puede iniciar un segundo; se conserva también el lookup de Pedido inexistente. El escenario operacional de Preparation/Delivery conserva sus actores y cantidades parciales, y Delivery Correction tiene cobertura vertical de Estado efectivo, Historia, Functional Amount y cantidad nuevamente entregable. El escenario de Inventory inicia sesión con una Identity que sólo posee `InventoryConfiguration`, crea un Item y comprueba que no puede operar; luego usa otra Identity que sólo posee `InventoryOperation`, establece la cantidad mediante Count/Reconciliation, registra Entry, ManualExit atravesando cero y Waste, comprueba el saldo vigente negativo y consulta una Historia que contiene los cuatro Movimientos. Esto demuestra capacidades separadas, `null != 0`, balance autoritativo e integración real navegador/backend/PostgreSQL. El fixture no implica bootstrap productivo. El harness usa PostgreSQL aislado y realiza cleanup de sus procesos y recursos.

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
