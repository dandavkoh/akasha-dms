# Phase 14 - Metadata System

**Datum:** 2026-09-23
**Scope:** `Akasha.Domain/Metadata` (`MetadataDefinition`, `MetadataValue`, `MetadataFieldType`, `MetadataOwnerType`, `MetadataValueInput`), `Akasha.Application/Metadata` (`IMetadataService`), `Akasha.Infrastructure/Metadata` (`MetadataService`), EF-Konfiguration + Migration `Met20260923171159_Metadata` (`metadata_definitions`, `metadata_values`), `Akasha.Api/Controllers/MetadataController`, Tests: `Akasha.Application.Tests/MetadataDefinitionTests`, `Akasha.Infrastructure.Tests/Metadata/MetadataServiceTests`, `Akasha.Api.IntegrationTests/MetadataApiTests`.
**Referenzen:** Root-`AGENTS.md` #33-#35, #41; Backend-`AGENTS.md` #41, #62, #165-#167; baut auf Phase 4 (Tenant-Filter), 6 (Authorization), 9-11 (Document/Akte) auf.

---

## 1. Ziel

Flexible, tenant-konfigurierbare Metadaten für Dokumente und Akten - strikt relational, keine JSON-Businesslogik. Drei Ebenen sind getrennt:

1. **System Metadata**: typisierte Spalten auf den Entitäten selbst (`Document.Name`, `Storage.SyncState`, Timestamps, Akte-Status...) - unverändert, nicht Teil dieses Moduls.
2. **Configurable Metadata Definitions**: `MetadataDefinition` (Key, FieldType, Required, MaxLength, Options) - das Schema.
3. **Metadata Values**: `MetadataValue` - genau **eine typisierte Spalte** pro Wert (`TextValue`/`NumberValue`/`BooleanValue`/`DateValue`/`TimestampValue`/`SelectionValues`), passend zum FieldType der Definition. Query- und indexierbar, kein JSON-Blob (#41).

## 2. Umgesetzte Komponenten

| Komponente | Layer | Zweck |
|--|--|--|
| `MetadataFieldType` | Domain | Text, Number, Boolean, Date, DateTime (nur UTC), Selection (1 Option), MultiSelection (n Optionen) |
| `MetadataOwnerType` | Domain | Document / Akte - polymorpher Owner einer Value |
| `MetadataDefinition` | Domain | Tenant-Schema; invariant validiert (Key, Optionen nur bei Selection, MaxLength nur bei Text); `Validate(input)` liefert reine Domain-Validierung (Fehlermeldung oder null). `FieldType` ist immutabel (Typänderung = Feld neu anlegen) |
| `MetadataValue` | Domain | Ein Wert pro (TenantId, OwnerType, OwnerId, DefinitionId) - unique; `Apply` mappt validierten Input auf genau eine Spalte |
| `IMetadataService` + DTOs | Application | Definitions-CRUD (ResourceType.Metadata, Read/Write) + Value-Ops; Values erben die Owner-Authorization (Document/Akte: Read zum Lesen, `EditMetadata` zum Schreiben, #10 vor Datenzugriff) |
| `MetadataService` | Infrastructure | Authz + Owner-Existenz (tenant-gefiltert: fremder Owner/Definition = NotFound), Validierung (400), Upsert mit Unique-Index-Schutz (konkurrierende Erst-Inserts = 409, #28), Definition-Updates über xmin (#28), Delete kaskadiert auf Values (FK Cascade; Audit überlebt, #169), Audit-Aktionen `metadata.definition.*`/`metadata.value.*` |
| `MetadataController` | Api | `GET/POST/PATCH/DELETE /api/v1/metadata/definitions[/{id}]`; Values: `GET/PUT/DELETE /api/v1/metadata/{documents|akten}/{ownerId}[/{key}]` |

**Indizes (Search/Index-Vorbereitung):** unique `(TenantId, Key)` auf Definitions, unique `(TenantId, OwnerType, OwnerId, DefinitionId)` sowie `(TenantId, DefinitionId)` auf Values. ponytail: bewusst nur btree - typ-selektive Value-Indizes oder pg_trgm für Text-LIKE erst bei echten Query-Mustern.

## 3. Tests (20 neu)

- **Domain (5, ohne DB):** Definitions-Invarianten, Validierungsmatrix pro Typ (Shape/Required/Länge/Optionen/UTC), `Apply` setzt genau eine Spalte.
- **Service (11, echtes PostgreSQL):** Create+Audit, Duplicate-Key 409 (tenant-scoped unique), Validierungsfehler, Definitions ohne Grant 403 / Cross-Tenant NotFound, Definition-Update mit xmin-Konflikt (409) + Audit, Roundtrip aller 7 Typen auf einem Dokument, Set-Validierung (required/leer, Überlänge, falsche Shape, unbekannte Option, unbekannter Key - ohne Nachwirkungen), Owner-Authorization + Owner-Existenz + Cross-Tenant-NotFound (eigener fremder Dokumentzugriff), Value-Overwrite + Remove + Audit, Akte-Owner braucht Akte-Grants, Definition-Delete kaskadiert Values + Audit.
- **API (4):** 401 unauthenticated, 403 ohne Grants, Definitions-CRUD + Value-Roundtrip über HTTP (inkl. 400 bei Validierung), Cross-Tenant-Dokument 404.

## 4. Architekturentscheidungen / YAGNI

| # | Entscheidung | Grenze / Upgrade-Pfad |
|--|--|--|
| P1 | FieldType immutabel; Typänderung = neues Feld | Typ-Migration von Bestandswerten einführen, wenn Produkt es verlangt |
| P2 | `IsRequired` erzwingt nur nicht-leere Werte beim Setzen, kein Dokument-Vollständigkeitscheck | Completeness-Validierung über alle Pflichtfelder kommt mit Workflows/Freigaben |
| P3 | Polymorpher Owner (OwnerType + OwnerId, kein FK über zwei Tabellen) | Owner-Existenz prüft der Service tenant-gefiltert; bei drittem Owner-Typ ggf. eigene Tabellen abwägen |
| P4 | Value-Upsert ohne eigenes xmin; Race nur beim Erst-Insert möglich → Unique-Violation = 409 Retry | Letzter-Schreiber-gewinnt bei Updates ist hier akzeptabel; ETag-Concurrency auf Values erst bei nachgewiesenem Bedarf (#28) |
| P5 | Keine Text-Suchindizes (pg_trgm/fts) in dieser Phase | btree reicht als Vorbereitung; Search-Modul ergänzt spezialisierte Indizes (#44) |
| P6 | EF: Options/SelectionValues als `List<string>`-Backing-Field hinter `IReadOnlyList<string>` | EF10 materialisiert primitive Collections als `List<T>`; `string[]`-Backing-Field wirft InvalidCast beim Laden - daher List |

## 5. Verifikation

- `dotnet build` (Solution): 0 Fehler (nur bereits vorhandene Warnungen)
- `dotnet test` (Solution): **221/221 grün** - 141 Infrastructure (echtes PostgreSQL, +11), 42 API (+4), 31 Application (+5), 7 ArchitectureTests (Dependency Direction unverändert grün)
- Migration: `20260923171159_Metadata` (versioniert, offline-anwendbar)
- Neue NuGet-Abhängigkeiten: keine (reine BCL + vorhandenes EF Core/Npgsql)
