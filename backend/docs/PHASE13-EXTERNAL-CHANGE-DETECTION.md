# Phase 13 - External Change Detection & Synchronization

**Datum:** 2026-09-23
**Scope:** `Akasha.Domain/Storage` (`ConnectorSyncCheckpoint`), `Akasha.Application/Storage` (`IStorageSyncService`), `Akasha.Infrastructure/Storage` (`StorageSyncService`), `Akasha.Infrastructure/Persistence` (DbSet, Config, Migration `StorageSyncCheckpoint`), Tests in `Akasha.Infrastructure.Tests/Storage` (neu: `StorageSyncServiceTests`).
**Referenzen:** Root-`AGENTS.md` #4-#9, #31, #47; Backend-`AGENTS.md` #18-#26, #101-#105, #138, #172; baut auf Phase 7/8 (Connector, GetChanges) und Phase 9/12 (Document-Domain, File-Ops) auf.

---

## 1. Ziel

Externe Änderungen (Created/Modified/Renamed/Moved/Deleted/Restored) werden als erste Klasse behandelt:
Detect (Connector) → Normalize (`StorageChange`) → Reconcile (Domain-Transitionen) → Persist (eine Transaktion inkl. Checkpoint) → Process (Dokumente landen in `ProcessingState.Pending`).

Der Core kennt **nur** das normalisierte Change-Modell aus Phase 7; WIE SharePoint/Dropbox/SMB Änderungen erkennen, bleibt im Connector (Phase 8: Full-Scan-Reconciliation des Filesystem-Connectors).

## 2. Umgesetzte Komponenten

| Komponente | Layer | Zweck |
|--|--|--|
| `IStorageSyncService` + `StorageSyncResult` | Application | Ein Sync-Zyklus pro (Tenant, Connector); Ergebnis-Zähler rein informativ |
| `StorageSyncService` | Infrastructure | Orchestrierung: Capability-Check (`ChangeTracking`), Checkpoint laden, `GetChangesAsync`, inkrementelle Anwendung ODER Full-Scan-Reconciliation (erster Sync / `RequiresFullRescan`), atomares Commit inkl. Checkpoint, Audit als System (ActorSubject = null) |
| `ConnectorSyncCheckpoint` | Domain + EF | Opaquer Connector-Cursor pro (TenantId, ConnectorId), unique + tenant-gefiltert (#103) |

**Semantik pro Change-Type** (über `(ConnectorId, ExternalId)` aufgelöst, niemals Pfad/Name, #101):

- **Created / unbekannte ExternalId** → neues logisches Dokument (Available, Processing=Pending). Existiert bereits ein `Missing`-Dokument mit derselben stabilen ExternalId → **Restored** (gleiche DocumentId, #26).
- **Restored** → wie Created; bei bekanntem Dokument `MarkRestored`.
- **Renamed / Moved** → `RecordExternalRelocation`: DocumentId, Akte-Relationen (referenzieren nur DocumentId) und Metadata bleiben unberührt (#22/#23).
- **Modified** → `RecordExternalVersion`: neuer ETag/Version, Processing=Pending; veraltete/Duplikat-Änderungen werden von der Domain-Verworfen (Stale-Guard auf `LastKnownModifiedAtUtc`, #102).
- **Deleted** → `MarkMissing`: kein physischer/logischer Verlust von DocumentId, Akte-Relation, Metadaten, Audit, Last-Known-Path/Name/ETag (#25).

**Fehlersemantik:**
- Connector-Ausfall (Unavailable/AuthenticationRequired) wirft **vor** jeder Mutation → bekannter Zustand bleibt vollständig erhalten, kein Massen-`Missing` (#105).
- Fehlende `ChangeTracking`-Capability → `UnsupportedOperation` vor jedem Zugriff.
- Create-Race (paralleler Upload + Sync derselben ExternalId) → Unique-Key-Verletzung `(TenantId, ConnectorId, ExternalId)`; der Zyklus schlägt vollständig fehl und heilt beim nächsten Poll (idempotent durch Replay).

## 3. Tests (14, neu: Real-PostgreSQL + realer `LocalFileSystemConnector`)

Externe Änderungen werden direkt im Dateisystem simuliert (File.Move/WriteAllText/Delete):

- Discovery beim ersten Sync (inkl. Root-Level-Dateien, Pfad normalisiert zu "."), Checkpoint persistiert, zweiter Zyklus inkrementell ohne Änderungen
- Modified: ETag aktualisiert, DocumentId stabil, Processing=Pending, Audit
- Idempotenz: doppelt geliefertes Modified ändert den Zustand nicht
- Out-of-Order: älteres `ModifiedAtUtc` überschreibt neueren Stand **nicht** (#102/#28)
- Rename/Move: gleiche DocumentId, Akte-Relation erhalten, Audit
- Deleted → Missing, Last-Known-State + Akte + Audit erhalten
- Restore über stabile ExternalId (File heraus-/zurückbewegt): gleiche DocumentId, Available
- Restart (`RequiresFullRescan`): neue Connector-Instanz, Full-Scan reconciliert statt neu anzulegen; verschwundene Dateien → Missing
- Connector-Ausfall: Exception propagiert, kein Dokument verändert
- Ohne ChangeTracking-Capability: fast-fail
- Tenant-Isolation: zwei Tenants am selben physischen Root bekommen jeweils eigene Dokumente, keine Cross-Tenant-Sichtbarkeit

## 4. Architekturentscheidungen / YAGNI

| # | Entscheidung | Grenze / Upgrade-Pfad |
|--|--|--|
| P1 | Kein Background-Scheduler/Hosted Service in dieser Phase | Der Service ist aufrufbar und tenant-safe; Scheduling (Hangfire/Quartz/Interval) kommt mit dem Modul Hintergrundverarbeitung (#33) |
| P2 | Ein Zyklus = ein DbContext = eine Transaktion inkl. Checkpoint | Bei riesigen Change-Batches: Paging über Connector-Cursors (Connector-Seite) oder Batch-Commits |
| P3 | Erster Sync = Full-Scan (Discovery) statt "nur ab jetzt" | Gewollt: Sonst nie registrierte Altbestände würden nie auftauchen (#21 Discovery vs. Sync) |
| P4 | Kein separates "Processing-Enqueue" | `ProcessingState.Pending` ist das Signal; die Processing-Pipeline pollt/subscribt später (#24, #74) |
| P5 | Kein API-Endpunkt zum Triggern | YAGNI bis Connector-Verwaltung/Admin-UI (#70); Service ist DI-registriert |
| P6 | Kein Retry-Layer im Sync-Service | `IsTransient` aus `StorageException` reicht dem künftigen Scheduler (#35/#137) |

## 5. Verifikation

- `dotnet build`: 0 Fehler (nur bereits vorhandene Warnungen)
- `dotnet test` (Solution): 201/201 grün - 130 Infrastructure (echtes PostgreSQL, +14 neu), 38 API, 26 Application, 7 ArchitectureTests (Dependency Direction unverändert grün)
- Migration: `20260923082233_StorageSyncCheckpoint` (versioniert, offline-anwendbar)
- Neue NuGet-Abhängigkeiten: keine (reine BCL + vorhandene EF Core)
