# Phase 12 - Upload / Download / File Operations

**Datum:** 2026-09-23
**Scope:** `Akasha.Application/Storage` (`IConnectorResolver`), `Akasha.Application/Documents` (Content-DTOs), `Akasha.Infrastructure/Storage` (`ConfiguredConnectorResolver`), `Akasha.Infrastructure/Documents` (physische Operationen), `Akasha.Api` (Content-Endpunkte, `StorageException`→HTTP), Tests in `Akasha.Infrastructure.Tests` (neu: `Fakes`, `DocumentFileOperationTests`) + `Akasha.Api.IntegrationTests` (neu: `DocumentFileApiTests`).
**Referenzen:** Root-`AGENTS.md` #6-#9, #47; Backend-`AGENTS.md` #9, #10, #29, #30, #59, #65-#67, #83, #105.

---

## 1. Ziel

Physische Dateioperationen durch den Storage-Connector, mit kompletter Pipeline pro Operation:

```text
Tenant ✓ → Authorization ✓ → Document ✓ → Connector ✓ → Capability ✓
→ Storage-Operation → ERST DANN DB-Zustand (rule #29) → Audit → Fehler sauber
```

Ein fehlgeschlagener Storage-Vorgang meldet niemals Erfolg; implizierte
Zustandskorrekturen (NotFound → `Missing`, Unavailable → `Unavailable`,
AuthenticationRequired → `AuthenticationRequired`) werden explizit und mit Audit
vollzogen (#30, #31, #105, #138).

## 2. Umgesetzte Komponenten

| Komponente | Zweck |
|--|--|
| `Application/Storage/IConnectorResolver` | Tenant-sichere Connector-Auflösung; fremde Connector-Ids → `NotFound` |
| `Infrastructure/Storage/ConfiguredConnectorResolver` | Connectors aus `Storage:Connectors` (Konfiguration). `TenantId = empty` = bewusst tenant-übergreifend geteilt (Self-Hosted), sonst strikt tenant-gebunden. Instanzen werden pro Connector-Id gehalten (Change-Tracking-Snapshot) |
| `DocumentService`: `UploadContentAsync`, `DownloadContentAsync`, `UpdateContentAsync`, `DeleteContentAsync`, `MoveFileAsync`, `CopyFileAsync`, `RenameFileAsync` | 7 physische Operationen, je mit AuthZ (Upload/Download/Write/Delete/Move/Copy+Upload/Rename), Capability-Check, ETag-Concurrency, Audit |
| `GuardedAsync` (DocumentService) | Einheitliche Fehlerbehandlung aller Connector-Calls: rethrow + ehrliche Zustandskorrektur + Audit-Event (`document.storage.state.*`) |
| `MarkRegisteredAsync` + Orphan-Cleanup | Upload registriert erst nach erfolgreichem Storage-Write; Unique-Conflict räumt die physische Datei wieder weg (keine Teil-Erfolge) |
| `ProblemExceptionHandler` | `StorageException` → 400 Validation/UnsupportedOperation, 403 AccessDenied, 404 NotFound, 409 Concurrency/Conflict, 503 Unavailable/AuthenticationRequired. Unknown → 500 |
| Controller-Endpunkte | `POST /upload?connectorId&folder&name` (raw body, 512 MiB Limit), `GET/PUT/DELETE /{id}/content`, `POST /{id}/move`, `POST /{id}/copy`, `POST /{id}/rename-file` |

## 3. Konkurrenz-Semantik

- Schreibende Operationen nutzen den Client-`If-Match`-ETag; fällt der fehlt,
  gewinnt der **zuletzt bekannte** ETag: externe Änderungen erzeugen
  `ConcurrencyConflict` statt still überschrieben zu werden (#9, #28).
- Move/Rename übergeben ebenfalls den bekannten ETag an den Connector.
- DB-seitig weiterhin PostgreSQL-`xmin` für logische Updates (Phase 9).

## 4. Tests (24 neu: 19 Service + 5 API)

Negativ-Szenarien, u. a.:

- Traversal-Upload (`../evil.txt`): 400, **nichts** wird geschrieben, kein Audit-Müll
- Unbekannter Connector: 404; fehlende Grants: 403/AccessDenied **vor** Storage-Zugriff
- Duplikat-Upload: Connector-Conflict, kein Overwrite, kein zweites Dokument
- Externe Modifikation → Update ohne passenden ETag: `ConcurrencyConflict`, externer
  Inhalt unverändert (kein Silent-Overwrite)
- Extern gelöschtes File → Download 404 und Dokument wechselt auditierbar nach `Missing`
- Fehlende Capability (Move ohne `StorageCapability.Move`) → `UnsupportedOperation`
  vor jedem Storage-Call
- Abgelaufene Connector-Credentials → Fehler propagiert, State `AuthenticationRequired`
- Rename/Move: gleiche `DocumentId`, Akte-Beziehungen unberührt (sie referenzieren nur Ids)
- Copy: neues logisches Dokument, erfordert Copy **und** Upload-Grant
- Cross-Tenant: alle Operationen → NotFound, trotz Vollberechtigung im eigenen Tenant
- Physisches Löschen: logisches Dokument bleibt als `Missing` (#83)

## 5. Architekturentscheidungen / YAGNI

| # | Entscheidung | Grenze / Upgrade-Pfad |
|--|--|--|
| P1 | Connector-Registry ist konfigurationsbasiert (`Storage:Connectors`), keine DB-Tabelle | Connector-Verwaltung (#70) führt die Tabelle ein; `IConnectorResolver`-Vertrag bleibt |
| P2 | Geteilte Connectors (TenantId leer) möglich | Explizite Operator-Entscheidung; Standard bleibt tenantgebunden |
| P3 | Download wird auditiert (`document.downloaded`), kein Pre-Sign-/URL-Modell | Temp-URLs nur, wenn ein Security-Modell dafür existiert (#65) |
| P4 | Physisches Löschen = logisches `Missing`, kein Hard-Delete des DMS-Datensatzes | Produktentscheidung für "Papierkorb" separat (#83) |
| P5 | Timeouts: DB-Command-Timeout + Request-Abbruch; kein eigenes Polly/Retry-Layer im DocumentService | Retry-Policy pro Connector, sobald Provider es verlangen (#137) |

## 6. Verifikation

- `dotnet build`: 0 Fehler (nur bereits vorhandene Warnungen)
- `dotnet test` (Solution): 187/187 grün — davon 116× Infrastructure (echtes PostgreSQL),
  38× API (Testcontainers + echter Dateisystem-Connector), 7× ArchitectureTests
- Dependency Direction: unverändert sauber (Domain ohne Infra-/Provider-Referenzen)
- Neue NuGet-Abhängigkeiten: keine
- Migration: keine nötig (Schema unverändert)
