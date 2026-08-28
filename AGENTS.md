# NexoBar - Instrucciones persistentes para Codex

## Proposito

Este repositorio implementa NexoBar.

Codex trabaja sobre un producto cuyo comportamiento funcional, modelo conceptual, UX, requisitos no funcionales y arquitectura ya fueron definidos fuera del repositorio mediante Sources normativas del Project.

Este archivo es contexto operacional para trabajar en el repositorio. No reemplaza esas Sources ni autoriza a inventar decisiones ausentes.

## Autoridad y decisiones

- No inventes comportamiento funcional, conceptual, UX, RNF o arquitectonico.
- Si una tarea requiere resolver una cuestion no definida o encuentra una contradiccion normativa, deten la parte afectada y reportala.
- Una decision posterior o Adenda aplicable prevalece sobre la formulacion anterior unicamente en el asunto que modifica.
- Los documentos historicos no son fuente de decisiones tecnicas actuales.
- No conviertas una conveniencia de implementacion en una decision normativa.
- Las cuestiones explicitamente abiertas, provisionales o parametrizadas continuan abiertas hasta que se proporcionen decisiones aplicables.

## Guardrails arquitectonicos

- Backend autoritativo: C# sobre .NET 10 LTS y ASP.NET Core 10.
- Frontend web: React 19 + TypeScript + Vite 8.
- Repositorio unico con frontend y backend como unidades tecnicas separadas.
- El backend es un monolito modular y una unidad principal de despliegue.
- Los cinco modulos superiores son:
  - Operacion de Pedidos
  - Catalogo
  - Inventario
  - Identidades y Capacidades
  - Configuracion Operacional
- Cada modulo es propietario de su Estado y no modifica directamente el Estado propiedad de otro modulo.
- La colaboracion entre modulos ocurre mediante capacidades explicitas dentro del proceso.
- No introduzcas dependencias ciclicas entre modulos.
- PostgreSQL es la base relacional transaccional primaria compartida.
- Estado vigente e Historia semantica son conceptos distintos; cuando sean consecuencias inseparables deben confirmarse atomicamente.
- No es CQRS ni Event Sourcing.
- Las mutaciones representan intenciones operacionales especificas; no sustituciones CRUD genericas del Estado autoritativo.
- El sistema es connected-to-authority: no existe modo operacional offline con cola diferida de comandos.
- SSE sera el mecanismo primario servidor-cliente para actualizacion activa cuando corresponda; no es fuente de verdad.

## Distinciones semanticas de alto riesgo

Preserva siempre estas diferencias:

- Composicion no es Pedido.
- La primera Confirmacion crea Pedido e Incorporacion.
- Confirmaciones posteriores crean nuevas Incorporaciones.
- Pendiente, En preparacion y Listo son estados conceptualmente distintos.
- Listo no significa Entregado.
- Correccion no significa Cancelacion.
- Liquidacion no significa Cierre.
- Producto no significa Elemento de Inventario.
- Identidad no significa Responsabilidad.
- Estado vigente no significa Historia.
- Inventario no deriva automaticamente Movimientos desde ventas durante el MVP.

## Persistencia y contratos

- EF Core 10 + Npgsql es la estrategia de persistencia predeterminada.
- Cada modulo superior posee su propio DbContext y mapping sobre la base PostgreSQL compartida.
- No introduzcas Repository Pattern generico ni lazy loading por defecto.
- SQL explicito puede utilizarse cuando una invariante, concurrencia o rendimiento lo justifique.
- Las migraciones son explicitas, versionadas y revisables; la aplicacion productiva no migra silenciosamente la base al arrancar.
- HTTP ordinario usa HTTPS + JSON.
- Consultas pueden ser orientadas a recursos; mutaciones deben expresar intencion.
- ASP.NET Core Minimal APIs es la base de endpoints.
- Los errores HTTP usan Problem Details como estructura comun.
- OpenAPI describe el contrato tecnico implementado, no sustituye el significado normativo.
- Los comandos persistentes usan identidad tecnica e idempotencia conforme a su diseno aprobado.
- Dinero y cantidades exactas no usan coma flotante binaria como representacion autoritativa.

## Calidad y forma de trabajo

- TypeScript debe permanecer estricto.
- El backend mantiene nullable reference types y warnings como errores salvo excepcion localizada y justificada.
- No anadas dependencias sustanciales, frameworks o tooling no autorizados por la tarea.
- Manten los cambios limitados al proposito principal de la tarea.
- No realices refactorizaciones adyacentes solo porque parezcan convenientes; reportalas aparte.
- No modifiques una prueba existente simplemente para acomodar una implementacion incorrecta.
- Ejecuta las verificaciones aplicables antes de declarar terminada una tarea.
- Si una verificacion falla, diagnostica la causa; no ocultes ni ignores el fallo.

## Git y autonomia

- Los cambios de Codex son candidatos hasta ser revisados por el usuario.
- No hagas git commit ni push salvo instruccion explicita.
- No realices reset --hard, rebase destructivo, limpieza destructiva ni reescritura del historial salvo instruccion explicita.
- Respeta cambios existentes que no pertenezcan a tu tarea.
- Si completar una tarea requiere ampliar materialmente su alcance, detente y explica la necesidad.

## Finalizacion de una tarea

Al terminar una tarea, informa de forma concisa:

1. que cambiaste;
2. que verificaciones ejecutaste y su resultado;
3. que quedo deliberadamente fuera del alcance;
4. cualquier bloqueo, riesgo o decision pendiente que hayas encontrado.
