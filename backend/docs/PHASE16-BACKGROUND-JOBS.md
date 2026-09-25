# Phase 16 - Background Job Infrastructure

**Datum:** 2026-09-24
**Scope:** `Akasha.Domain/Jobs/BackgroundJob` (+ `BackgroundJobStatus`), `Akasha.Application/Jobs` (`IBackgroundJobQueue`, `EnqueueBackgroundJob`, `IBackgroundJobHandler`, `BackgroundJobContext`, `PermanentJobFailureException`), `Akasha.Application/Common/ICorrelationContext`, `Akasha.Infrastructure/Jobs` (`BackgroundJobQueue`, `BackgroundJobWorker`, `BackgroundJobOptions`), EF-Config + Migration `20260924134944_BackgroundJobs`, `CorrelationIdMiddleware` speist jetzt `ICorrelationContext`, Tests: `Akasha.Infrastructure.Tests/Jobs/BackgroundJobTests` (14).
**Referenzen:** Root-`AGENTS.md` #29, #43, #90; Backend-`AGENTS.md` #33, #34, #35, #79, #80, #135, #137, #157-#160; baut auf Phase 3 (Persistenz), 4 (TenantContext), 5 (Identity/CurrentUser) auf.

---

## 1. Ziel

Durable, tenant-sichere Hintergrundverarbeitung fuer connector sync, OCR, Indexing, Embeddings, Workflows, Notifications und AI - ohne neuen Broker (Regel "keine Infrastruktur ohne Requirement"): **die PostgreSQL-Tabelle `background_jobs` IST die Queue**. Restart-sicher, sichtbar, transaktional konsistent mit dem Rest der DMS-Daten.

Jeder Job traegt fachlichen Kontext (Regel #33): `TenantId`, `InitiatedByUserId/Subject/Provider/IsServiceAccount` (null = Systemjob), `OperationId`, `CorrelationId` (geerbt vom aufrufenden Request ueber `ICorrelationContext`, Fallback = eigene OperationId - nie leer).

## 2. Umgesetzte Komponenten

| Komponente | Layer | Zweck |
|--|--|--|
| `BackgroundJob` + `BackgroundJobStatus` (Queued/Running/Cancelled/Completed/Dead) | Domain | Job-Aggregat mit Zustandsuebergaengen als Entity-Methoden (`Cancel`, `Complete`, `MarkCancelled`, `ScheduleRetry`, `MoveToDead`); `MaxAttempts`-Invariante; Fehler-Truncate auf 2000 Zeichen |
| `IBackgroundJobQueue` (`EnqueueAsync`, `CancelAsync`) | Application | Einzige Schreib-Schnittstelle. Enqueue verlangt etablierten Tenant-Kontext (Regel #158) und fotografiert Identitaet + Korrelation in die Zeile. `CancelAsync` sieht dank Query-Filter nur eigene Tenant-Jobs |
| `IBackgroundJobHandler` + `BackgroundJobContext` + `PermanentJobFailureException` | Application | Handler-Vertrag pro `Type` (z.B. `storage.sync`, `document.ocr`). Beliebige Exception = transienter Fehler (Retry mit Backoff); `PermanentJobFailureException` = direkt Dead (#35) |
| `ICorrelationContext` | Application | Ambiente Korrelation pro Scope; Middleware etabliert sie, Queue uebernimmt sie in Jobs |
| `BackgroundJobQueue` | Infrastructure | EF-Implementierung; Idempotenz ueber Unique-Partial-Index `(tenant_id, type, idempotency_key) WHERE NOT NULL` - ein Race verliert gegen 23505 und bekommt die existierende Job-Id (kein doppelter Job, #34) |
| `BackgroundJobWorker` (`BackgroundService`) | Infrastructure | Poll-Schleife: Due-Query -> **atomarer Claim** via konditionellem `UPDATE ... WHERE status='Queued'` (zwei Worker koennen denselben Job nie doppelt ausfuehren) -> Ausfuehrung im **job-eigenen DI-Scope** mit etabliertem `ITenantContext`/`ICurrentUser`/`ICorrelationContext` -> Retry/Dead/Cancel. Laufende Jobs werden kooperativ ueber Flag-Polling gecancelt (Linked CTS, #135); Host-Shutdown laesst den Row in Running (#160) |
| `RecoverAbandonedJobsAsync` | Infrastructure | Startup-Recovery: alle Running-Jobs zurueck nach Queued (Restart/Resume). ponytail: nimmt **einen** Worker pro Datenbank an; Multi-Worker braucht Heartbeat-Spalte + Requeue-Stale-by-Timeout statt Requeue-all |
| `BackgroundJobOptions` (Retry-Backoff exp. + Jitter, MaxAttempts=5, BatchSize=10, Poll/Cancel-Intervalle) | Infrastructure | Konfiguration `BackgroundJobs:*`, validiert via DataAnnotations |

**Retry:** exponentiell `InitialRetryDelay * 2^(attempt-1)`, gedeckelt auf `MaxRetryDelay`, ±20% Jitter (#137). Nach `MaxAttempts` Fehlversuchen -> **Dead** mit `LastError` (administrative Aufloesung, nicht stille Wiederholung).

**Tenant-Sicherheit im Worker:** Der Poller liest bewusst cross-tenant (`IgnoreQueryFilters`), legt dann aber pro Job einen frischen Scope an und etabliert dort `job.TenantId` + initiierende Identitaet (User oder System-Service-Account ohne Default-Grants, #79). Der scoped DbContext filtert und der Save-Guard blockt Cross-Tenant-Writes auch im Job (beides getestet). Handler ohne Registrierung -> Dead ("not silently lost").

## 3. Tests (14 neu, echtes PostgreSQL)

Enqueue ohne Tenant -> `MissingTenantContextException`; Job traegt Tenant/Identity/OperationId/CorrelationId in die Ausfuehrung; Systemjob ohne Identitaet laeuft als Service-Account; transienter Fehler -> Backoff-Retry mit zukuenftigem `ScheduledAtUtc` (nicht vor Faelligkeit gepollt) -> Erfolg im 2. Versuch; Retry-Erschoepfung -> Dead (kein weiterer Lauf); permanenter Fehler -> Dead ohne Retry; unbekannter Typ -> Dead; Idempotency-Key dedupliziert Enqueue (auch nach Abschluss) und verhindert Doppelausfuehrung; zwei Worker gleichzeitig -> Job laeuft exakt einmal; Cancel queued -> uebersprungen; Cancel running -> Handler-Token gecancelt -> Cancelled; Crash zwischen Claim und Ausfuehrung -> Startup-Recovery fuehrt aus; Host-Shutdown mitten im Lauf -> Row bleibt Running, Recovery + Wiederholung -> Completed; Tenant-Isolation waehrend Ausfuehrung (jeder sieht nur eigene Rows) und Cross-Tenant-Cancel -> NotFound; Cross-Tenant-Write im Handler -> Save-Guard wirft.

## 4. Architekturentscheidungen / YAGNI

| # | Entscheidung | Grenze / Upgrade-Pfad |
|--|--|--|
| P1 | Kein Hangfire/Quartz: PostgreSQL-Tabelle als Queue | Der worker-interne Claim ist bereits atomar; bei horizontalem Multi-Worker-Skalieren Heartbeat + Lease-Spalten ergaenzen (oder Hangfire hinter denselben Application-Abstraktionen ersetzen) |
| P2 | Payload als opaker `text`; Serialisierung bleibt Sache des jeweiligen Moduls | Typsichere Enqueue-Overloads pro Handler bei Bedarf |
| P3 | `OperationId` = Guid je Enqueue, `CorrelationId` geerbt vom Request oder = OperationId | Anbindung an Audit-Entries (#56) sobald Handler fachlich auditable Operationen ausloesen |
| P4 | Kein Admin-/Status-API-Endpoint fuer Jobs | Bei Betriebsanforderung duenner Controller auf Lesemodell (#36) |
| P5 | Keine Metriken-Pipeline | Strukturierte Log-Scopes (TenantId/JobId/OperationId/CorrelationId auf jedem Job-Log); OpenTelemetry bei Observability-Phase (#90) |
| P6 | Cancellation-Flag-Polling im Worker-Intervall (default 500 ms) statt Instant-Signal | 'Gut genug' fuer kooperativen Abbruch; LISTEN/NOTIFY erst bei nachgewiesener Latenzanforderung |
| P7 | Recurring/scheduled Jobs (`NotBeforeUtc` existiert) ohne Scheduler-Modul | Cron-Scheduler als eigener Handler-Typ wenn Workflows/Phase es verlangt (#157) |
| P8 | Neue NuGet-Abhaengigkeit: `Microsoft.Extensions.Hosting.Abstractions` (Infrastruktur), sonst BCL/EF | -- |

## 5. Verifikation

- `dotnet build` (Solution): 0 Fehler (nur bereits vorhandene Warnungen)
- `dotnet test` (Solution): **251/251 gruen** - 167 Infrastructure (+14), 46 API, 31 Application, 7 ArchitectureTests (Dependency Direction unveraendert gruen)
- Migration: `20260924134944_BackgroundJobs` (versioniert, offline-anwendbar; Partial Unique Index + Poller-Index `(status, scheduled_at_utc)`)
