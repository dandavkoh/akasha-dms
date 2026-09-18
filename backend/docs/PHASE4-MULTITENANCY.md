# Phase 4 – Multi-Tenancy (Tenant-Isolation als Sicherheitsarchitektur)

**Datum:** 2026-09-18
**Scope:** `backend/src/Akasha.Domain`, `backend/src/Akasha.Application`, `backend/src/Akasha.Infrastructure`, `backend/src/Akasha.Api`, alle drei Testprojekte
**Referenzen:** `/AGENTS.md` (root), `/backend/AGENTS.md` („backend #NN"), `PHASE0-INVENTORY.md` … `PHASE3-PERSISTENCE.md`

Dieses Dokument ist die analoge Aufzeichnung zu Phase 0–3: Anforderungen, umgesetzte Komponenten, Architekturentscheidungen, Verifikation und zurückgestellte Aufgaben.

---

## 1. Ziel und Auftrag

Strikte Tenant-Isolation als zentrale Backend-Sicherheitsarchitektur. Grundregel: Tenant A darf niemals Daten von Tenant B lesen, verändern, löschen oder indirekt über Suche/AI/MCP/Jobs/APIs erkennen.

Kernanforderungen:

- Tenant-Kontext **ausschließlich** aus authentifizierter Identität + serverseitig validierter Zuordnung — nie aus einem rohen Client-`tenantId`-Feld (backend #172.1, #7)
- Systematische Verhinderung von Cross-Tenant Reads/Updates/Deletes auf Persistence-Ebene, nicht durch Entwickler-Disziplin (`Where(TenantId == ...)`) (backend #8, #162)
- Tenant-aware Queries, Commands, Background-Grundlagen, Cache- und Audit-Grundlagen
- Automatisierte Integrationstests inkl. absichtlicher `tenantId`-Manipulation durch den Client
- PostgreSQL RLS nur, wenn sauber vereinbar (backend #164)

---

## 2. Ausgangszustand

Phase 3 brachte: EF Core + Npgsql + Migrationen + Testcontainers, aber ausschließlich die (nicht tenant-scoped) Entität `Tenant`. Es gab: kein TenantMarker, keinen Tenant-Kontext, keine Isolation im DbContext, keine Identität→Tenant-Zuordnung, keine Auth-Middleware, kein Audit.

---

## 3. Umgesetzte Komponenten

| Komponente | Umsetzung |
|--|--|
| Tenant-Marker | `Domain/Common/ITenantScoped.cs` (`Guid TenantId`): Einzige Voraussetzung, damit eine Entität vollautomatisch Isolation erbt (Filter + Save-Guard). Keine aufzählende Konvention pro Entität nötig |
| Identität→Tenant-Zuordnung | `Domain/Tenants/TenantMembership.cs`: `(TenantId, Subject)` mit Unique-Index `(tenant_id, subject)`, Index auf `subject` (Resolution-Hot-Path), FK zu `tenants` (Restrict) |
| Audit-Grundlage | `Domain/Audit/AuditEntry.cs`: tenant-scoped, append-only, FK zu `tenants` mit `Restrict` (Audit überlebt fachliche Löschungen, backend #84, #169) |
| Tenant-Kontext | `Application/Tenancy` → `ITenantContext`/`TenantContext` (scoped DI): `Establish()` ist idempotent für denselben Tenant, **verweigert den Wechsel** auf eine andere TenantId im selben Scope; `Require()` wirft `MissingTenantContextException` |
| Resolution-Abstraktion | `ITenantResolver` (Application, bricht keine Dependency Direction: arbeitet auf `ClaimsPrincipal` aus dem BCL) mit `TenantResolutionFailure` (`Unauthenticated`/`NoMembership`/`Forbidden`/`Ambiguous`) |
| Resolver-Implementierung | `Infrastructure/Tenancy/TenantResolver.cs`: liest Subject aus `ClaimTypes.NameIdentifier`/`sub`, lädt Memberships (bewusst `IgnoreQueryFilters` — zu diesem Zeitpunkt existiert noch kein Tenant; dokumentiert im Code), honoriert ein optional mitgeschicktes `tenant_id`-Claim **nur bei bestehender Membership**, sonst `Forbidden` (Spoofing-Schutz); >1 Membership ohne Auswahl → `Ambiguous` |
| Isolation im DbContext | `AkashaDbContext`: (a) **Global Query Filter** via reflexiver Schleife über alle `ITenantScoped`-CLR-Typen — unaufgelöster Kontext filtert auf `Guid.Empty` → 0 Zeilen (secure by default); (b) **SaveChanges-Guard**: jede geänderte/gelöschte `ITenantScoped`-Entity muss zum aktuellen Tenant gehören, sonst `TenantIsolationViolationException` — fängt auch angehängte/gebundene Entities ab, die am Query-Filter vorbeikommen |
| Filter-Caching | Filter referenziert den Tenant **über die DbContext-Instanz** (`this → _tenantContext.TenantId`), nicht per `Expression.Constant(tenantContext)` — EF Core parameterisiert je Context-Instanz, sonst würde der erste (design-time) Kontext im Model-Cache kleben |
| Cache-Grundlage | `Application/Tenancy/TenantCacheKey.For(tenantId, key)` → `tenant:{id}:…`; lehnt `Guid.Empty` und bereits gepräfixte Keys ab (backend #64) |
| Middleware | `Api/Middleware/TenantResolutionMiddleware.cs`: nach `UseAuthorization()`; ohne authentifizierte Identität passiert sie ohne Kontext (Endpoints prüfen selbst); mapped `TenantResolutionException` → 401/403/409 |
| E2E-Endpoint | `TenantsController.GetCurrent` (`/api/v1/tenants/current`): dünn, nutzt `ITenantContext` + `ITenantQueries` (Application-Port, EF-Implementierung in Infrastructure) — 401 ohne Kontext |
| DI-Verdrahtung | `AddInfrastructure` registriert `ITenantContext` (scoped), `ITenantResolver`, `ITenantQueries`; DbContext erhält `ITenantContext` per Konstruktor aus DI |
| Migration | `Migrations/…_TenantIsolation.cs`: `tenant_memberships` + `audit_entries` (FKs, Unique/Indexes) |
| Design-Time | `DesignTimeDbContextFactory` nutzt einen never-established `DesignTimeTenantContext` (Migrationstooling führt keine Queries aus) |

**Tests:** `TenantIsolationTests` (7), `TenantResolutionTests` (7) in `Akasha.Infrastructure.Tests`; `TenantIsolationApiTests` (5) mit neuem Testcontainers-basiertem `ApiFixture` und `TestAuthHandler` (Header→Claims, strikt nur als Test-Ersatz für den echten IdP).

---

## 4. Architekturentscheidungen

| # | Entscheidung | Begründung / Vorgabe |
|--|--|--|
| P1 | Isolation im DbContext (Filter + Guard), nicht per Abfrage-Konvention | backend #8: Entwickler sollen nicht „sich an jedes Where erinnern müssen". Beide Richtungen abgedeckt: Lesen per Filter, Schreiben/Löschen zusätzlich per Guard (Attach/`Update()` umgeht Filter) |
| P2 | Secure-by-default bei fehlendem Kontext | Offener Kontext liest nichts (Filter auf `Guid.Empty`) und darf nichts Tenant-scoped schreiben. Fehlschluss Richtung Sicherheit, nie Richtung Offenheit |
| P3 | `TenantMembership` als einzige Vertrauensbasis für Resolution | root #13: client-seitige `tenantId` ist Eingabewunsch, kein Recht. Validierung immer gegen die Tabelle |
| P4 | Migration nach SaveGuard-Ausnahme für Design-Time | Migrationstooling bekommt einen expliziten, nicht etablierbaren Kontext — kein Backdoor-Kontext in Produktion |
| P5 | **RLS bewusst zurückgestellt** | EF Global Query Filters + Save-Guard + Integrations-Tests decken alle App-Zugriffe ab; RLS würde `set_config('app.tenant_id',…)` pro Connection/Transaktion erfordern (Connection-Pooling!) und bringt nur Mehrwert, sobald Raw-SQL/Reporting/Batch-Zugriffe außerhalb des DbContext existieren (backend #162: Filter sind nicht „die einzige" Autorisierung, RLS wäre Defense-in-Depth, #164). Wird mit dem ersten nicht-EF-Zugriffspfad fällig |
| P6 | Kein generisches Repository für Tenant-Isolation | Filter+Guard auf EF-Ebene, Use-Case-Ports erst bei Bedarf (backend #77) |
| P7 | `AuditEntry` ohne audit-service/repositories | „Grundlagen": Entität + Schema + Isolation; Schreibpfad folgt mit dem ersten fachlich auditablem Use Case (YAGNI, root #46) |
| P8 | `TenantCacheKey` als einzige Cache-Prämisse | Minimal, aber erzwingt die Sicherheitsform der Keys, sobald der erste Cache existiert (backend #64) |
| P9 | API-Auth noch nicht final | `TestAuthHandler` simuliert signierte Claims nur in Tests; die eigentliche IdP-Anbindung (JWT/OIDC) ist Phase Identity. Der Resolver hängt **nicht** am Auth-Mechanismus, sondern am validierten `ClaimsPrincipal` |
| P10 | ApiFixture auf Testcontainers umgestellt | Isolations-Tests brauchen reale DB; bisheriger `akasha_test`-Localhost-Ansatz war nicht isolierbar |

---

## 5. Verifikation (Phase-4-Abschlusskriterien)

- `dotnet build`: **0 Fehler**
- `dotnet test`: **44/44 grün**
  - 7 Architektur-Tests (Dependency Direction unverändert eingehalten; Application sauber — `ClaimsPrincipal` ist BCL, kein EF/ASP.NET)
  - 18 Infrastructure-Tests (7 TenantIsolation + 7 TenantResolution + bestehende Persistenz)
  - 19 API-Integrationstests (inkl. 5 TenantIsolationApiTests)

Konkret abgedeckte Bedrohungen:

| Bedrohung | Absicherung | Test |
|--|--|--|
| Cross-Tenant Read | Query-Filter | `Tenant_cannot_read_other_tenants_data`, `Queries_without_tenant_context_return_nothing` |
| Cross-Tenant Update (Attach/Bypass) | SaveChanges-Guard | `Tenant_cannot_update_other_tenants_data` |
| Cross-Tenant Delete | SaveChanges-Guard | `Tenant_cannot_delete_other_tenants_data` |
| Schreiben ohne Tenant-Kontext | Guard | `Writes_without_tenant_context_are_rejected` |
| Client manipuliert `tenant_id` | Resolver ↔ Membership | `Spoofed_tenant_id_claim_is_rejected` (403), `Client_supplied_tenant_is_honored_only_with_valid_membership` |
| Mehrfach-Membership | Resolver `Ambiguous` | `Multiple_memberships_require_valid_selection` (409), `Multiple_memberships_without_selection_are_ambiguous` |
| Identität ohne Membership | Resolver `NoMembership` | `Identity_without_membership_is_forbidden` (403) |
| Unauthentifiziert | Middleware skipft, Endpoint ohne Kontext | `Unauthenticated_requests_have_no_tenant_context` (401) |
| nachträglicher Tenant-Wechsel im Scope | `TenantContext.Establish` | `Tenant_context_cannot_be_switched_once_established` |
| Suche/Cache | Filter + `TenantCacheKey` | indirekt über Filter; `Tenant_cache_keys_are_tenant_prefixed` |
| Model-Cache-Falle (statischer Capture) | `this`-Referenz im Filter-Ausdruck | implizit in allen Isolation-Tests |

Regel-Konformanz: kein Client-Trust (backend #172.1), Isolation infra-erzwungen (#8), kein EF/ASP.NET in Domain/Application (#3), kein Raw SQL ohne Parameter, Migrationen versioniert, keine Secrets, no Cross-SaaZ/self-Hosted-Unterschied.

---

## 6. Tech-Debt-Register / zurückgestellt

Bewusst nicht in Phase 4:

### Direkt nachgelagerte Phasen
- **Identity-Phase:** echter IdP (JWT/OIDC/intern) ersetzt den `TestAuthHandler`; Claim-Typ `tenant_id` bleibt der Resolutions-Eingang — kein Resolver-Umbau nötig
- **Fachdomäne (User, Document, Akte, Connector…):** einfach `ITenantScoped` implementieren → Isolation erbt. Kein eigenes Tenant-Handling pro Entität
- **RLS als Defense-in-Depth:** sobald der erste Nicht-EF-Zugriff auf PostgreSQL entsteht (Raw SQL, Reporting, Migrations-Batches) — DSN + `set_config` je Connection/Transaktion, App-DB-User ≠ Table-Owner (backend #164)

### Später
- Background-Job-Host: scoped-`ITenantContext` je Job (Job-Contract trägt TenantId, `Establish` vor Zugriff, backend #33, #158)
- Caching (Identity/Memberships/Entitlements) mit `TenantCacheKey` + Invalidierung
- Audit-Schreibpfad beim ersten auditablem Use Case

### Offene Punkte
- `TenantResolver` ist für Tests `public`; mit der Identity-Phase prüfen, ob er hinter `internal` + `InternalsVisibleTo` zurück kann
- Claims in Produktion: OIDC `sub`-Stabilität je Provider sicherstellen; `ClaimTypes.NameIdentifier`-Mapping pro IdP konfigurierbar halten

---

Phase 4 gilt mit diesem Stand als abgeschlossen. Änderungen sind nicht committet (kein Commit ohne ausdrückliche Anweisung).
