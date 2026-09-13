# Inventory frontend

### Inventory frontend

- `InventoryPanel` presenta superficies independientes de **Configuración de Inventario** y **Estado actual de Inventario**. Cada una carga su read model y expone por separado su `403`; una Identity de configuración puede crear/listar Items sin aparentar autoridad operacional y una Identity de operación puede consultar/actuar sin aparentar autoridad de configuración.
- La vista de configuración crea Items con nombre y unidad operacional. La vista operacional distingue “Existencia no establecida” (`null`) de una existencia establecida en `0`, muestra cantidad y unidad autoritativas y emite una advertencia visible de inconsistencia cuando el saldo es negativo.
- Cada Item operacional permite registrar un Conteo y luego reconciliar exactamente esa observación. También expone Entry, ManualExit y Waste sólo una vez establecida la cantidad. No calcula aritmética optimista: tras una mutación usa el resultado autoritativo y refresca el listado.
- Cada intención nueva congela kind, Item, body e `Idempotency-Key`. Ante network, timeout o `5xx` incierto conserva esos mismos datos, bloquea otra acción sobre el Item y ofrece reintentar la misma operación con la misma key. Un conflicto conocido resuelve la intención y refresca; no se presenta como éxito.
- **Movimientos** muestra la Historia paginada, separa Reconciliation de Entry, ManualExit y Waste, muestra actor con su nombre operacional vigente, timestamp, efecto y saldos, y distingue el establecimiento inicial sin diferencia ficticia. Puede actualizarse manualmente y se refresca automáticamente después de una mutación autoritativa del Item abierto.
- Aún no hay SSE implementado. El vertical aprobado suscribirá sólo el listado operacional montado a `inventory.operation`: la invalidación opaca obligará a releer `GET /api/inventory/operations/items` con fencing/coalescing por generación, sin mutar cantidades desde SSE. Un Conteo retenido podrá presentarse stale al diferir su revisión de `AsOfMovementRevision`; backend conserva la validación. No se agregan SSE de configuración, Historia ni Items individuales. Los intents inciertos viven sólo en memoria y una recarga puede perder la key; no existe cola offline ni retry automático en background.

El transporte compartido es `NotificationSseProvider` / `NotificationSseTransport`. El coordinator de reads reutilizable aún tiene naming de Preparation y debe neutralizarse antes de reutilizarse para Inventory; es deuda de mantenibilidad, no semántica.

## Continuidad pendiente de intenciones

- los intents inciertos de Inventory también viven sólo en memoria: reload o unmount puede perder kind, Item, body e idempotency key. El backend conserva idempotencia durable, pero el frontend no garantiza continuidad cross-reload.
