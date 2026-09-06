# Delivery frontend

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
- Cada Content con `DeliveredQuantity > 0` expone la acción explícita “Corregir entrega”. La intención congela endpoint, target, body e `Idempotency-Key`; ante un resultado incierto, el retry reutiliza exactamente esos mismos valores. Tras éxito, el frontend refresca Delivery y Order para mostrar el Estado y Functional Amount autoritativos, sin aritmética optimista.

## Continuidad pendiente de intenciones

- los intents inciertos de Delivery también viven solo en memoria: reload o unmount puede perder target, quantity e idempotency key. No existen `localStorage`, `sessionStorage`, IndexedDB, offline queue ni background retry. El backend conserva idempotencia durable, pero el frontend no garantiza continuidad cross-reload. Esta deuda es especialmente relevante porque Delivery representa un hecho físico; se registra sin convertirla aquí en requisito normativo ni resolverla.

- persistencia cross-reload de intents inciertos de Delivery;
