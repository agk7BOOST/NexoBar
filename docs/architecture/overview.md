# Arquitectura y dependencias

## Plataforma, repositorio y módulos

- Monorepo Git con backend y frontend como unidades técnicas separadas.
- Backend autoritativo: C# sobre .NET 10 LTS y ASP.NET Core 10. Frontend: React 19, TypeScript estricto y Vite 8.
- El backend es un monolito modular y una unidad principal de despliegue. `NexoBar.Host` compone los módulos y es el composition root.
- Los módulos superiores son `OrderOperations`, `Catalog`, `Inventory`, `IdentitiesAndCapabilities` y `OperationalConfiguration`. Los cinco están materializados; `Inventory` posee su Estado, operaciones físicas, Historia y superficies web de configuración y operación, mientras `IdentitiesAndCapabilities` posee Estado, sesiones, administración y capacidades públicas de autorización.
- `OperationalConfiguration` es un módulo persistente y funcional para `PreparationResponsibility` y Context configurado. Ambos tienen lifecycle mínimo de rename, retiro/reactivación y Delete físico elegible; ordering y estaciones permanecen diferidos. Sus capacidades de participación/referencias se definen en OperationalConfiguration y son implementadas por los módulos propietarios, sin dependencias inversas de proyecto.
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
- `OrderOperations` depende de `Catalog` y de capacidades públicas estrechas de `IdentitiesAndCapabilities`; resuelve Context configurado por una capacidad estrecha de `OperationalConfiguration`, sin leer su `DbContext`, schema o tablas ni usar FK cross-module.
- `IdentitiesAndCapabilities` consume una capacidad pública estrecha de `OperationalConfiguration` para resolver o validar destinos de Preparation.
- `Inventory` consume capacidades públicas estrechas de `IdentitiesAndCapabilities` para estabilizar autorización y resolver el nombre operacional vigente de los actores de Movimientos; no accede a su `DbContext`, schema ni tablas.
- No existen las dependencias inversas `OperationalConfiguration → IdentitiesAndCapabilities`, `Catalog → OrderOperations` ni `IdentitiesAndCapabilities → OrderOperations`.
- Product Delete consume una capacidad pública estrecha `IConfirmedProductParticipation` definida en Catalog e implementada por OrderOperations, conectada mediante DI en el Host. No introduce dependencia de proyecto `Catalog → OrderOperations` ni acceso de Catalog a tablas de OrderOperations.
- Identity Delete consume `IOrderFunctionalIdentityAttribution` y `IInventoryFunctionalIdentityAttribution`, definidas en IdentitiesAndCapabilities e implementadas por los módulos propietarios de Historia. La composición DI conecta las capacidades sin dependencias inversas de proyecto ni consultas cross-schema desde Identity.
- No existen foreign keys ni accesos a `DbContext`, schema o tablas ajenos cross-module. El Host compone las capacidades y sus implementaciones.

## Límites transversales del producto

El sistema es connected-to-authority: no existe modo operacional offline con cola diferida de comandos. SSE es el mecanismo primario servidor-cliente para actualización activa cuando corresponda, no es fuente de verdad y sus decisiones de Slice 8 están en [SSE y frescura multiusuario](sse-and-freshness.md). El MVP opera una única instancia backend activa para su fan-out SSE; es una limitación de despliegue, no una invariante de dominio.

Las distinciones de negocio se consultan en sus áreas: [Confirmation](../order-operations/confirmation.md), [Preparation](../order-operations/preparation.md), [Delivery](../order-operations/delivery.md), [terminación](../order-operations/ending.md), [Catalog](../catalog/README.md), [Inventory](../inventory/README.md) e [Identities](../identities-and-capabilities/security.md).
