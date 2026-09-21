# Phase 10 - Akten

**Datum:** 2026-09-21
**Scope:** `Akasha.Domain/Akten`, `Akasha.Application/Akten`, `Akasha.Infrastructure/Akten` + `Persistence`, `Akasha.Api/AktenController`, Migration `Akten`, Tests in allen drei Testprojekten.
**Referenzen:** `/backend/AGENTS.md` #8-#11, #42, #43, #166-#167; Root `AGENTS.md` #10 (Akten sind logisch, kein Storage-Folder). Baut auf Phase 3-6 (Persistence/Tenant/Identity/Authorization) und Phase 9 (Document-Pattern) auf.

---

## 1. Ziel

Akte als first-class logisches Domain-Konzept. Eine Akte ist **kein** physischer
Storage-Folder, sondern ein logischer Container, der Dokumente ausschließlich über ihre
stabile `DocumentId` referenziert - unabhängig von Storage-System, Pfad oder Dateiname.
Externe Rename/Move-Operationen auf Dokumente lassen die Akte-Beziehung unberührt (#42).

## 2. Umgesetzte Komponenten

| Komponente | Zweck |
|--|--|
| `Domain/Akten/Akte` | Logische Entität: `Id` (AkteId, Guid v7), `TenantId`, `Name`, `Description`, `Status`, Timestamps. Methoden: `Update` (abgelehnt wenn archiviert), `Archive`/`Reactivate` (idempotent), `EnsureActive` |
| `Domain/Akten/AkteStatus` | `Active` / `Archived` – minimales Lifecycle-Modell, keine Workflows |
| `Domain/Akten/AkteDocument` | Membership Akte ↔ Dokument ausschließlich per logischer `DocumentId` (#42), `ITenantScoped`, `LinkedAtUtc` |
| `Application/Akten/IAkteService` | List (paged), Get (inkl. DocumentIds), Create, Update (xmin), Archive/Reactivate (xmin), Attach/Detach |
| `Infrastructure/Akten/AkteService` | AuthZ vor Datenzugriff (#10), `ResourceType.Akte`, xmin-Optimistic-Concurrency (#28), Audit `akte.created/updated/archived/reactivated` + `akte.document.attached/detached` |
| `Persistence` | Tabellen `akten` + `akte_documents`; FKs: Tenant Restrict, Akte/Document Cascade; Unique `(TenantId, AkteId, DocumentId)` (#166); xmin RowVersion auf `akten` |
| `Api` | `AktenController` (`/api/v1/akten`): GET/POST/PATCH, `/{id}/archive|reactivate`, PUT/DELETE `/{id}/documents/{documentId}` |

**Authorization-Mapping:** List → type-level `Read`; Get → resource `Read`; Create → type-level
`Write`; Update/Archive/Reactivate/Attach/Detach → resource `Write`; Attach zusätzlich
resource `Read` auf das Dokument (beides vor jedem Datenzugriff).

**Exception-Mapping (Review-Frage):** Domain-Invarianten werfen weiterhin
`InvalidOperationException`; der Service prüft `IsArchived` vorab und normalisiert auf
`ConflictException` (409) - der `ProblemExceptionHandler` mappt nur Application-Exceptions;
Domain-Exceptions bleiben Sicherheitsnetz, kein API-Vertrag.

**Concurrency:** `xmin` wie bei Dokumenten; Update/Archive/Reactivate verlangen
`RowVersion`, stale → 409 ohne Überschreiben; DB bleibt verifiziert unverändert.

**Bug während der Entwicklung (Lesson):** Ein erwarteter Unique-Conflict bei doppeltem
Attach ließ die fehlgeschlagene Entity im ChangeTracker als `Added` getrackt - das
nächste `SaveChanges` hätte sie erneut eingefügt. Fix: im Conflict-Pfad wird die Entity
detached (gleiches latentes Muster wie Dokumente, dort dokumentiert).

## 3. Tests (21 neu, davon 15 mit echtem PostgreSQL)

- Domain (6): Identität server-generiert, Validierung, Update, Archive/Reactivate
  idempotent, archiviert ⇒ Update/Membership-Invariante, `AkteDocument`-Validierung
- Service (9): Create + Audit, Create ohne Grant denied, Cross-Tenant NotFound (auch mit
  fremdem Resource-Grant), List + Pagination + Bounds + type-level Read, stale
  RowVersion → Conflict ohne Überschreiben (DB verifiziert unverändert), Lifecycle
  Archive → Update 409 → Reactivate, Attach/Detach inkl. Duplicate-Conflict und
  Detach-NotFound, Cross-Tenant-Document → NotFound, Dokument-Löschung cascadiert nur
  die Membership (Akte bleibt)
- API (6): 401 unauthenticated, 403 ohne Grant, CRUD/Lifecycle-Roundtrip inkl. 409 stale
  RowVersion und 409 bei Update auf archivierter Akte, Attach/Detach/Duplicate,
  Cross-Tenant 404, 400 Validation

## 4. Architekturentscheidungen / YAGNI (ponytail-markiert im Code)

| # | Entscheidung | Grenze / Upgrade-Pfad |
|--|--|--|
| P1 | List lädt keine DocumentIds (nur Get) | Join/Aggregation sobald die Listenansicht Mitgliedschaften anzeigen soll |
| P2 | Status minimal `Active`/`Archived`; kein Delete, kein Workflow-Hook | Erweiterter Lifecycle erst mit konkretem Bedarf |
| P3 | Attach verlangt resource `Read` auf das Dokument, nicht Upload/Write | Verschärfen, sobald fachliche Attach-Semantik definiert ist |

## 5. Verifikation

- `dotnet build` Solution: 0 Fehler (nur bekannte, vorher existierende Warnungen)
- `dotnet test` Solution: 157/157 grün (21 neu)
- Migration `20260921102317_Akten` erzeugt, Tabellen gegen Testcontainers PostgreSQL 17
  angelegt und in den Service-/API-Tests durch Migration (`MigrateAsync`) verifiziert
- ArchitectureTests (Dependency Direction) unverändert grün: Domain ohne
  EF/ASP.NET/Provider-Abhängigkeiten
- Neue NuGet-Abhängigkeiten: keine
