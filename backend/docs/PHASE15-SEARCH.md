# Phase 15 - Search Foundation

**Datum:** 2026-09-24
**Scope:** `Akasha.Application/Search` (`ISearchService`, `DocumentSearchRequest`, `DocumentSearchHit`, `DocumentSearchSort`), `Akasha.Infrastructure/Search/SearchService`, pg_trgm-Migration `20260924071123_SearchTrigramIndex` (Extension + GIN-Index auf `documents.name`), `Akasha.Api/Controllers/SearchController`, Tests: `Akasha.Infrastructure.Tests/Search/SearchServiceTests` (12), `Akasha.Api.IntegrationTests/SearchApiTests` (4).
**Referenzen:** Root-`AGENTS.md` #16; Backend-`AGENTS.md` #10, #44, #45, #81, #82, #106, #144-#146; baut auf Phase 4 (Tenant-Filter), 6 (Authorization), 9 (Documents), 10/11 (Akten), 14 (Metadata) auf.

---

## 1. Ziel

Zentrale serverseitige Sucharchitektur für logische Dokumente. Suchbar: Dokumentname und Text-Metadaten (freier Query-Text), dazu Filter auf Akte, Dokumenttyp (Dateiendung), Daten (created/updated), Sync-State und typisierte Metadaten-Werte. Sortierung (Relevanz/Name/Created/Updated), Pagination (skip/take, max 100).

Kernprinzip (Regel #81): **Authorization ist Teil der Query, kein nachträglicher Filter.** Ein Benutzer kann die Existenz eines nicht zugänglichen Dokuments nicht erkennen - weder über Treffer noch über die Trefferzahl (`Total` zählt nur sichtbare Zeilen, gleiche Query).

## 2. Umgesetzte Komponenten

| Komponente | Layer | Zweck |
|--|--|--|
| `ISearchService` + DTOs | Application | Einzige Such-Abstraktion (`SearchDocumentsAsync`); spätere Full-Text/OCR/Semantic-Engines stecken hinter demselben Vertrag (#44/#45). Hits enthalten bewusst **keine** Speicherpfade und keine Metadateninhalte (#81) |
| `SearchService` | Infrastructure | Faltet Tenant (DbContext-Query-Filter) UND Grants in einen SQL-Query: `EXISTS allow ∧ ¬EXISTS deny` über User-/Gruppen-/Rollen-Grants, resource- oder type-level bzw. tenant-weit (Admin-Read deckt Read) - exakt dieselbe Semantik wie `AuthorizationService`. Fehlende Identität/Tenant-Kontext = Exception (401/403 über `ProblemExceptionHandler`), niemals stille Leerseite |
| GIN/pg_trgm-Index | Infrastructure | `ix_documents_name` (gin_trgm_ops) + `CREATE EXTENSION pg_trgm` via Migration - ILIKE '%q%' nutzt einen Index statt Seq-Scan (#165). Offline-anwendbare Migration |
| `SearchController` | Api | `GET /api/v1/search/documents` mit Query-Parametern; dünn, nur Binding |

**Filter:** `query` (Name oder Text-Metadaten, ILIKE, wildcard-escaped), `akteId` (über `AkteDocument`, logische Identität #42), `documentType` (Endung, validiert), `metadataKey`+`metadataValue` (typgetreu pro `MetadataFieldType`: contains für Text, Gleichheit für Number/Boolean/Date/DateTime-UTC/Selection, Element-Match für MultiSelection), `createdFrom/To`, `updatedFrom/To`, `state`. Unbekannter Metadata-Key = leere Treffermenge (kein Existenz-Leak), unparsebarer Wert = 400.

## 3. Tests (16 neu)

- **Service (12, echtes PostgreSQL):** kein Grant → leer; Cross-Tenant-Leakage (gleicher Dokumentname, fremder Tenant zählt nicht mit); Deny schlägt type-level Allow; Gruppen-/Rollen-Vererbung; Relevanz-Ranking (exact > prefix > substring > Metadata-only, case-insensitive); Query trifft Text-Metadaten; Filter Akte/State/DocumentType/Datumsfenster; typisierte Metadata-Filter + unbekannter Key leer + unparsebares Number → 400; Pagination zählt nur sichtbare, stabil über Seiten (`ThenBy(Id)`); Sortierung Name asc/desc; Validierungsfehler; fehlende Identität → 401-Exception.
- **API (4):** 401 unauthenticated; Roundtrip zeigt nur lesbare Dokumente; Cross-Tenant über HTTP unsichtbar; `take=0` → 400.

## 4. Architekturentscheidungen / YAGNI

| # | Entscheidung | Grenze / Upgrade-Pfad |
|--|--|--|
| P1 | Suche läuft direkt auf den relationalen Tabellen, kein separater Index | Full-Text/OCR (tsvector/pg_trgm-Index-Tabelle) und später pgvector hinter `ISearchService` ergänzen, wenn Processing-Pipeline (Phase: Document Processing) Inhalte extrahiert (#24, #32). Die Query-Faltung von Tenant+Grants bleibt identisch, nur die Treffermenge kommt dann aus dem Index |
| P2 | Freier Query-Text matcht Name + Text-Metadaten, nicht OCR/Inhalt | Inhaltssuche braucht extrahierten Text (#45-Vorbereitung existiert als Filter-Faltung) |
| P3 | Relevanz = 3-stufige Heuristik (exact/prefix/substring) statt ts_rank | ponytail: ausreichend für Namens-/Metadaten-Suche; echtes Ranking mit tsvector/späterer Engine |
| P4 | DocumentType = Dateiendung im Namen | Fachlicher Dokumenttyp ist ein Metadatenfeld (Phase 14) und damit bereits filterbar; dedizierte Systemspalte nur bei Produktanforderung |
| P5 | Metadatenfilter Gleichheit für skalare Typen, contains nur für Text | Bereichsfilter (number/date ranges) bei Bedarf ergänzen - Struktur (typgetreue Spalten) ist darauf vorbereitet |
| P6 | Wiederholte Schreibweise des Grant-Prädikats (Allow/Deny) in einer LINQ-Where | Duplikat der `AuthorizationService`-Semantik, aber in SQL-formulierbar; bei Änderung der Grant-Semantik beide Stellen anfassen (Tests decken Deny/Vererbung ab) |

## 5. Verifikation

- `dotnet build` (Solution): 0 Fehler (nur bereits vorhandene Warnungen)
- `dotnet test` (Solution): **237/237 grün** - 153 Infrastructure (+12), 46 API (+4), 31 Application, 7 ArchitectureTests (Dependency Direction unverändert grün)
- Migration: `20260924071123_SearchTrigramIndex` (versioniert, `CREATE EXTENSION IF NOT EXISTS pg_trgm`, offline-anwendbar)
- Neue NuGet-Abhängigkeiten: keine (reine BCL + vorhandenes EF Core/Npgsql)
