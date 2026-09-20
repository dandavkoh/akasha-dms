# Phase 9 - Document Domain

**Datum:** 2026-09-20
**Scope:** `Akasha.Domain/Documents` + `StorageReference` (Erweiterung), `Akasha.Application/Documents` + `Common`, `Akasha.Infrastructure/Documents` + `Persistence`, `Akasha.Api/DocumentsController` + `ProblemExceptionHandler`, Migration `DocumentDomain`, Tests in allen drei Testprojekten.
**Referenzen:** `/backend/AGENTS.md` #10-#13, #22-#28, #31, #62, #83, #109-#110; baut auf Phase 3-6 (Persistence/Tenant/Identity/Authorization) auf.

---

## 1. Ziel

Zentrale logische Dokumententität. `DocumentId` ist server-generiert (Guid v7) und niemals
von Pfad, Dateiname oder Provider-ID abgeleitet. Externe Rename/Move/Delete/Modify mutieren
nur die `StorageReference`, nie die logische Identität.

## 2. Umgesetzte Komponenten

| Komponente | Zweck |
|--|--|
| `Domain/Documents/Document` | Logische Entität: `Id` (DocumentId), `TenantId`, `Name`, `Storage` (1:1), `State` => `Storage.SyncState`, `Processing` (separate Dimension, #110), Timestamps. Methoden: `Rename`, `RecordExternalRelocation`, `RecordExternalVersion` (Stale-Guard #102), `MarkMissing/Restored/Unavailable/SyncError/ConnectorUnavailable/AuthenticationRequired`, `BeginProcessing/MarkProcessed/MarkProcessingFailed` |
| `Domain/Documents/ProcessingState` | None/Pending/Processing/Processed/Failed - getrennt vom Sync-State (#110) |
| `Domain/Storage/StorageSyncState` | + `ConnectorUnavailable` (#105/#70) |
| `Domain/Storage/StorageReference` | jetzt `ITenantScoped` (TenantId im Ctor) für Unique-Constraint und Query-Filter |
| `Application/Documents/IDocumentService` | List (paged), Get, Create (Registrierung), Rename (mit RowVersion), Delete (nur logisch) |
| `Application/Common/Errors` | `NotFoundException`, `ConflictException`, `ValidationException` |
| `Infrastructure/Documents/DocumentService` | AuthZ vor Datenzugriff (#10), xmin-Optimistic-Concurrency (#28), unique (TenantId,ConnectorId,ExternalId) -> Conflict, Audit `document.created/renamed/deleted` |
| `Persistence` | `documents` + `storage_references` Tabellen, FKs (Tenant Restrict, Document Cascade), xmin RowVersion |
| `Api` | `DocumentsController` (`/api/v1/documents`), `ProblemExceptionHandler` (IExceptionHandler): AccessDenied->403, NotFound->404, Conflict->409, Validation->400, TenantResolution->401/403. JSON: Enum-Strings |

**Authorization-Mapping:** Create -> `Upload` (type-level), Read -> `Read` (resource; List: type-level),
Rename -> `Rename` + RowVersion, Delete -> `Delete`. Alles über `IAuthorizationService` (Phase 6).

**Concurrency:** PostgreSQL `xmin` System-Spalte als Concurrency-Token (kein eigener Column -
Npgsql überspringt ihn beim CREATE TABLE). Rename verlangt `RowVersion`; stale -> 409,
kein silent overwrite (#28). Getestet inkl. Verifikation dass die DB unverändert bleibt.

**Wichtig (Lesson):** Nach `DbUpdateConcurrencyException` ist die getrackte Entity im selben
DbContext stale. Aufrufer (Request-Scope) verwirft den Context; Tests müssen frische
Contexte verwenden. In der Produktion (scoped DbContext pro Request) kein Problem.

## 3. Tests (31 neu, davon 15 API/Infra mit echtem PostgreSQL)

- Domain: Identität stabil über Rename/Move; Stale-/Duplicate-Change ignoriert; Missing->Restored;
  ConnectorUnavailable bewahrt letzten Zustand; Processing-Fehler != Availability; Validierung
- Service: CRUD, AuthZ deny ohne Grant, AuthZ vor Datenzugriff, Unique-Conflict ExternalId,
  gleiche ExternalId in anderem Tenant erlaubt, Cross-Tenant -> NotFound (auch mit fremdem
  Resource-Grant), Rename + xmin-Konflikt, Delete hält Audit, Pagination + Boundaries
- API: 401 unauthenticated, 403 ohne Grant, voller CRUD-Roundtrip, Cross-Tenant 404, 400 Validation,
  409 stale RowVersion

## 4. Architekturentscheidungen / YAGNI (ponytail-markiert im Code)

| # | Entscheidung | Grenze / Upgrade-Pfad |
|--|--|--|
| P1 | `ListAsync` verlangt type-level READ (Resource-Level-Grants filtern nicht pro Zeile) | Per-Row-Grant-Join, sobald dokumentbezogene Grants tatsächlich vergeben werden |
| P2 | Create = Registrierung bestehender Storage-Objekte; kein Upload/Download/Physical-Rename | Content-Ops kommen mit Connector-Registry (Phase 7, P4/P5) + Connector-Aufruf vor DB-Commit (#29) |
| P3 | Delete ist reine logische Löschung (harte DB-Löschung, Audit + physische Datei bleiben) | Soft-Delete/Retention-Policy erst bei konkretem Requirement (#83) |
| P4 | Kein Sync-Service/External-Change-Endpoint | Domain-Methoden dafür sind fertig; Orchestrierung kommt mit Change-Detection-Phase |

## 5. Verifikation

- `dotnet build` Solution: 0 Fehler, 0 Warnungen
- `dotnet test` Solution: 136/136 grün (31 neu)
- Migration `20260920114331_DocumentDomain` erzeugt + gegen PostgreSQL 17 verifiziert
- ArchitectureTests (Dependency Direction) unverändert grün: Domain ohne EF/ASP.NET-Vendor-Abhängigkeiten
- Neue NuGet-Abhängigkeiten: keine
