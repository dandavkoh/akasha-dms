# Phase 2 – Konfigurations- und Infrastructure-Grundlage

**Datum:** 2026-09-17
**Scope:** `backend/src/Akasha.Infrastructure`, `backend/src/Akasha.Api` (Konfiguration), `backend/tests/Akasha.Api.IntegrationTests`
**Referenzen:** `/AGENTS.md` (root), `/backend/AGENTS.md` („backend #NN"), `PHASE0-INVENTORY.md`, `PHASE1-FOUNDATION.md`

Dieses Dokument ist die analoge Aufzeichnung zu Phase 0/1: Anforderungen, umgesetzte Komponenten, Architekturentscheidungen, Verifikation und zurückgestellte Aufgaben.

---

## 1. Ziel und Auftrag

Professionelle Konfigurations- und Infrastructure-Grundlage ohne Fachlogik:

- strongly typed Options, saubere Konfigurationsklassen, Konfigurationsvalidierung
- Environment Overrides (Docker-kompatibel), sichere Behandlung von Secrets
- Infrastructure Dependency Registration als einziger Registrierungsort
- Connection-String-Grundlage, Development/Production-Trennung, Logging-Konfiguration

Ausdrücklich **nicht** in Scope: Paddle, AI Provider, SharePoint, Dropbox, LDAP, Entra, MCP — und grundsätzlich keine Fachmodule. Das Ziel ist ausschließlich die technische Vorbereitung für PostgreSQL, Storage Connectors, Identity Provider, externe Services und Background Jobs.

Verbindliche Regeln: keine Secrets im Code oder in Logs (backend #57, #58), keine Environment-spezifischen Hacks, keine Infrastructure-Abhängigkeiten im Domain-Projekt (backend #3), strong typing statt Magic Strings (backend #86), Konfigurationsvalidierung am Boundary (backend #78), nichts vorzeitig bauen (root #46).

---

## 2. Ausgangszustand

Phase 1 brachte die API-Foundation (ProblemDetails, Correlation-ID, CORS aus Roh-Konfiguration, Health Checks, OpenAPI, Environment-Dateien, 16 Tests). Es gab: keine typisierten Options (CORS wurde als `string[]` aus der Section gelesen), keine Konfigurationsvalidierung (fehlende/leere Werte fielen erst zur Laufzeit auf), keinen Infrastructure-Registrierungspunkt, keine Connection-String-Grundlage, kein dokumentiertes Secrets-Handling. `Akasha.Infrastructure` enthielt keinen Code.

---

## 3. Umgesetzte Komponenten

| Komponente | Umsetzung |
|--|--|
| Strongly typed Options | `src/Akasha.Infrastructure/Configuration/DatabaseOptions.cs` (`ConnectionString`, `CommandTimeoutSeconds`, `SectionName`-Konstante statt Magic String) und `Configuration/CorsOptions.cs` (`AllowedOrigins`, `IsOriginAllowed`) |
| Konfigurationsvalidierung | `ValidateDataAnnotations()` (`[Required]`, `[Range]`) + zusätzliches `Validate(...)` mit verständlicher Fehlermeldung für fehlenden Connection String; `ValidateOnStart()` → Fehlkonfiguration failt beim Boot, nicht erst beim ersten Request |
| Infrastructure DI-Registration | `src/Akasha.Infrastructure/DependencyInjection.cs`: `AddInfrastructure(this IServiceCollection, IConfiguration)` als einziger Einstiegspunkt; `Program.cs` ruft nur noch diese Methode für den Infrastructure-Layer |
| Composition Root | `Program.cs`: CORS-Policy wird aus typisiertem `CorsOptions` aufgebaut statt aus `Get<string[]>()` |
| Environment Overrides | Framework-Bordmittel: ASP.NET Core Environment-Variables-Provider (`Database__ConnectionString`, `Cors__AllowedOrigins__0` etc.) — Docker-/Compose-kompatibel ohne Zusatzcode; Konvention überall identisch (`:` ↔ `__`) |
| Connection-String-Grundlage | `appsettings.json`: `"Database": { "ConnectionString": "" }` mit JSON-Kommentar, dass echte Credentials ausschließlich via Environment-Variable oder Secrets-Store zu liefern sind; `appsettings.Development.json`: passwortloser lokaler Default (`Host=localhost;Database=akasha_dev;Username=postgres`) |
| Development/Production-Trennung | Development: lesbare Logs, Dev-CORS, Dev-Connection; Production: JSON-Console-Logs (Phase 1), leere CORS-Origins, Connection String nur extern |
| Secrets-Handling | Kein Secret im Repository; Secrets-Transport per Env-Var/User-Secrets; `DatabaseOptions.ConnectionString` ist dokumentiert als nie zu loggen; `CorsOptions.IsOriginAllowed` als erste typsichere, testbare Konfigurationslogik |
| Tests | `tests/Akasha.Api.IntegrationTests/ConfigurationTests.cs` (5 Tests); `ApiFixture` injiziert Test-Connection-String via `UseSetting` (gleicher Provider-Mechanismus wie Env-Vars) |

**Neue Pakete (nur Akasha.Infrastructure):** `Microsoft.Extensions.Options.ConfigurationExtensions` 10.0.12 und `Microsoft.Extensions.Options.DataAnnotations` 10.0.12 — offizielle Framework-Pakete für Options-Binding und -Validierung.

---

## 4. Architekturentscheidungen

| # | Entscheidung | Begründung / Vorgabe |
|--|--|--|
| B1 | Options-Klassen liegen in `Akasha.Infrastructure.Configuration`, nicht im Api-Projekt | Konfiguration ist ein Infrastructure-Concern (backend #6, #86); der Api-Layer konsumiert die gebundenen Werte, definiert sie aber nicht; Domain/Application bleiben konfigurationsfrei |
| B2 | `AddInfrastructure(IConfiguration)` als einzige Registrierungsmethode des Infrastructure-Layers | Verhindert verstreute `Configure<…>`-Aufrufe in `Program.cs` (backend #45: keine duplizierte Config-Logik); jede künftige Infrastructure-Komponente (EF Core, Connectors, Jobs) hängt sich hier an — Composition Root bleibt dünn |
| B3 | Validierung via DataAnnotations + `ValidateOnStart` statt eigener Validator-Infrastruktur | Framework-Bordmittel; fail-fast beim Start deckt fehlende Secrets sofort auf, statt erst zur Laufzeit eines Tenant-Requests (backend #78, #136) |
| B4 | Kein eigenes Secrets-Framework, keine Secret-Redaction-Middleware | Bordmittel (Env-Vars, User-Secrets, ggf. später Azure Key Vault / Docker Secrets über dieselbe Config-Pipeline) genügen; Regel ist konventionell durchgesetzt: appsettings enthalten keine Credentials, dokumentiert im JSON-Kommentar (backend #57) |
| B5 | Connection-String-Override in Tests via `UseSetting`, nicht via `appsettings.Testing.json` | Gleicher Konfigurationspfad wie Produktion (Environment-Override), kein weiteres Config-File; Test-String enthält lokale Dummy-Credentials ohne reales Passwort |
| B6 | Kein EF Core/Npgsql, kein DB-Health-Check, kein Serilog in Phase 2 | Auftrag verlangt „Grundlage/Connection-String", nicht Persistenz (root #46); `AddDbContext` + Npgsql-Health-Check folgen in der ersten Persistenz-Phase, sobald ein DbContext existiert (backend #89); eingebautes JSON-Console-Logging aus Phase 1 genügt |
| B7 | Background-Job-/Connector-/Identity-Konfiguration nur als vorhandener Extension Point (dieselbe Options/Pipeline), keine Options-Klassen dafür | Es gibt noch keine Implementierungen, die Werte bräuchten (YAGNI); das Muster (`XOptions` + `AddInfrastructure`-Block + Validation) ist etabliert und testgetrieben wiederholbar |
| B8 | `IsOriginAllowed` als einzige Logik in Options-Klasse | Kleiner, testbarer Helper direkt an der gebundenen Config; keine geratene `CorsPolicyEvaluator`-Abstraktion für eine Prüfung |

---

## 5. Verifikation (Phase-2-Abschlusskriterien)

- `dotnet build`: **0 Warnungen, 0 Fehler**
- `dotnet test`: **21/21 grün** — 7 Architektur-Tests + 14 Integrationstests (9 bisherige aus Phase 1 + 5 neue Konfigurationstests)
- Dependency Direction: unverändert erzwungen durch `Akasha.ArchitectureTests` — `Akasha.Infrastructure` referenziert nur Framework-Options-Pakete + Application/Domain; Domain/Application unverändert hersteller- und infrastrukturneutral; Api referenziert kein EF Core/Npgsql

Konfigurationstest-Abdeckung:

| Anforderung | Test |
|--|--|
| Strongly typed Binding | `Database_options_are_bound_and_validated_from_configuration` (In-Memory-Config → `DatabaseOptions`) |
| Defaults | `Database_options_use_defaults_when_not_configured` (`CommandTimeoutSeconds = 30`) |
| Validierung fail-fast | `Missing_database_connection_string_fails_options_validation` → `OptionsValidationException` |
| Environment-/Override-Pfad | `Environment_variable_style_override_reaches_validated_options` (Fixture injiziert `Database:ConnectionString`; derselbe Mechanismus wie `Database__ConnectionString` in Docker) |
| CORS-Konfiguration | `Cors_options_bind_allowed_origins` inkl. Origins-Match-Negativfall |

Regel-Konformanz: keine Secrets im Repository (geprüft: alle appsettings ohne Passwörter/Schlüssel), keine Secrets in Logs (Connection String wird nirgends geloggt; Logs enthalten weiterhin nur Correlation-ID), keine Environment-Hacks (eine Config-Pipeline für alle Umgebungen), Domain ohne Infrastructure-Abhängigkeit, Konfiguration isoliert testbar (Binding + Validierung ohne HTTP-Host).

---

## 6. Tech-Debt-Register / zurückgestellt

Bewusst nicht in Phase 2 umgesetzt:

### Nächste Backend-Phase (Persistenz)
- EF Core + Npgsql-Registrierung im vorhandenen `AddInfrastructure`, verbraucht dann `DatabaseOptions` (backend #61)
- Health Check für PostgreSQL (`AddNpgSql` o. ä.), sobald ein DbContext existiert (backend #89)
- Versionierte Migrationen, Design-Time-Factory (backend #63)

### Später
- Options-Klassen für Storage Connectors / Identity Provider / Background Jobs — erst mit der ersten Implementierung (Muster steht: `Akasha.Infrastructure.Configuration` + `AddInfrastructure`-Block + Validation + Tests)
- Secrets-Provider-Integration (z. B. Azure Key Vault / Docker Secrets) in der Deployment-Phase — Config-Pipeline ist dafür bereits offen
- `IOptionsMonitor`-basiertes Hot-Reload, sobald ein Wert zur Laufzeit änderbar sein muss (aktuell: Start-Zeit-Bindung, fail-fast)
- Serilog/OpenTelemetry (unverändert aus Phase 1 zurückgestellt)

### Offene Punkte
- Keine kritischen offenen Punkte. Die fail-fast-Validierung beim Boot ohne Connection String ist im Integrationstest-ServiceProvider bewiesen, nicht im echten Kestrel-Start-Pfad — wird mit dem ersten Compose-/Deployment-Setup verifiziert.

---

Phase 2 gilt mit diesem Stand als abgeschlossen. Änderungen sind nicht committet (kein Commit ohne ausdrückliche Anweisung).
