# Phase 6 - Authorization

**Datum:** 2026-09-18
**Scope:** `backend/src/Akasha.Domain`, `backend/src/Akasha.Application`, `backend/src/Akasha.Infrastructure`, `Akasha.Infrastructure.Tests`
**Referenzen:** `/AGENTS.md` #14, `/backend/AGENTS.md` #9, #10, #17 (#17 backend = Storage Permissions), Phase 4/5

---

## 1. Ziel und Auftrag

Zentrales serverseitiges Berechtigungsmodell: Principals (User, Group, Role, Service Account), Ressourcen (Tenant, Akte, Document, Folder, Workflow, Metadata, Connector, AdminSettings), 13 Actions, Vererbung, explizite Deny-Overrides. Keine neue NuGet-Abhängigkeit nötig (BCL + EF Core reichen).

---

## 2. Umgesetzte Komponenten

| Komponente | Umsetzung |
|--|--|
| `PermissionAction` / `ResourceType` / `PrincipalType` / `GrantEffect` (Domain) | Enums; Aktion/Resourcentyp als string-konvertierte Spalten |
| `Role`, `Group` (Domain) | tenant-scoped, eindeutiger Name pro Tenant (`(tenant_id, name)` unique) |
| `GroupMember` (Domain) | PK `(GroupId, UserId)`; FKs Group/User cascade, Tenant restrict |
| `RoleAssignment` (Domain) | Role → User **oder** Group (Rollen → Rollen im Konstruktor verboten); PK `(RoleId, PrincipalType, PrincipalId)` |
| `PermissionGrant` (Domain) | `(TenantId, PrincipalType, PrincipalId, ResourceType, ResourceId?, Action, Effect)`. `ResourceId == null` = Type-Level-Grant (Vererbung auf alle Ressourcen des Typs). Principal bewusst **ohne** FK (polymorpher Verweis über 3 Tabellen) |
| `IAuthorizationService` (Application) | `IsAuthorizedAsync(CurrentUser, ResourceType, ResourceId?, Action)` + Default-`RequireAsync` wirft `AccessDeniedException` |
| `AuthorizationService` (Infrastructure) | EF-Auswertung, siehe Auswertungsregeln; läuft vollständig im Tenant-Kontext (globale Query-Filter + Save-Guards gelten auch für Grants) |
| Migration | `Authorization`: Tabellen `roles`, `groups`, `group_members`, `role_assignments`, `permission_grants` |

### Auswertungsregeln (AuthorizationService)

1. **Default deny:** ohne passenden Allow → verweigert.
2. **Sammelmenge:** direkte User-Grants + Grants der Gruppen des Users + Grants aller Rollen, die dem User oder einer seiner Gruppen zugewiesen sind.
3. **Vererbung:** Type-Level-Grant (`ResourceId == null`) gilt für alle Ressourcen des Typs; Tenant-Grant (`ResourceType.Tenant, ResourceId == null`) gilt tenantweit; Tenant-`**Admin**`-Allow deckt jede Action im Tenant ab.
4. **Override:** jeder passende **Deny** schlägt alle Allows (auch Tenant-Admin).
5. **Service Accounts:** identischer Pfad wie Personen, keine Sonderrechte (backend #122).

Cross-Tenant-Sicherheit entsteht strukturell: Grants sind `ITenantScoped`, die globalen Query-Filter aus Phase 4 machen fremde Grants unsichtbar und der Save-Guard blockiert Fremd-Schreibzugriffe. Zusätzlich deckt der Cross-Tenant-Test ab, dass ein Grant-Tupel aus Tenant A in B weder gelesen noch ausgewertet wird.

---

## 3. Architekturentscheidungen

| # | Entscheidung | Begründung |
|--|--|--|
| P1 | Ein `PermissionGrant`-Modell statt ACL-Tabellen pro Ressourcentyp | Ein Auswertungspfad, eine Tabelle; Ressourcen (Akte, Document...) existieren teils noch nicht als Entitäten - polymorphe `ResourceId` vermeidet Schema-Churn |
| P2 | Rollen an User **oder** Gruppen, nie an Rollen | Endlose Rollen-Rollen-Rekursion bringt keinen Mehrwert; Konstruktor verbietet es |
| P3 | `ResourceId?` statt Vererbungshierarchie-Tabelle | Akte→Document-Vererbung benötigt noch keine Beziehungstabelle, solange Document/Akte nicht modelliert sind; Type-Level + Tenant-Level deckt "Vererbung" vorerst ab (siehe Tech-Debt) |
| P4 | Tenant-Admin-Allow deckt alle Actions, Deny schlägt es trotzdem | "Admin" als Supergrant ist Produktanforderung (admin access-Test); explizite Overrides müssen laut Auftrag stärker sein |
| P5 | Keine API-Endpunkte für Grant-Verwaltung | YAGNI: noch kein Admin-UI; Verwaltung folgt mit dem ersten Consumer. Service hängt an DI und ist überall nutzbar |
| P6 | Keine neue Library | Auswertung ist eine EF-Query mit drei kleinen Reads; ein externes Policy-Framework wäre Overhead |

---

## 4. Effective Permission (Vorbereitung Storage-Regel)

`IAuthorizationService` liefert ausschließlich die **DMS-Seite**. Die Schnittstelle dokumentiert explizit:

```text
Effective Permission = DMS Permission ∩ Storage Permission
```

Storage-seitige Prüfung kommt mit den Storage Connectors (backend #17): Connectors kapseln die Provider-Prüfung, der Document-Anwendungspfad kombiniert beides. Ein DMS-Allow darf ein Storage-Deny niemals aufheben - der aktuelle Service hat keinen Codepfad, der Storage-Ergebnisse überstimmen könnte.

---

## 5. Verifikation

- `dotnet build`: **0 Fehler**
- `dotnet test`: **64/64 grün** (7 Architektur + 36 Infrastructure + 21 API-Integration)
- Dependency Direction unverändert sauber (ArchitectureTests grün; Domain/Application ohne EF/ASP.NET-Referenzen)

Geforderte Testfälle (`AuthorizationTests`, 12 Tests, echtes PostgreSQL via Testcontainers):

| Anforderung | Test |
|--|--|
| default deny | `No_grant_means_denied` |
| user grant | `Direct_user_grant_allows` (inkl. Fremd-Action/Fremd-Resource bleiben verboten) |
| group | `Group_grant_applies_to_members` (Nicht-Mitglied verweigert) |
| role (user + group assignment) | `Role_grant_applies_to_assigned_users_and_groups` |
| inheritance | `Type_level_grant_is_inherited_by_all_resources_of_that_type`, Tenant-Admin-Tests |
| override | `Explicit_deny_overrides_allow`, `Deny_via_group_beats_direct_user_allow`, `Explicit_deny_overrides_tenant_admin` |
| admin access | `Tenant_admin_grant_allows_everything_in_tenant` |
| cross-tenant | `Cross_tenant_grants_are_never_inherited` |
| service accounts | `Service_accounts_use_the_same_authorization_path` |
| RequireAsync | `RequireAsync_throws_AccessDenied_when_denied` |
| Eindeutigkeit | `Role_names_are_unique_per_tenant` |

---

## 6. Tech-Debt / nachgelagert

- **Akte→Document-Vererbung:** sobald `Akte`/`Document`-Entitäten existieren, Grants entlang der Akte-Beziehung vererben (aktuell nur Resource → Type-Level → Tenant-Level).
- **Grant-Cache:** Auswertung sind 3 kleine Queries; Caching über `tenant:{id}:permissions:{userId}` erst bei Messbarkeit (backend #64), Invalidierung bei Grant-/Rollen-/Gruppen-Änderung.
- **Verwaltungs-Endpunkte** (Grant/Rollen/Gruppen-CRUD) mit erstem Admin-Consumer; verlangen selbst `AdminSettings/Admin`-Authorization über denselben Service.
- **Storage Permission Enforcement** mit den Storage Connectors (Intersection im Document-Anwendungspfad).
- **Search/AI/MCP** müssen denselben `IAuthorizationService` nutzen, sobald diese Module entstehen (kein Zweitmodell, backend #49).

Phase 6 gilt mit diesem Stand als abgeschlossen. Änderungen sind nicht committet (kein Commit ohne ausdrückliche Anweisung).
