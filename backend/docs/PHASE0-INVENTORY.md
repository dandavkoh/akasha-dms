# Phase 0 – Architektur-Inventur Backend

**Datum:** 2026-09-16
**Scope:** `backend/` des Akasha-DMS-Repositories
**Referenzen:** `/AGENTS.md` (root, Abschnitte wie „root #35"), `/backend/AGENTS.md` („backend #NN")

Dieses Dokument ist die nachvollziehbare Aufzeichnung der Phase-0-Inventur: Ausgangszustand, Abweichungsanalyse gegenüber den AGENTS-Vorgaben, durchgeführte Korrekturen, Architekturentscheidungen und das bewusst zurückgestellte Tech-Debt-Register für Phase 1+.

---

## 1. Untersuchungsgegenstand

Gelesen und analysiert wurden vollständig:

- `/AGENTS.md` (Produktvision, Architekturprinzipien, Abschnitte 1–48)
- `/backend/AGENTS.md` (Backend-Regeln 1–172)
- Alle vorhandenen Backend-Projektdateien (Template-Projekt `backend.csproj`, `Program.cs`, `WeatherForecast.cs`, `Controllers/WeatherForecastController.cs`, `appsettings*.json`, `Properties/launchSettings.json`, `backend.http`, `.gitignore`)
- `/README.md` (Toolchain: Node 24.13.0 / npm 11.6.2 / .NET SDK 10.0.401)
- Frontend-Struktur (`frontend/app`, `frontend/demo`) – zur Kontextwahrnehmung, nicht Gegenstand der Änderungen
- Git-Historie (2 Commits) und lokales Build-Verhalten

Nicht vorhanden (bestätigt): Solution-Dateien, Tests, Docker-/Container-Konfiguration, PostgreSQL-Anbindung, Authentifizierung, Tenancy-Code, Storage-Abstraktion, CI.

---

## 2. Ausgangszustand (Bestandsaufnahme)

| Bereich | Befund |
|--|--|
| Projektstruktur | Einzelnes ASP.NET-Core-Template-Projekt `backend/` (SDK `Microsoft.NET.Sdk.Web`, net10.0, Package `Microsoft.AspNetCore.OpenApi` 10.0.12) |
| Fachlogik | Nur WeatherForecast-Beispiel (Template-Demo, kein Produktcode) |
| Solution | Keine |
| Tests | Keine |
| Layering | Kein Schichtenmodell; Controller enthielt Demologik (verstößt gegen backend #36, #132) |
| Namenskonvention | Projektname `backend` statt `Akasha.*` (backend #2, #132) |
| Persistenz | Keine (PostgreSQL-Forderung root #33 / backend #61 unbehandelt) |
| Tenancy | Keine Voraussetzungen im Code (root #13 / backend #7, #8) |
| Security | Kein Authentifizierungs-/Autorisierungskonzept (root #27 / backend #38) |
| Deployment | Keine Docker-/Compose-Konfiguration |
| Toolchain | README fordert .NET SDK 10.0.401; lokal war nur SDK 9.0.312 installiert → Baseline-Build schlug fehl (NETSDK1045) |

---

## 3. Abweichungsanalyse gegenüber den AGENTS-Vorgaben

| Prüfpunkt (Vorgabe) | Ist-Zustand vor Phase 0 | Bewertung / Maßnahme |
|--|--|--|
| Layering, Projektstruktur (root #35, backend #2) | Fehlte vollständig | **Blocker für Phase 1 → behoben** (Solution + 4 Layer-Projekte) |
| Dependency Direction (root #36, backend #3, #96) | Nicht vorhanden, nicht prüfbar | **Blocker → behoben** (Referenzgraph + Architektur-Tests) |
| Modularität (root #35, backend #76) | Nicht ansatzweise erkennbar | Struktur geschaffen; funktionale Module entstehen mit Fachlogik ab Phase 1 innerhalb der Layer |
| PostgreSQL-Vorgaben (root #33, backend #61–#63) | Nicht erfüllt, aber auch kein Fachcode vorhanden | Bewusst Phase 1 zugewiesen (Persistenz-Fundament + versionierte Migrationen); Infrastructure-Layer als Zielschema vorhanden |
| Multi-Tenancy-Voraussetzungen (root #13, backend #7, #8) | Keine | Bewusst Phase 1 zugewiesen (TenantId, TenantContext, EF Global Query Filters, optional RLS als Defense-in-Depth); Layerstruktur erlaubt saubere Platzierung |
| Security (root #27, backend #38, #58) | Host ohne Auth; Template ohne Secrets in Konfiguration (ok) | `UseAuthorization()` als Platzhalter im Pipeline; Authentifizierung + Autorisierungsmodell = Phase 1; keine Secrets in appsettings (bestätigt) |
| Testbarkeit (root #40, backend #94–#96) | Keine Tests | Architektur-Tests eingeführt; Integrationstests mit echtem PostgreSQL = Phase 1 (benötigt DB-Infrastruktur) |
| SaaS/Self-hosted-Kompatibilität (root #23) | Template deployment-neutral | Keine Cloud-Features erzwungen, gleiche Codebasis – Vorgabe bereits erfüllbar |
| Offline-Kompatibilität (root #24) | Host ohne externe Laufzeitabhängigkeiten | `/health`, OpenAPI, MVC rein lokal nutzbar – erfüllt |
| Storage-Abstraktion (root #11, backend #14–#16) | Keine | Bewusst zurückgestellt (Storage-Phase); Architektur-Tests verhindern bereits jetzt SDK-Leaks in Domain/Application |
| Keine prematuren Features (root #46) | – | Leitlinie für Phase 0: kein EF Core, kein Provider, kein AI/MCP/Billing, keine Microservices |

Fazit: Die einzige fachlich relevante Abweichung mit Phase-1-Blocker-Charakter war das **fehlende Architekturgerüst** (Schichten, Dependency Direction, testbare Struktur) sowie die **nicht buildbare lokale Toolchain**. Alle übrigen Lücken sind geplante Phaseninhalte, keine Schulden aus falschen Architekturentscheidungen.

---

## 4. Durchgeführte Änderungen

1. **Toolchain**: Installation .NET SDK 10.0.401 (winget, side-by-side; .NET 9 blieb erhalten) gemäß README-Voraussetzung; Baseline-Build des Altprojekts dadurch zunächst grün bestätigt.
2. **`backend/global.json`**: Pinnt SDK 10.0.401, `rollForward: latestFeature`, `allowPrerelease: false` → reproduzierbare Builds für alle Entwickler/CI.
3. **`backend/Directory.Build.props`**: Zentrale Projektproperties (`net10.0`, `Nullable`, `ImplicitUsings`) für alle Backend-Projekte.
4. **Solution `backend/Akasha.slnx`** (SDK-10-Standardformat) mit Layout:
   ```text
   backend/
   ├── Akasha.slnx
   ├── Directory.Build.props
   ├── global.json
   ├── src/
   │   ├── Akasha.Api/            (Composition Root, Hosting, Controller)
   │   ├── Akasha.Application/    (Use Cases, orchestriert Domain)
   │   ├── Akasha.Domain/         (Fachkonzepte, herstellerneutral)
   │   └── Akasha.Infrastructure/ (Persistenz, Connectors, Provider)
   └── tests/
       └── Akasha.ArchitectureTests/
   ```
   Referenzgraph: `Api → Application`, `Api → Infrastructure`, `Application → Domain`, `Infrastructure → Application`, `Infrastructure → Domain`. `Domain` hat keinerlei Referenzen.
5. **WeatherForecast-Sample entfernt** (`WeatherForecast.cs`, `Controllers/`, `backend.http`, Alt-`Program.cs`, Alt-`appsettings*`, Alt-`launchSettings.json`). Begründung: Template-Demo ist kein Produktcode, widerspricht backend #36 (dünne Controller), #132 (Naming) und würde das neue Architekturmodell von Beginn an verwässern. Kein funktionaler Verlust.
6. **Minimaler Api-Host** (`src/Akasha.Api/Program.cs`): Controllers, OpenAPI (nur Development gemappt), `AddHealthChecks` + `MapHealthChecks("/health")` (backend #89), `UseHttpsRedirection`, `UseAuthorization` als Platzhalter. Keine Fachlogik, keine Provider.
7. **Ports 5257/7118 beibehalten** (Kompatibilität zu Doku/`.http`); `launchBrowser: false`; `Akasha.Api.http` mit `/health`- und OpenAPI-Request.
8. **Architektur-Tests** (`tests/Akasha.ArchitectureTests/DependencyDirectionTests.cs`, xUnit): 7 Tests via Assembly-Reflection:
   - Alle Layer ladbar (Smoke)
   - Domain referenziert keine äußeren Layer
   - Domain referenziert keine Infrastruktur-/Provider-Technologien (ASP.NET, EF Core, Npgsql, Graph, SharePoint, Dropbox, OpenAI, Azure, AWS, Google, Paddle, LDAP/Novell, HTTP, Web)
   - Application referenziert weder Infrastructure noch Api
   - Application ebenfalls hersteller-/infrastruktur-neutral
   - Infrastructure referenziert Api nicht
   - Api referenziert Persistenz nicht direkt (backend #36: keine EF-Queries im Controller)
   Bewusst Reflection statt NetArchTest (Regel 133: dependency-light; Regel 46: nichts vorzeitig). Testprojekt enthält `FrameworkReference Microsoft.AspNetCore.App`, damit `Akasha.Api.dll` im Test-Host auflösbar ist.

---

## 5. Architekturentscheidungen

| # | Entscheidung | Begründung / Vorgabe |
|--|--|--|
| A1 | Modular Monolith mit den vier Layern `Api`, `Application`, `Domain`, `Infrastructure` | root #35, backend #2; keine Microservices (root #46) |
| A2 | Api ist der alleinige Composition Root (referenziert Application + Infrastructure); alle anderen Abhängigkeiten zeigen nach innen | root #36, backend #3 |
| A3 | Infrastructure referenziert Application **und** Domain | implementiert innere Abstraktionen, egal ob ein künftiger Kontrakt (z. B. `IStorageConnector`) in Domain oder Application deklariert wird; diese Zuordnung wird bewusst erst in der Storage-Phase entschieden |
| A4 | Domain- und Application-Layer starten bewusst leer | Phase 0 legt nur das Gerüst; Fachkonzepte (Tenant, Document, Akte, …) entstehen mit Phase 1+ dort |
| A5 | Architektur-Tests als dauerhafte, statische Absicherung der Dependency Direction | backend #96 („Architecture tests should prevent unwanted dependencies"); Regeln wachsen mit den Layern mit |
| A6 | `.slnx` statt `.sln` | SDK-10-Standard bei `dotnet new sln`, merge-freundlich; `global.json` garantiert konsistente CLI-Behandlung; alle gängigen .NET-10-Tools unterstützen das Format |
| A7 | Kein EF Core/Npgsql-Paket in Phase 0 | Regel 46 („Do Not Build Prematurely"); Persistenz inkl. versionierter Migrationen ist Phase-1-Inhalt |
| A8 | Health-Endpoint ohne DB-/Connector-Checks | Es existiert noch keine DB; Checks werden mit den jeweiligen Abhängigkeiten ergänzt (backend #89: optionale Integration darf Gesundheitszustand nicht kippen) |
| A9 | Löschen des WeatherForecast-Samples statt Migration in neue Struktur | Demo-Code hat keinen Produktwert; würde Architektur- und Naming-Regeln vom ersten Tag verletzen |

---

## 6. Verifikation (Phase-0-Abschlusskriterien)

- `dotnet build` (Solution): **0 Warnungen, 0 Fehler**
- `dotnet test`: **7/7 Tests grün** (Akasha.ArchitectureTests)
- Smoke-Test laufender Instanz: `GET /health` → 200 `"Healthy"`; `GET /openapi/v1.json` → 200 (OpenAPI 3.1.1)
- Architektur für Phase 1 geeignet: Layer vorhanden, Dependency Direction per Test abgesichert, Toolchain gepinnt, Composition Root betriebsbereit

---

## 7. Tech-Debt-Register / zurückgestellte Aufgaben

Bewusst **nicht** in Phase 0 umgesetzt (gemäß Vorgabe „keine unnötigen Features, nichts Vorzeitiges"):

### Phase 1 (nächste Backend-Phase)
- Persistenz-Fundament: EF Core + Npgsql, DbContext, Design-Time-Setup, versionierte Migrationen (backend #61–#63)
- Tenancy: `TenantId`-Wertobjekt, serverseitiger TenantContext (backend #7), EF Global Query Filters als Isolationsmechanismus (backend #8, #162), PostgreSQL RLS optional als Defense-in-Depth (backend #164)
- Authentifizierung + Autorisierungsmodell-Grundlagen (backend #9, #38, #40)
- Integrationstest-Projekt mit echtem PostgreSQL (root #40, backend #95; benötigt DB/Testcontainers-Infrastruktur)
- CI-Pipeline (Build + Test bei jedem Commit)
- OpenAPI-Auslieferung: Route-Präfix/Versionierung (z. B. `/api/v1`) vor dem ersten echten Endpoint festlegen (backend #37, #93); UI-Tooling (Scalar/Swagger UI) auswählen

### Phase 2+ (Storage)
- `IStorageConnector`-Kontrakt + Capability-Modell (backend #14, #15) – deklariert in Domain oder Application (Entscheidung dann)
- Erster Connector: Local Filesystem (backend #141) inkl. Pfadsicherheit (backend #67)
- Change Detection / Synchronisation / Reconciliation-Grundlagen (backend #18–#21, #100–#104)

### Später (Produktmodule gemäß Roadmap)
- Audit-Modul (backend #56), Identity-Provider-Abstraktion (backend #39), Search (backend #44), Workflows (backend #51), Licensing/Entitlements (backend #53, #54), Billing-Abstraktion (backend #55), AI/MCP (optional, backend #47–#50)
- Docker/Compose-Support (root backend #1 „Docker-friendly")
- CORS-Konfiguration für Frontend-Integration (Frontend nutzt derzeit Mocks; keine Backend-Anbindung vorhanden)
- Analyzer-Härtung (`TreatWarningsAsErrors`, `.editorconfig`) sobald Team-Stilstand definiert ist

### Offene Punkte / dokumentierte Unsicherheiten
- README nennt Swashbuckle nur als NuGet-Beispiel; Projekt nutzt `Microsoft.AspNetCore.OpenApi`. Kein Konflikt – das Doku-UI-Tooling wird bei der API-Phase entschieden.
- Wo genau künftige provider-Abstraktionen (`IStorageConnector`, `IIdentityProvider`, …) deklariert werden (Domain vs. Application), ist in den AGENTS-Dateien nicht abschließend festgelegt; der aufgebaute Referenzgraph unterstützt beide Varianten, Entscheidung erfolgt zum jeweiligen Phasenbeginn.

---

Phase 0 gilt mit diesem Stand als abgeschlossen. Änderungen sind nicht committet (kein Commit ohne ausdrückliche Anweisung).
