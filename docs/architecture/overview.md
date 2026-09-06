# Arquitectura y dependencias

## Plataforma, repositorio y módulos

- Monorepo Git con backend y frontend como unidades técnicas separadas.
- Backend autoritativo: C# sobre .NET 10 LTS y ASP.NET Core 10. Frontend: React 19, TypeScript estricto y Vite 8.
- El backend es un monolito modular y una unidad principal de despliegue. `NexoBar.Host` compone los módulos y es el composition root.
- Los módulos superiores son `OrderOperations`, `Catalog`, `Inventory`, `IdentitiesAndCapabilities` y `OperationalConfiguration`. Los cinco están materializados; `Inventory` posee su Estado, operaciones físicas, Historia y superficies web de configuración y operación, mientras `IdentitiesAndCapabilities` posee Estado, sesiones, administración y capacidades públicas de autorización.
- `OperationalConfiguration` ya es un módulo persistente y funcional en el alcance de `PreparationResponsibility`; no tiene todavía un lifecycle completo.
- `Preparation` y `Delivery` son fronteras internas de `OrderOperations`, no módulos top-level. Son dimensiones distintas: `Ready != Delivered`.
- Cada módulo conserva la propiedad de su Estado y colabora mediante capacidades explícitas. No hay ciclos ni un `Shared`/`Common` genérico.

Ningún módulo modifica directamente Estado ajeno. La colaboración entre módulos ocurre dentro del proceso; no introduzcas dependencias cíclicas ni un `Shared`/`Common` genérico preventivo.

### Dependencias modulares materializadas

```text
Host
├─ OrderOperations
├─ Catalog
├─ Inventory
├─ OperationalConfiguration
└─ IdentitiesAndCapabilities

OrderOperations ──→ Catalog
OrderOperations ──→ IdentitiesAndCapabilities
Catalog ──────────→ OperationalConfiguration
Inventory ────────→ IdentitiesAndCapabilities
IdentitiesAndCapabilities ──→ OperationalConfiguration
```

- `Catalog` consume una capacidad pública estrecha de `OperationalConfiguration` para validar la existencia de una `PreparationResponsibility`; no accede a su `DbContext`, schema ni tablas.
- `OrderOperations` depende de `Catalog` y de capacidades públicas estrechas de `IdentitiesAndCapabilities`; no depende directamente de `OperationalConfiguration` ni lo consulta durante una Confirmación.
- `IdentitiesAndCapabilities` consume una capacidad pública estrecha de `OperationalConfiguration` para resolver o validar destinos de Preparation.
- `Inventory` consume capacidades públicas estrechas de `IdentitiesAndCapabilities` para estabilizar autorización y resolver el nombre operacional vigente de los actores de Movimientos; no accede a su `DbContext`, schema ni tablas.
- No existen las dependencias inversas `OperationalConfiguration → IdentitiesAndCapabilities`, `Catalog → OrderOperations` ni `IdentitiesAndCapabilities → OrderOperations`.
- No existen foreign keys ni accesos a `DbContext`, schema o tablas ajenos cross-module. El Host compone las capacidades y sus implementaciones.

## Límites transversales del producto

El sistema es connected-to-authority: no existe modo operacional offline con cola diferida de comandos. SSE será el mecanismo primario servidor-cliente para actualización activa cuando corresponda; no es fuente de verdad y sigue pendiente.

Las distinciones de negocio se consultan en sus áreas: [Confirmation](../order-operations/confirmation.md), [Preparation](../order-operations/preparation.md), [Delivery](../order-operations/delivery.md), [terminación](../order-operations/ending.md), [Catalog](../catalog/README.md), [Inventory](../inventory/README.md) e [Identities](../identities-and-capabilities/security.md).
