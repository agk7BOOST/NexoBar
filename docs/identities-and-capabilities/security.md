# Identity, credencial, sesión y autorización

Identity no significa Responsabilidad. Los contratos de sesión de este documento también gobiernan su consumo frontend.

## Identities & Capabilities

`IdentitiesAndCapabilities` posee `IdentitiesAndCapabilitiesDbContext`, el schema `identities_and_capabilities` y migration history propia. No adopta roles genéricos ni ASP.NET Identity completo.

### Identity, responsabilidades y habilitaciones

El Estado vigente de `Identity` contiene:

- `Id` UUID v7;
- `OperationalName` y `NormalizedOperationalName`;
- `IsActive`.

`OperationalName` no es unique. `FunctionalResponsibility` es un repertorio cerrado de siete códigos:

```text
OrderOperationsAndBasicClosure
OperationalIntervention
Preparation
CatalogConfiguration
InventoryOperation
InventoryConfiguration
GeneralConfiguration
```

`ResponsibilityAssignment` tiene PK compuesta `(IdentityId, ResponsibilityCode)`. `PreparationEnablement` tiene PK compuesta `(IdentityId, PreparationResponsibilityId)`; el destino es un UUID externo opaco, sin FK cross-module, y su vigencia se representa por existencia. La habilitación es independiente de la asignación `Preparation`: preparar o actuar sobre un destino requiere simultáneamente una Identity activa, `Responsibility.Preparation` y la `PreparationEnablement` exacta.

### Credencial local

`LocalCredential` está separada de `Identity` en una relación 1:1 (como máximo una credencial por Identity). Contiene `LoginIdentifier`, `NormalizedLoginIdentifier` unique y `SecretVerifier`; el locator no exige email. El login identifier aplica trim exterior y normalización invariant de case. El secret no se normaliza.

`SecretVerifier` es un wrapper estrecho sobre `PasswordHasher<T>`, admite rehash cuando el verificador lo requiere y nunca persiste el secret crudo.

### Sesiones opacas y política provisional

`IdentitySession` contiene `Id` UUID v7, `IdentityId`, `TokenHash`, `CreatedAt`, `LastActivityAt`, `AbsoluteExpiresAt` y `RevokedAt` nullable. El token entregado al cliente usa 256 bits de RNG y Base64URL; solo se persiste su SHA-256, nunca el token crudo. Una Identity puede mantener varias sesiones.

Logout revoca únicamente la sesión actual. Desactivar una Identity invalida su autoridad y revoca sus sesiones activas. Set/Replace Local Credential conserva la Identity y sus capacidades, y revoca todas las sesiones del target.

La implementación usa como **hipótesis técnica provisional**, no como requisito normativo: inactividad máxima de 30 minutos, lifetime absoluto de 12 horas, expiración exacta cuando `now >= límite`, refresh throttled de `LastActivityAt` aproximadamente cada minuto y `AbsoluteExpiresAt` fijado al crear la sesión. `PAR-SEC-02` continúa abierto normativamente. El frontend no duplica timers; el backend es la autoridad de expiración.

### Cookie, antiforgery y contratos de sesión

En configuración production-like, la cookie de sesión es `__Host-nexobar-session`, `HttpOnly`, `Secure`, `SameSite=Strict`, `Path=/`, sin `Domain` y sin `Max-Age`/`Expires`: es una session cookie. Development y tests usan nombre y configuración explícitos compatibles con HTTP local y no simulan el prefijo `__Host-` cuando `Secure=false`.

`GET /api/security/antiforgery` es un endpoint técnico anónimo que entrega el request token para el header `X-NexoBar-CSRF`; no crea una sesión. El frontend conserva ese token solo en memoria. El session token nunca está disponible a JavaScript ni se guarda en `localStorage` o `sessionStorage`.

Los contratos materializados son:

- `POST /api/identity-sessions`: recibe `loginIdentifier` y `secret`, requiere antiforgery, responde un `401` genérico `invalid_credentials` cuando las credenciales no pueden usarse, revoca solo la sesión actual que ya estuviera representada por el cookie jar al reemplazarla, crea una sesión nueva y no afecta otras sesiones;
- `GET /api/identity-sessions/current`: autenticado; devuelve solo `identityId` y `operationalName`, sin `sessionId`, capabilities ni token;
- `DELETE /api/identity-sessions/current`: requiere antiforgery, revoca la sesión actual, limpia la cookie y devuelve `204`; es idempotente según la implementación actual;
- `GET /api/security/antiforgery`: anónimo y puramente técnico.

El pipeline de autenticación es:

```text
request
→ cookie
→ SHA-256
→ lookup de IdentitySession
→ RevokedAt
→ expiración absoluta
→ inactividad
→ Identity.IsActive
→ principal/contexto del request
```

`AuthenticatedContext` contiene únicamente `IdentityId` y `SessionId`. No hay responsibility claims, enablement claims, roles ni snapshots de capabilities en la sesión.

### Estabilización transaccional de autorización

Para operaciones que requieren autoridad estabilizada, el módulo caller abre una transacción PostgreSQL, `IdentitiesAndCapabilities` adopta su `DbTransaction`, revalida y bloquea selectivamente Session e Identity y, cuando corresponde, filas de capabilities; luego el caller ejecuta la consulta o mutación autorizada. Se usa `READ COMMITTED`, transacciones cortas y `FOR SHARE` en el Estado positivo materializado. No hay transacción distribuida.

### Destinos de Preparation de la Identity actual

`GET /api/identity-sessions/current/preparation-destinations` devuelve únicamente las habilitaciones de la Identity actual como `preparationResponsibilityId + operationalName`. Requiere autenticación, Identity activa y `Responsibility.Preparation`; Preparation sin habilitaciones devuelve `200 []`.

La resolución de nombres usa un batch lookup estrecho de `OperationalConfiguration`; no concede lectura universal de ese módulo.

- El Estado de autenticación es explícito: `loading`, `unauthenticated` o `authenticated(currentIdentity)`. Login envía `loginIdentifier + secret` con antiforgery, muestra el `401` genérico y limpia el secret al tener éxito. La barra de sesión muestra el `OperationalName` actual y permite logout/cambiar persona.

Parámetros normativos, seguridad global y administración aún pendiente: [fronteras de seguridad](../architecture/security-boundaries.md).
