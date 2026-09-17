# Phase 1 – Technische Foundation Backend

**Datum:** 2026-09-16
**Scope:** `backend/src/Akasha.Api` + `backend/tests/Akasha.Api.IntegrationTests`
**Referenzen:** `/AGENTS.md` (root), `/backend/AGENTS.md` („backend #NN"), `PHASE0-INVENTORY.md`

Dieses Dokument ist die analoge Aufzeichnung zu Phase 0: Anforderungen, umgesetzte Komponenten, Architekturentscheidungen, Verifikation und zurückgestellte Aufgaben.

---

## 1. Ziel und Auftrag

Aufbau einer professionellen, stabilen technischen Backend-Basis ohne Fachlogik:

- ASP.NET-Core-Grundstruktur, controller-basierte REST API, Dependency Injection
- OpenAPI, zentrale Fehlerbehandlung, ProblemDetails
- Request-/Correlation-ID, strukturierte Logs, Health Checks
- grundlegende HTTP-Konfiguration, CORS-Grundlage
- Environment-Konfiguration (Development/Production), saubere API-Konventionen

Verbindliche Regeln aus dem Auftrag und backend/AGENTS.md: keine Businesslogik in Controllern (backend #36), Domain ohne ASP.NET/EF/PostgreSQL-Kenntnisse (backend #3), keine Provider-Storage-Logik, keine Secrets im Repository (backend #57), keine sensiblen Dokumentdaten in Logs (backend #58), keine unnötigen Abstraktionen (root #46).

---

## 2. Ausgangszustand

Phase 0 lieferte das Layergerüst (`Akasha.Api` / `Application` / `Domain` / `Infrastructure`), ein minimales Hosting (Controllers, OpenAPI in Dev, `/health`), 7 Architektur-Tests und gepinnte Toolchain (SDK 10.0.401). Es gab: keine zentrale Fehlerbehandlung, keine ProblemDetails, keine Correlation-ID, keine CORS-Konfiguration, keine Production-Logkonfiguration, keine End-to-End-Tests, keine API-Routen-Konvention.

---

## 3. Umgesetzte Komponenten

Alle Komponenten leben ausschließlich im Api-Layer; `Application`/`Domain`/`Infrastructure` bleiben bewusst leer.

| Komponente | Umsetzung |
|--|--|
| Composition Root | `src/Akasha.Api/Program.cs`: Controllers, CORS, OpenAPI, HealthChecks, ProblemDetails; Pipeline in fester Reihenfolge (Correlation → DevPage/OpenAPI → ExceptionHandler → StatusCodePages → HttpsRedirection → CORS → Authorization → Controller/Health) |
| Zentrale Fehlerbehandlung | `AddProblemDetails` + `UseExceptionHandler()` + `UseStatusCodePages()` (Framework-Bordmittel, RFC 9457); unbehandelte Exceptions und leere 4xx-Antworten werden einheitlich als `application/problem+json` serialisiert |
| Correlation-ID | `Middleware/CorrelationIdMiddleware.cs`: übernimmt `X-Correlation-ID` des Clients oder erzeugt eine GUID; schreibt Response-Header, `HttpContext.Items` und einen Log-Scope; reichert ProblemDetails um die Extension `correlationId` an |
| Strukturierte Logs | JSON-Console-Formatter in `appsettings.Production.json` (UTC-Timestamps, Format `O`); Development behält lesbares Format |
| Health Checks | `MapHealthChecks("/health")` (Prozess-Liveness; DB-/Connector-Checks folgen mit den jeweiligen Abhängigkeiten, backend #89) |
| CORS-Grundlage | Policy `Default` aus Konfiguration `Cors:AllowedOrigins`; leer = keine Origins in Production, `http://localhost:5173` in Development |
| API-Konventionen | Route-Präfix `/api/v1/…`, `[ApiController]`, dünner Diagnostics-Controller mit `ProducesResponseType`, `public partial class Program` für Test-Host |
| Environment-Konfiguration | `appsettings.json` (Basis + leere CORS-Origins) / `.Development.json` (Dev-CORS) / `.Production.json` (JSON-Logs) |
| Integration-Tests | `tests/Akasha.Api.IntegrationTests`: `ApiFixture` (`WebApplicationFactory<Program>`, Environment `Testing`) + 9 Tests; einziges neues Paket: `Microsoft.AspNetCore.Mvc.Testing` 10.0.12 (offizielles ASP.NET-Core-Testpaket, nur Test-Projekt) |

**Keine neuen Produktionspakete** — ProblemDetails, Health Checks, CORS, JSON-Logging-Formatter und OpenAPI sind Framework-Bordmittel (Regel 46, keine unnötigen Abhängigkeiten).

---

## 4. Architekturentscheidungen

| # | Entscheidung | Begründung / Vorgabe |
|--|--|--|
| A1 | Alle Foundation-Komponenten im Api-Layer; Application/Domain/Infrastructure unverändert leer | Foundation ist API-Infrastruktur; Fachmodule entstehen erst mit Fachlogik (root #46); Architektur-Tests wachen weiter über die Dependency Direction (Api→Application→Domain, Infrastructure implementiert innere Schichten) |
| A2 | Zentrale Fehlerbehandlung ausschließlich mit Bordmitteln (`UseExceptionHandler` + ProblemDetails) | backend #59 (Fehlerkategorien, keine Internas leaken); kein Exception-Middleware-Bauwerk, das das Framework schon mitbringt |
| A3 | Correlation-ID als eigene, äußerste Middleware (vor ExceptionHandler) | Setzt Header/Items/Log-Scope, bevor irgendein Handler antwortet — auch Fehler-Antworten tragen die ID; kein Drittanbieter-Paket (z. B. CorrelationId-Library) für ~30 Zeilen nötig |
| A4 | ProblemDetails-Extension `correlationId` + Response-Header statt separatem Error-Contract | Kleinstmöglicher, RFC-konformer Fehlerkanal; DTO-Konventionen folgen mit den ersten Fach-Endpunkten |
| A5 | Diagnostics-Controller `SystemController` unter `/api/v1/system`: `ping` (Pipeline/DI-Smoke) und `fail` (wirft absichtlich `InvalidOperationException`) | `fail` ist die kleinste verlässliche Quelle für 500er — verifiziert die Fehlerbehandlung in Integrationstests und als Ops-Smoke-Test; kein Fach-Endpoint, keine Businesslogik |
| A6 | Environment `Testing` für Integrationstests | Deterministisches Verhalten: kein Developer-Page-Plaintext, aber OpenAPI gemappt; CORS-Origin via `UseSetting("Cors:AllowedOrigins:0", …)` injiziert — kein `appsettings.Testing.json` nötig |
| A7 | OpenAPI nur in `Development` und `Testing` gemappt | API-Dokumentation in Production nicht öffentlich exponieren (Security-Mindset, backend #38); kann bei Bedarf pro Deployment aktiviert werden |
| A8 | CORS ohne `AllowCredentials`, Origins ausschließlich konfiguriert | Cookie-basierte Browser-Auth ist nicht Teil der Basis; wird erst mit dem Authentifizierungsmodell entschieden. Leere Origin-Liste = kein CORS-Zugriff (fail-secure) |
| A9 | Route-Konvention `/api/v1/…` festgelegt | Löst die in PHASE0-INVENTORY.md offene Frage (backend #37/#93); Breaking Changes künftig über neue Version, nicht durch stille Semantik-Änderungen |
| A10 | Kein Serilog/OpenTelemetry, kein Rate-Limiting, kein Auth in Phase 1 | Framework-Logging mit JSON-Formatter + Correlation-Scope genügt der Anforderung „strukturierte Logs"; die übrigen Punkte gehören zu Auth-/Ops-Phasen (Backend-Regel: nichts vorzeitig bauen) |

---

## 5. Verifikation (Phase-1-Abschlusskriterien)

- `dotnet build`: **0 Warnungen, 0 Fehler**
- `dotnet test`: **16/16 grün** — 7 Architektur-Tests + 9 Integrationstests
- API-Start: bewiesen durch `WebApplicationFactory` (boots `Program` in jedem Integrationstest)
- Dependency Direction: unverändert erzwungen durch `Akasha.ArchitectureTests` (Api referenziert kein EF Core/Npgsql; Domain/Application bleiben hersteller- und infrastrukturneutral)

Integrationstest-Abdeckung der geforderten Szenarien:

| Anforderung | Test |
|--|--|
| API startet / DI funktioniert | `WebApplicationFactory`-Boot + `Foundation_services_resolve_from_di` (`ICorsService`, `HealthCheckService`, `ILoggerFactory`) + Controller-Aktivierung via `ping` |
| Health Endpoint | `Health_endpoint_reports_healthy` → 200 `"Healthy"` |
| OpenAPI | `OpenApi_document_is_served_and_contains_api_routes` → 200, `application/json`, Route `/api/v1/system/ping` enthalten |
| Fehler konsistent als ProblemDetails | `Unknown_routes_return_problem_details` (404) und `Unhandled_exceptions_return_problem_details_with_correlation_id` (500) → jeweils `application/problem+json` |
| Correlation/Request-ID | Generierung ohne Client-Header, Übernahme einer Client-ID, Echo im Response-Header, `correlationId`-Extension in 500er-ProblemDetails |

Regel-Konformanz: keine Businesslogik im Controller (Diagnostics only), Domain kennt weiterhin weder ASP.NET noch EF (Schicht leer + Architektur-Tests), keine Provider-Storage-Logik vorhanden, keine Secrets in appsettings (nur Log-Level und CORS-Origins), Logs enthalten keine sensiblen Daten (Middleware loggt ausschließlich die Correlation-ID).

---

## 6. Tech-Debt-Register / zurückgestellt

Bewusst nicht in Phase 1 umgesetzt:

### Nächste Backend-Phase (Persistenz & Tenancy)
- EF Core + Npgsql, Design-Time-Setup, versionierte Migrationen (backend #61–#63)
- Tenancy: `TenantId`, serverseitiger TenantContext, EF Global Query Filters, optional RLS (backend #7, #8, #162, #164)
- Authentifizierung + Autorisierungsmodell (backend #9, #38, #40) — `UseAuthorization()` ist derzeit Platzhalter ohne Schema
- Integrationstests mit echtem PostgreSQL (root #40, backend #95)
- DTO-Konventionen (Request/Response-Models) mit dem ersten Fach-Endpoint (backend #131)

### Später
- Rate Limiting (backend #88 — `AddRateLimiter` aus dem Framework, sobald API öffentlich/produktiv)
- HSTS/AllowedHosts-Schärfung mit dem ersten realen Deployment
- OpenTelemetry/Serilog und log-basiertes Request-Tracing, wenn Observability-Anforderungen konkret (Correlation-ID + `traceId` sind bereits vorhanden)
- Docker/Compose + CI-Pipeline
- API-Versionierungs-Paket erst bei tatsächlichem Breaking Change (aktuell: Versionspräfix in der Route)

### Offene Punkte
- Keine kritischen offenen Punkte. Die Production-Logkonfiguration (JSON-Formatter) ist konfigurationsseitig verifizierbar, aber nicht per Integrationstest abgesichert — Verifikung erfolgt im Rahmen des ersten Deployment-Setups.

---

Phase 1 gilt mit diesem Stand als abgeschlossen. Änderungen sind nicht committet (kein Commit ohne ausdrückliche Anweisung).
