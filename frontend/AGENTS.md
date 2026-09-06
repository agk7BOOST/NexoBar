# Frontend

React 19 + TypeScript estricto + Vite 8. La representación autoritativa de dinero/cantidades exactas no usa coma flotante binaria; conserva el contrato recibido.

- Antes de cambiar una superficie, lee su AGENTS en `src/catalog`, `src/identity`, `src/orderOperations`, `src/preparation`, `src/delivery` o `src/inventory`. Las lecturas de comportamiento se activan si el cambio afecta lógica, textos semánticos, cantidades, acciones o contratos.
- Para CSS puramente visual y cambios de presentación sin efecto semántico, bastan las instrucciones de la ruta y la estructura/componente afectado.
- Para `App.tsx`, navegación o coordinación entre paneles, lee [frontend transversal](../docs/frontend/README.md) y los AGENTS de las superficies afectadas.
- Para clientes HTTP o intenciones inciertas, lee [HTTP e idempotencia](../docs/architecture/http-and-idempotency.md), [contratos de sesión](../docs/identities-and-capabilities/security.md) y el documento local de la superficie. Para fórmulas de cantidades efectivas, consulta el apartado [Q/R/F implementado](../docs/order-operations/confirmation.md#q-r-y-f-s7-i2).
- El sistema es connected-to-authority y SSE sigue pendiente: [límites transversales](../docs/architecture/overview.md). Las migraciones se consultan solo cuando la tarea también afecte persistencia backend.
- Toolchain e instalación reproducible: [Development](../docs/architecture/development.md). Verificación: [testing](../docs/testing/verification.md); scripts reales en [package.json](package.json). `e2e` tiene sus [instrucciones](e2e/AGENTS.md).
