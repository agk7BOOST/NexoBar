# Frontend transversal y composición de App

React 19 + TypeScript estricto + Vite 8. Leer únicamente la documentación de la superficie afectada; las migraciones pertenecen al backend.

- `App` coordina `CatalogPanel`, `OrderWorkflow` y `OrderLookup`; las responsabilidades de catálogo/Price Change, Pedido activo/Composición y consulta están separadas. Un `Order` consultado puede retomarse para una Composición posterior mientras su Estado lo permita; Frozen/Closed impide continuar ordinariamente.
- Los recursos se refrescan desde la autoridad: Price Change recarga `Catalog` y las Confirmaciones recargan el `Order`. El cliente no compone manualmente la Historia.
- En reads sensibles a SSE, una invalidación sólo marca stale y provoca GET autoritativo; no muta State de negocio desde el payload. El App mantiene una conexión por Session con el snapshot de scopes actual, refresca al abrir/reconectar y usa fencing por generación por clave de read para que una respuesta anterior no sobrescriba un refresh más nuevo. Los detalles transversales están en [SSE y frescura multiusuario](../architecture/sse-and-freshness.md).
