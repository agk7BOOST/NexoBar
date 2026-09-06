# Preparation frontend

- `PreparationPanel` carga los destinos habilitados por nombre: con cero informa que no hay destinos, con uno lo selecciona automáticamente y con varios presenta selector. Muestra el `ProductOperationalName` vigente, context/reference, instruction y los contadores Total, Pending, En preparación y Ready; no presenta un Status único.
- Cada Work con cantidad Pending ofrece un input integer de Start, inicialmente igual al Pending actual, y el botón “Iniciar”. Cada Work con cantidad InPreparation ofrece un input integer de Ready, inicialmente igual al InPreparation actual, y el botón “Marcar listo”. Ambas acciones admiten la totalidad o una parte del bucket elegible.
- Cuando `ReadyQuantity == TotalQuantity`, el panel muestra “Todo listo”. No usa lenguaje de Delivery.
- Cada Work admite como máximo una mutación frontend activa, en fase mínima `submitting` o `uncertain`; los demás Work siguen operables. Una intención nueva obtiene `crypto.randomUUID()` y congela kind, `WorkId`, quantity y key.
- En `200`, aplica los contadores autoritativos del response, resuelve la intención y refresca después. No realiza mutación optimista.
- Un `409` conocido limpia la intención, informa que cambió el Estado o que existe conflicto de idempotencia y refresca.
- Ante network/timeout con resultado incierto no cambia cantidades locales: conserva exactamente key, body y endpoint, ofrece retry exacto y bloquea una segunda mutación sobre ese Work hasta resolver. No genera una key nueva durante el retry ni ofrece descarte ordinario.
- Un `401` devuelve el frontend a `unauthenticated`, limpia antiforgery e intents y vuelve al login. Un `403` conserva la Identity autenticada y muestra la falla de autorización. Un `404` informa “trabajo ya no disponible” y refresca sin revelar una posible pérdida de enablement.

## Continuidad pendiente de intenciones

- un intent incierto de Preparation vive actualmente solo en memoria: reload o unmount puede perder su key. No hay persistencia en `localStorage` o `sessionStorage`, offline queue ni automatic background retry. El backend conserva idempotencia durable, pero el frontend no garantiza continuidad cross-reload del intent. Es deuda técnica/UX consciente, no una norma.

- persistencia cross-reload de intents inciertos de Preparation;
