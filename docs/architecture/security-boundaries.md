# Autoridad de seguridad y fronteras pendientes

## Autoridad y actores

- En los comandos humanos autenticados materializados, el backend obtiene el actor de la Identity autenticada y estabilizada; el cliente no elige el actor durable. `IdentityId` es el actor, `SessionId` solo identifica la sesión.
- Sesión utilizable e Identity activa son precondiciones de esas operaciones, también del replay. Las capacidades se consultan en Estado vigente; no se derivan de claims persistidos en sesión. Cada intención nueva exige la capacidad y, cuando corresponde, habilitación exacta definidas por su módulo.
- Un replay de efecto ya confirmado sigue la regla local documentada: no reinterpreta su Historia por una revocación posterior de capacidad. No generalices ese tratamiento a intenciones nuevas.
- El alcance de protección existente es explícito: los endpoints anónimos pendientes de retrofit no se consideran protegidos por esta guía. Las reglas completas de cookie, antiforgery, credenciales, sesión y estabilización están en [Identities](../identities-and-capabilities/security.md); leerlas cuando la tarea afecte esos mecanismos.

## Pendientes

Este inventario no define políticas nuevas ni afirma seguridad global completa.

- retrofit global de autenticación/autorización para endpoints todavía anónimos, según corresponda: Catalog, OperationalConfiguration, lookup de Order y otros endpoints funcionales actuales no cubiertos. Confirmaciones ya tienen el retrofit de Slice 6;
- bootstrap productivo de Identity y credenciales;
- implementación de recovery extraordinario (`AD-SEC-01`) y UX de recovery ordinario;
- decisión normativa de parámetros de timeout (`PAR-SEC-02`) y política cuantitativa de brute-force/lockout;
- frontend administrativo completo;
- elegibilidad de Delete Identity y coordinación con Historia;
- auditoría global de seguridad y consumo de autenticación en SSE;

- profundidad de Historia administrativa según los OPEN-TRA aplicables;
