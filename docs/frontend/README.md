# Frontend transversal y composición de App

React 19 + TypeScript estricto + Vite 8. Leer únicamente la documentación de la superficie afectada; las migraciones pertenecen al backend.

- `App` coordina `CatalogPanel`, `OrderWorkflow` y `OrderLookup`; las responsabilidades de catálogo/Price Change, Pedido activo/Composición y consulta están separadas. Un `Order` consultado puede retomarse para una Composición posterior mientras su Estado lo permita; Frozen/Closed impide continuar ordinariamente.
- Los recursos se refrescan desde la autoridad: Price Change recarga `Catalog` y las Confirmaciones recargan el `Order`. El cliente no compone manualmente la Historia.
