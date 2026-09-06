# HTTP e idempotencia transversal

## Idempotencia y resultado incierto

- Los comandos materializados reciben un `Idempotency-Key` UUID v4 y mantienen persistencia durable por comando.
- Cada comando/módulo toma un advisory transaction lock local. Efecto e idempotencia se confirman dentro de la misma transacción.
- Misma key y misma intención produce replay; misma key e intención incompatible produce conflicto.
- Ante resultado incierto se reintenta con la misma key. No existe blind retry para mutaciones.
- `Catalog`, `OperationalConfiguration`, Primera Confirmación y Confirmación posterior tienen infraestructura y canonicalización propias; no deben uniformarse sin una decisión explícita.

- La duplicación local actual es deliberada. No existe infraestructura `Shared` de idempotencia.

## Contratos técnicos materializados

- HTTP ordinario usa HTTPS y JSON; ASP.NET Core Minimal APIs implementa endpoints con handlers delgados.
- Problem Details es la estructura común de errores e incluye códigos estables. OpenAPI describe el contrato técnico implementado, no sustituye su significado normativo.
- Las mutaciones expresan intenciones operacionales específicas, no reemplazos CRUD genéricos del Estado autoritativo.

Consultas pueden ser orientadas a recursos; mutaciones deben expresar intención. Para endpoints o clientes, consultar también las [fronteras de seguridad pendientes](security-boundaries.md).
