# Phase 11 - Document ↔ Akte

**Datum:** 2026-09-22
**Scope:** `Akasha.Application/Akten` (`AkteDocumentView`, `ListDocumentsAsync`), `Akasha.Infrastructure/Akten` (Implementierung), `Akasha.Api/AktenController` (`GET /api/v1/akten/{id}/documents`), Tests in `Akasha.Infrastructure.Tests/Akten` + `Akasha.Api.IntegrationTests`.
**Referenzen:** Root `AGENTS.md` #10/#47.2 (Akten logisch, nie Storage-Path), Backend-`AGENTS.md` #8-#11, #42-#43, #25, #105, #167. Baut direkt auf Phase 10 (Akten) und Phase 9 (Document) auf.

---

## 1. Ziel

Beziehung Akte ↔ Dokument: **Akte referenziert ausschließlich die stabile `DocumentId`**
- niemals Storage-Pfad, Dateiname oder Provider-URL. Phase 10 lieferte Attach/Detach,
Member-Ids und Persistence; Phase 11 ergänzt das fachliche Kernstück: **Dokumente einer
Akte listen** (paginiert, mit Availability-State) und zementiert die Kardinalität.

## 2. Fachliche Entscheidung: Kardinalität

**Document ↔ Akte ist many-to-many:**

* eine Akte enthält beliebig viele Dokumente
* ein Dokument darf mehreren Akten zugeordnet sein

Umgesetzt durch die Join-Entität `AkteDocument` mit Unique-Constraint
`(TenantId, AkteId, DocumentId)` (#166) - ein Link pro Paar, Duplikate → 409.
FKs: Akte/Document Cascade (logisches Dokument-Löschen entfernt nur die Membership,
die Akte bleibt), Tenant Restrict. Externe Rename/Move berühren die Beziehung nicht,
weil nur die `DocumentId` referenziert wird (#42).

**Physische Verfügbarkeit ist orthogonal zur Membership** (#25/#105): ein Dokument im
Zustand `Missing` oder `ConnectorUnavailable` bleibt Bestandteil der Akte und wird im
Listing mit seinem State geführt. Auch das *Attachen* eines Missing-Dokuments ist
erlaubt - maßgeblich ist die logische Existenz, nicht die physische Datei.

## 3. Umgesetzte Komponenten (Delta zu Phase 10)

| Komponente | Zweck |
|--|--|
| `Application/Akten/AkteDocumentView` | `DocumentId`, `Name`, `State` (`StorageSyncState`), `LinkedAtUtc` |
| `IAkteService.ListDocumentsAsync(akteId, skip, take)` | Paginierte Dokumentliste der Akte |
| `Infrastructure/Akten/AkteService` | AuthZ `Read` auf die Akte **vor** Datenzugriff (#10), Existenzprüfung über Tenant-Filter (fremde Akte → NotFound, kein Existenz-Leak), Join Membership → Document, Pagination (1..100) |
| `AktenController` | `GET /api/v1/akten/{id}/documents?skip=&take=` (200/404) |

Add/Remove/AuthZ/Tenant-Isolation waren aus Phase 10 bereits vorhanden und bleiben
unverändert (`PUT|DELETE /{id}/documents/{documentId}`, `Write` auf Akte + `Read` auf
Dokument vor Datenzugriff).

## 4. Tests (6 neu)

- Infra (4, echtes PostgreSQL): m:n-Beziehung dasselbe Dokument in zwei Akten;
  ListDocuments Pagination/Bounds/NotFound/Denied; Missing + ConnectorUnavailable
  bleiben mit State sichtbar; Attach erlaubt Missing-Dokument
- API (1 plus Assertions): ListDocuments-Roundtrip mit State-Mapping
  (`Pending`/`Missing` im JSON), 404 für unbekannte Akte

Bestehende Abdeckung aus Phase 10 bleibt: Duplicate-Attach 409, Detach 404,
Cross-Tenant Document → 404, Cross-Tenant Akte → 404, Dokument-Deletion → nur
Membership weg, Audit `akte.document.attached/detached`.

## 5. Architekturentscheidungen / YAGNI

| # | Entscheidung | Grenze / Upgrade-Pfad |
|--|--|--|
| P1 | `AkteDocumentView` trägt nur Id/Name/State/LinkedAtUtc, kein voller StorageReference | Storage-Detail im Listing erst bei konkretem UI-/API-Bedarf |
| P2 | ListDocuments ohne zusätzliche per-Document-Grant-Filterung: READ auf die Akte genügt (gleiche Trade-off-Logik wie List bei Dokumenten, Phase 9 P1) | Per-Dokument-Grant-Join, sobald dokumentbezogene Grants granular genutzt werden |

## 6. Verifikation

- `dotnet build` Solution: 0 Fehler (nur vorher existierende Warnungen)
- `dotnet test` Solution: 162/162 grün (6 neu), davon Infra/API gegen echtes PostgreSQL
- ArchitectureTests (Dependency Direction) grün: Domain weiterhin frei von
  EF/ASP.NET/Provider-Abhängigkeiten; Neuerung liegt in Application/Infrastructure
- Migration: keine nötig (`akte_documents` existiert seit Phase 10)
- Neue NuGet-Abhängigkeiten: keine
