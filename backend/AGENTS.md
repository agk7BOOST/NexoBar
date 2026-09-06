# Backend

C# sobre .NET 10 LTS / ASP.NET Core 10. Mantén nullable reference types y warnings como errores, salvo excepción localizada y justificada.

- Antes de cambiar backend, lee [arquitectura y dependencias](../docs/architecture/overview.md) y el AGENTS del proyecto afectado en `src`.
- Para Estado, EF, SQL, concurrencia o migraciones, lee [persistencia](../docs/architecture/persistence.md). Para endpoints/comandos, lee [HTTP e idempotencia](../docs/architecture/http-and-idempotency.md) y [fronteras de seguridad](../docs/architecture/security-boundaries.md).
- Para configuración, ejecución, visibilidad técnica o provisión, lee [Development](../docs/architecture/development.md). No confundas el harness E2E con bootstrap productivo.
- Las pruebas están en proyectos hermanos de `src`: sigue [tests/AGENTS.md](tests/AGENTS.md) para cargar el módulo y tema que verifican.
- Comandos y criterio de verificación: [testing](../docs/testing/verification.md). Los archivos grandes `*Module.cs` concentran endpoints y `*DbContext.cs` mapping; localiza primero el handler o entidad pertinente. Designers y snapshots se consultan cuando la tarea afecta persistencia.
