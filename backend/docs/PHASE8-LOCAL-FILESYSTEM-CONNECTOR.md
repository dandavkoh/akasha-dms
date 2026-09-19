# Phase 8 - Local Filesystem Connector

**Datum:** 2026-09-19
**Scope:** `Akasha.Infrastructure/Storage` (neu), `Akasha.Infrastructure.Tests/Storage` (neu)
**Referenzen:** `/backend/AGENTS.md` #16, #67, #113, #139-#141; baut auf Phase 7 (Storage-Abstraktion) auf.

---

## 1. Ziel

Erster echter Connector: `LocalFileSystemConnector : IStorageConnector` für
lokale/customer-controlled Files. Kein Code außerhalb des Connectors kennt
Filesystem-Details (#113).

## 2. Umgesetzte Komponenten

| Komponente | Zweck |
|--|--|
| `LocalFileSystemConnector` | Alle 15 Interface-Operationen; erreichbare: Get/List/Create/Update/Delete/Move/Copy/Rename/Download (Stream)/GetMetadata + `GetChanges` (Basis-Scope "basic change detection"); GetVersions/UpdateMetadata/GetPermissions bleiben `UnsupportedOperation` laut Base-Klasse |
| `LocalFileSystemConnectorOptions` | `RootPath` (Sicherheitsgrenze, muss existieren, kein Reparse-Point), `MaxFileSizeBytes` (Default 512 MiB) |

**Capabilities:** `Read | Write | Delete | Move | Copy | Rename | Metadata | ChangeTracking`.
Nicht deklariert: Versioning, Permissions, RecycleBin, WatchChanges, Locking, NativeSearch.

**Identität / Concurrency:**

- `ExternalId`: NTFS-VolumeSerial + FileId (`GetFileInformationByHandleEx(FileIdInfo)`) - stabil über Rename/Move im Volume (#101). Fallback außerhalb Windows: relativer Pfad (s. bekannte Grenze unten).
- `ETag`: `lastWriteTicks-size`; alle mutierenden Operationen prüfen `expectedETag` -> `ConcurrencyConflict`, kein silent overwrite (#28).
- Update schreibt **in-place** (Truncate), damit die FileId erhalten bleibt; Create schreibt atomar über `.akasha-part`-Tempfile im Zielordner (Cleanup bei Fehler, #68).

**Sicherheit (#67, #141):**

- Segment-Validierung vor jeder I/O: `..`, `.`, absolute Pfade, ungültige Zeichen, reservierte Namen (CON, NUL, LPT1...), trailing Punkt/Leerzeichen.
- Fully-qualified Auflösung + StartsWith-Check -> alles außerhalb der Root wird abgelehnt (Traversal, Root-Escape).
- Reparse-Points/Junctions/Symlinks werden auf jeder Pfad-Ebene abgelehnt und in Scans übersprungen.
- Oversized Uploads werden während des Streamings abgelehnt (Limit enforced).
- IO-Exceptions werden auf `StorageErrorKind` normalisiert (NotFound/AccessDenied/Conflict/Unavailable(transient)/Validation), keine rohen Exceptions nach außen (#59).

**Change Detection (basic):** Full-Scan-Reconciliation pro Poll gegen In-Memory-Snapshot;
Cursor = fortlaufende Sequenz; unbekannter Cursor -> `RequiresFullRescan`.
Created/Modified/Deleted/Restored-fähig; Rename vs. Move via FileId unterschieden.

## 3. Tests (36, neu)

- Roundtrips: Create/Download, Get/List, Update (ETag match), Delete, Copy
- Identität: Rename/Move behalten ExternalId; Update behält ExternalId (In-Place)
- Concurrency: stale ETag -> Conflict, kein Überschreiben; Datei unverändert
- Sicherheit: `../`-Traversal, absolute Pfade, reservierte/ungültige Namen, Root-Escape via außenliegender Datei, Reparse-Point-Junction, Oversized-Upload inkl. Temp-Cleanup
- Parallel: 16 parallele Creates; 5 MiB Streaming-Roundtrip
- Missing: extern gelöschte Datei -> NotFound; Missing-Read
- Change Detection: Created/Modified/Deleted/Restored-Sequenz, Rename als `Renamed` (Windows), unbekannter Cursor -> `RequiresFullRescan`
- Capabilities: korrekt deklariert; nicht unterstützte Ops werfen `UnsupportedOperation`

## 4. Architekturentscheidungen / YAGNI (ponytail-markiert im Code)

| # | Entscheidung | Grenze / Upgrade-Pfad |
|--|--|--|
| P1 | Update in-place statt atomarem Replace | Nicht crash-atomar; ETag/Reconciliation erkennt Inkonsistenz. Atomic+identisch würde File.Replace + Re-ID brauchen |
| P2 | ExternalId = Pfad außerhalb Windows | Rename sieht aus wie Deleted+Created; Upgrade: inode/dev via stat, sobald Self-Hosted-Linux konkret |
| P3 | ExternalId-Auflösung per Scan O(tree) | Ok für lokale Volumes; interner Id->Pfad-Index (über GetChanges getrieben) wenn Scan messbar |
| P4 | Change-Snapshot in-memory | Neustart -> `RequiresFullRescan`, Caller macht Vollscan; Upgrade: persistenter Checkpoint (USN Journal) |
| P5 | Kein DI-Binding / kein Connector-Registry | Kommt mit Connector-Verwaltung (Phase-7-Entscheidung P4, #70) |
| P6 | Keine ACL-Permissions-Capability | YAGNI bis Products Storage-Permissions lesen will (#17) |

## 5. Verifikation

- `dotnet build` Solution: 0 Fehler
- `dotnet test` Solution: 110/110 grün (36 neu; ArchitectureTests inkl. Dependency Direction unverändert grün)
- Neue NuGet-Abhängigkeiten: keine (reine BCL)
