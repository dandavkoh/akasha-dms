# Phase 3 – PostgreSQL-Persistenz

**Datum:** 2026-09-17
**Scope:** `backend/src/Akasha.Domain`, `backend/src/Akasha.Infrastructure` (Persistence), `backend/src/Akasha.Api` (Migration-Bootstrap), `backend/tests/Akasha.Infrastructure.Tests`
**Referenzen:** `/AGENTS.md` (root), `/backend/AGENTS.md` („backend #NN"), `PHASE0-INVENTORY.md`, `PHASE1-FOUNDATION.md`, `PHASE2-CONFIGURATION.md`

Dieses Dokument ist die analoge Aufzeichnung zu Phase 0–2: Anforderungen, umgesetzte Komponenten, Architekturentscheidungen, Verifikation und zurückgestellte Aufgaben.

---

## 1. Ziel und Auftrag

Professionelle relationale Persistenzschicht auf PostgreSQL etablieren:

- EF Core + Npgsql als einziger Datenbank-Stack (kein SQLite, kein MySQL, keine zweite Datenbank)
- DbContext, Entity-Mapping, versionierte Migrationen, Connection-Handling, Transaktionen
- Vorbereitung der späteren Fachdomäne (Tenant, User, Group, Role, Permission, Document, Akte, Storage Connector, Audit) **ohne** diese Domäne bereits zu implementieren
- Besonders geprüft: Transaktionsgrenzen, Concurrency-Vorbereitung, Tenant-Isolation-Vorbereitung, Performance-Grundlagen

Verbindliche Regeln: Domain bleibt EF-frei (backend #3, #61), Infrastructure implementiert Persistenz (backend #6), FK/Unique/Index/Constraints wo fachlich erforderlich (backend #62, #165–#167), Migrationen versioniert und offline-sicher (backend #63), keine generischen Repositories aus Gewohnheit (backend #77), nichts vorzeitig bauen (root #46).

---

## 2. Ausgangszustand

Phase 2 brachte die Konfigurationsgrundlage (`DatabaseOptions` mit validiertem Connection String und `CommandTimeoutSeconds`, `AddInfrastructure` als einziger DI-Einstieg). Es gab: kein EF Core, keinen DbContext, keine Entitäten, keine Migrationen, kein Datenbank-Testfundament, kein lokales dotnet-ef-Tool.

---

## 3. Umgesetzte Komponenten

| Komponente | Umsetzung |
|--|--|
| Erste Entität (Anchor) | `src/Akasha.Domain/Tenants/Tenant.cs`: `Id` (`Guid.CreateVersion7()`, zeitlich sortierbar → Index-Lokalität), `Name` (≤200), `Slug` (≤100), `CreatedAtUtc`; Invarianten im Konstruktor, `Rename()` als Verhalten; keine EF-Attribute, privater EF-Konstruktor — Domain bleibt persistenzfrei |
| DbContext | `src/Akasha.Infrastructure/Persistence/AkashaDbContext.cs`: `DbSet<Tenant>`, Konfiguration ausschließlich per `ApplyConfigurationsFromAssembly` |
| Entity-Mapping | `Persistence/Configurations/TenantConfiguration.cs`: PK, `Name`/`Slug` required + MaxLength, **Unique-Index auf `slug`**, `created_at_utc` als `timestamp with time zone`, **`xmin` (PostgreSQL-Systemspalte, `xid`) als RowVersion** → optimistische Concurrency ohne eigenes Versionsfeld in der Domäne |
| Naming | `EFCore.NamingConventions` → snake_case für Tabellen/Spalten (`tenants`, `created_at_utc`); zentrales `DbContextOptionsBuilderExtensions.UseAkashaNpgsql(...)` (public), damit DI, Design-Time-Factory und Tests identisches Setup haben |
| DI-Registrierung | `DependencyInjection.AddInfrastructure`: `AddDbContext<AkashaDbContext>` aus validiertem `DatabaseOptions` (Connection String + Command Timeout) — einziger Registrierungsort |
| Migrationen | `Persistence/Migrations/20260917115151_InitialSchema.cs` (via lokalem dotnet-ef-Manifest, `backend/dotnet-tools.json`); `Persistence/DesignTimeDbContextFactory.cs` liest optional `Database__ConnectionString` |
| Migration-Bootstrap | `Persistence/DatabaseMigration.MigrateAkashaDatabaseAsync()` (public extension auf `IServiceProvider`, EF-Typen bleiben in Infrastructure); `Program.cs` führt sie nur bei `Database:MigrateOnStartup=true` aus (Development: true, default/Testing: false) — idempotent, offline-fähig, Migrationen shippen mit der Anwendung (backend #63) |
| Optionen | `DatabaseOptions.MigrateOnStartup` ergänzt |
| Tests | Neues Projekt `tests/Akasha.Infrastructure.Tests`: Testcontainers (`postgres:17-alpine`) mit `MigrateAsync()` beim Start — Migrationen werden damit gegen echtes PostgreSQL verifiziert; `TenantPersistenceTests` (5 Tests) |

**Neue Pakete:** `Npgsql.EntityFrameworkCore.PostgreSQL`, `EFCore.NamingConventions`, `Microsoft.EntityFrameworkCore.Design` (Build-only, `PrivateAssets=all`) in Infrastructure; `Testcontainers.PostgreSql` im Testprojekt. dotnet-ef als **lokales** Tool-Manifest (kein Global-Install).

---

## 4. Architekturentscheidungen

| # | Entscheidung | Begründung / Vorgabe |
|--|--|--|
| P1 | Nur `Tenant` als Entität, keine User/Document/Akte-Vorab-Modelle | Auftrag: „noch KEINE vollständige Fachdomäne" (root #46, YAGNI); Tenant ist der fachliche Anker für künftige Tenant-FKs; das Muster (Domain-Entität + `IEntityTypeConfiguration` + Test) ist damit etabliert |
| P2 | `xmin` als Shadow-Property-Concurrency-Token statt eigenem `RowVersion`-Feld | PostgreSQL-Bordmittel: jede UPDATE inkrementiert `xmin` automatisch; Domain bleibt frei von Persistenz-Artefakten (backend #61); EF wirft `DbUpdateConcurrencyException` bei stalem Snapshot |
| P3 | `Guid.CreateVersion7()` für IDs (client-seitig) | V7-UUIDs sind zeitgeordnet → bessere B-Tree-Lokalität als Random-UUIDs, ohne DB-Sequenz-Roundtrip |
| P4 | Migration beim App-Start hinter `MigrateOnStartup`-Flag | Idempotent und offline-sicher (backend #63); SaaS kann das Flag per Env ausschalten und Deployment-seitig migrieren; API-Integrationstests (ohne DB) bleiben grün |
| P5 | Kein generisches Repository, kein eigenes UoW-Interface | DbContext **ist** die Unit of Work / der Persistence-Zugriff; Abstraktion erst wenn ein Use Case sie braucht (backend #77) |
| P6 | Keine Query-Filter-/RLS-Machinerie für Tenant-Isolation noch | Es gibt noch keine tenant-scoped Entität; sobald `User`/`Document` landen: `TenantId`-Konvention + zentrale Filter in einer Basis-Konfiguration (backend #8, RLS als Defense-in-Depth bleibt offen, #164) |
| P7 | Integrationstests gegen echtes PostgreSQL via Testcontainers | backend #95 verlangt reales PostgreSQL „where practical"; Docker ist im Dev-Setup vorhanden; kein InMemory-Provider (verfälscht Constraints/Concurrency-Verhalten) |
| P8 | Kein Datenbank-Health-Check an `/health` | Würde die bestehenden API-Tests ohne DB brechen; folgt, sobald der Health-Endpoint nach readiness/liveness differenziert wird (backend #89) |
| P9 | snake_case via Konvention-Paket statt manueller Spaltennamen überall | PostgreSQL-Idiom; ein Paket, ein zentraler Aufruf — kein Boilerplate pro Property |
| P10 | Transaktionen ohne eigene Infrastruktur | `IDbContextTransaction` aus EF Core genügt; Transaktionsgrenzen bleiben beim späteren Application-Service (backend #60, #118); belegt durch Rollback-Test |

---

## 5. Verifikation (Phase-3-Abschlusskriterien)

- `dotnet build`: **0 Fehler**
- `dotnet test`: **26/26 grün** — 7 Architektur-Tests + 14 API-Integrationstests (unverändert grün, Testing-Env migriert nicht) + 5 neue Persistenz-Tests gegen echtes PostgreSQL
- Dependency Direction: Architektur-Tests bestätigen — Api referenziert weiterhin weder EF Core noch Npgsql (`MigrateAkashaDatabaseAsync` kapselt EF-Typen in Infrastructure); Domain/Application unverändert herstellerneutral; kein SQLite/MySQL/SqlServer-Paket referenziert

Persistenz-Testabdeckung (`TenantPersistenceTests`):

| Anforderung | Test |
|--|--|
| CRUD / Mapping | `Tenant_roundtrips` |
| Unique Constraint | `Duplicate_slug_is_rejected_by_unique_constraint` → `DbUpdateException` |
| DB-seitige Constraints | `Name_length_is_rejected_by_column_constraint` (201 Zeichen via Raw SQL scheitern) |
| Concurrency #28 | `Stale_update_is_rejected_by_xmin_concurrency_token` → `DbUpdateConcurrencyException` bei zwei parallelen Snapshots |
| Transaktionsgrenzen #60 | `Rolled_back_transaction_persists_nothing` |
| Migrationen #63 | implizit: Fixture wendet alle Migrationen beim Container-Start an |

Regel-Konformanz: PostgreSQL einzige Datenbank; keine JSON-Ablage für Geschäftsobjekte; FK/Unique/Index/NotNull vorhanden; keine Secrets im Repository (Dev-Connection passwortlos, Test-Verbindung aus Container); Domain ohne EF-Abhängigkeit; Migrationen versioniert und offline anwendbar.

---

## 6. Tech-Debt-Register / zurückgestellt

Bewusst nicht in Phase 3 umgesetzt:

### Nächste Backend-Phasen (Fachdomäne)
- Entitäten `User`, `Group`, `Role`, `Permission`, `Document`, `Akte`, `StorageConnector`, `AuditEntry` — jeweils nach dem etablierten Muster, mit `TenantId`-FK + zusammengesetzten Unique Constraints (z. B. `UNIQUE(tenant_id, connector_id, external_id)`, backend #166)
- Tenant-Isolation-Mechanismus (zentrale Query-Filter-Konvention, ggf. PostgreSQL RLS als Defense-in-Depth, backend #8, #164) — sobald die erste tenant-scoped Entität existiert
- Cascade-Regeln und Audit-Retention bei der jeweiligen Beziehung festlegen (backend #168, #169), nicht global raten
- DB-Health-Check, sobald der Health-Endpoint differenziert wird (backend #89)

### Später
- Retry-on-Transient (Npgsql `EnableRetryOnFailure`) gemeinsam mit dem ersten Hintergrund-Job synchronisieren — Entscheidung gehört zur Job-Ausführung, nicht zur Options-Registrierung (backend #35)
- Connection-Pooling-Tuning / Minimal-Pool-Größen erst mit Lastzahlen (backend #91)

### Offene Punkte
- Keine kritischen offenen Punkte. Der `MigrateOnStartup`-Pfad ist in Development konfiguriert, aber noch nicht gegen einen echten lokalen PostgreSQL gestartet — erste Verifikation beim Dev-Setup/Compose.

---

Phase 3 gilt mit diesem Stand als abgeschlossen. Änderungen sind nicht committet (kein Commit ohne ausdrückliche Anweisung).
