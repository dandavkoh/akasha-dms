# Phase 7 - Storage Connector Architecture

**Datum:** 2026-09-19
**Scope:** `Akasha.Domain/Storage`, `Akasha.Application/Storage`, `Akasha.Application.Tests` (neu)
**Referenzen:** `/AGENTS.md` #3, #6-#9, #11, `/backend/AGENTS.md` #11-#31, #59, #101

---

## 1. Ziel

Stabile, provider-agnostische Storage-Abstraktion. Core/Application kennt nur `IStorageConnector` + Capabilities, niemals Provider-Namen oder Provider-SDKs. Kein echter Cloud-Connector implementiert (Auftrag).

## 2. Umgesetzte Komponenten

| Komponente | Layer | Zweck |
|--|--|--|
| `IStorageConnector` | Application | Alle 15 Operationen (Get/List/Create/Update/Delete/Move/Copy/Rename/Download/GetVersions/GetMetadata/UpdateMetadata/GetPermissions/GetChanges), streamingbasiert, `expectedETag` für optimistische Concurrency |
| `StorageCapability` (Flags) | Application | 14 Capabilities; Core fragt `Supports(Move)` statt `provider == SharePoint`; `Require()` wirft bei fehlender Capability |
| `StorageConnectorBase` | Application | Alle Operationen default `UnsupportedOperation` -> Connector override't nur, was er deklariert |
| `StorageItem / StorageVersion / StoragePermission` | Application | Normalisierte, vendor-neutrale Modelle |
| `StorageChange / StorageChangeType / StorageChangePage` | Application | Normalisierte externe Änderungen (Created/Modified/Renamed/Moved/Deleted/Restored) + opaker Cursor (Delta-Token) + `RequiresFullRescan` |
| `StorageException / StorageErrorKind` | Application | Normalisierte Fehlerkategorien (NotFound, AccessDenied, ConcurrencyConflict, Unavailable, AuthenticationRequired, UnsupportedOperation, Validation, Conflict) + `IsTransient` für Retry-Entscheidungen |
| `StorageReference` | Domain | Physische Identität eines Dokuments: `ConnectorId`, `ExternalId`, `Path`, `Name`, `Version`, `ETag`, `LastKnownModifiedAtUtc`, `SyncState`. `RecordRelocation` (Rename/Move) ändert niemals Id/ConnectorId/ExternalId |
| `StorageSyncState` | Domain | Pending, Available, Missing, Unavailable, SyncError, AuthenticationRequired |

## 3. Tests (10, neu: `Akasha.Application.Tests`)

- Base-Klasse wirft `UnsupportedOperation` für nicht deklarierte Capabilities
- `Supports`/`Require` spiegeln deklarierte Capabilities
- Fake-Connector (In-Memory): Create/Download/Rename-Roundtrip, ExternalId stabil über Rename
- Stale-ETag -> `ConcurrencyConflict` (kein silent overwrite, /AGENTS.md #9)
- Inkrementelles `GetChanges` mit Cursor
- `StorageReference`: Rename/Move behält Identität; externe Deletion -> Missing mit Last-Known-State; Restore -> Available

## 4. Architekturentscheidungen / YAGNI

| # | Entscheidung | Begründung |
|--|--|--|
| P1 | Upload = `Create`/`Update`, kein separates `UploadDocument` | Doppelt gemoppelt; Download ist wegen Streaming der einzige eigene Read-Pfad |
| P2 | Keine EF-Persistenz/Migration für `StorageReference` in dieser Phase | Entity ist bereit (`Id`, private setter), wird mit dem Documents-Modul/einem Document-Aggregat persistiert |
| P3 | `WatchChanges`/`Locking`/`NativeSearch`: Capability vorhanden, keine Interface-Methoden | Wird ergänzt, sobald ein Connector es braucht (Webhook-Subscription, Lock/Unlock, Provider-Search) |
| P4 | Kein Connector-Registry/Factory | Kommt mit der Connector-Verwaltung (Konfiguration, Zustände, #70) |
| P5 | Kein LocalFileSystem-Connector | Nicht angefordert; Abstraktion ist durch Fake-Connector bewiesen |

## 5. Verifikation

- `dotnet build`: 0 Fehler
- `dotnet test` (Solution): 74/74 grün, inkl. ArchitectureTests (Domain/Application ohne Infra-/Provider-Referenzen)
- Dependency Direction: Storage-Abstraktion in Application, `StorageReference` in Domain, beide nur BCL
