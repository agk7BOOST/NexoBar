# E2E

Lee [testing](../../docs/testing/verification.md) y [Development](../../docs/architecture/development.md).

- Si cambias únicamente mecánica del harness (esperas, arranque, puertos o cleanup), consulta el harness y esas guías; añade [persistencia](../../docs/architecture/persistence.md) si afecta conexión, aislamiento o migraciones.
- Si cambias un escenario, fixture o sus expectativas funcionales, añade los AGENTS y documentos de las superficies y módulos cuyo comportamiento se verifica. No es necesario cargar los otros escenarios ni todos sus módulos.

El harness usa PostgreSQL efímero aislado y Chromium. Las cuentas/credenciales del fixture son técnicas; no definen bootstrap productivo. Los scripts existentes gobiernan arranque, puertos y cleanup.
