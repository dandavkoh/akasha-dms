# Phase 5 – Identity Foundation

**Datum:** 2026-09-18
**Scope:** `backend/src/Akasha.Domain`, `backend/src/Akasha.Application`, `backend/src/Akasha.Infrastructure`, `backend/src/Akasha.Api`, alle drei Testprojekte
**Referenzen:** `/AGENTS.md` (root), `/backend/AGENTS.md` („backend #NN"), `PHASE0-INVENTORY.md` … `PHASE4-MULTITENANCY.md`

---

## 1. Ziel und Auftrag

Interne Identity-Grundlage, strikt getrennt:

- **Identity** = Wer ist der Benutzer? (Subjekt, Provider, User)
- **Authorization** = Was darf der Benutzer? (folgt in einer späteren Phase)

Scope: User, UserIdentity (Tenant-Zugehörigkeit), Authentication Context (`ICurrentUser`), Service-Account-Grundlagen, Identity-Provider-Abstraktion als Vorbereitung für Entra ID / AD / LDAP / OIDC / SAML (keine echten Provider-Integrationen).

Regeln: Domain kennt keinen konkreten Provider (backend #39, #40), stabile User-IDs, Tenant-Zugehörigkeit serverseitig validiert, Service Accounts ohne implizite Sonderrechte (backend #122).

---

## 2. Ausgangszustand

Phase 4 brachte Tenant-Isolation über `TenantMembership` (TenantId + Subject) und `ITenantResolver`. Zwei parallele Konzepte drohten (`TenantMembership` vs. `UserIdentity`): Die Membership-Tabelle **ist** faktisch die User-Tenant-Zuordnung und wurde daher durch `User` + `UserIdentity` **ersetzt** (Deletion over Addition), statt ein zweites, divergierendes Konzept danebenzustellen.

---

## 3. Umgesetzte Komponenten

| Komponente | Umsetzung |
|--|--|
| User (Domain) | `Domain/Identity/User.cs`: tenant-scoped (`ITenantScoped`), stabile `Guid.CreateVersion7()`-ID, `UserKind` (Person/ServiceAccount), `IsActive` (deaktivierte Nutzer behalten Identität+Historie, können sich aber nicht authentifizieren), `xmin`-Concurrency |
| UserIdentity (Domain) | `Domain/Identity/UserIdentity.cs`: `(Provider, ExternalSubject) → UserId`, Unique-Index `(provider, external_subject, user_id)` — dasselbe externe Subjekt darf User in mehreren Tenants haben; FKs zu Users/Tenants `Restrict`. Nur Provider-**Key** + stabiles Subjekt, keine provider-spezifischen Felder (backend #39) |
| Authentication Context | `Application/Identity/ICurrentUser.cs`: `CurrentUser` (UserId, TenantId, Subject, Provider, IsServiceAccount) + `CurrentUserContext` (scoped DI, Establish idempotent, Wechsel verboten — analog `TenantContext`) |
| Provider-Abstraktion | `Application/Identity/IIdentityProvider.cs`: `Map(ClaimsPrincipal) → ExternalIdentity?`. Eine Implementierung pro Provider kapselt dessen Claim-Mapping; Domain/Application sehen nur das normalisierte Ergebnis (backend #40) |
| Resolver | `Application/Identity/IIdentityResolver.cs` (ersetzt `ITenantResolver`) → `Infrastructure/Identity/IdentityResolver.cs`: mapped Principal über registrierte Provider, validiert gegen persistierte User-Identitäten (`IgnoreQueryFilters` dokumentiert — noch kein Tenant-Kontext), filtert inaktive User, honoriert `tenant_id`-Claim nur mit valider Zuordnung (Spoofing → 403), Mehrfach-Tenants ohne Auswahl → `Ambiguous` |
| Interner Provider | `Infrastructure/Identity/InternalIdentityProvider.cs`: `sub`/`NameIdentifier`-Claim; Service-Account-Erkennung über `client_id`-Claim oder Entra `idtyp=app`; verweigert nicht-authentifizierte Principals |
| Service Accounts | `UserKind.ServiceAccount` + `IsServiceAccount` in `CurrentUser`. **Gleicher** Validierungspfad wie Personen: Tenant-Zuordnung Pflicht, Spoofing verboten, keinerlei implizite Rechte (backend #122) — per Test belegt |
| Middleware | `TenantResolutionMiddleware`: nutzt `IIdentityResolver`, etabliert `ICurrentUser` **und** `ITenantContext`; Failure-Mapping unverändert (401/403/409) |
| API-Grundlage | `Program.cs`: `AddAuthentication()` registriert (fehlte — `UseAuthentication` ohne Registrierung war latenter Laufzeitfehler außerhalb der Tests) |
| Migration | `Migrations/20260918122814_IdentityFoundation.cs`: erstellt `users` + `user_identities`, droppt `tenant_memberships` |
| DI | `AddInfrastructure`: `ICurrentUser`, `IIdentityResolver`, `IIdentityProvider` via `TryAddEnumerable` (weitere Provider registrieren sich daneben, nicht dagegen) |

**Migration bewusst destruktiv:** `tenant_memberships` wird gedroppt. Akzeptabel, weil die Tabelle erst in Phase 4 entstand und noch keine Bestandsdaten enthält; der einzige Consumer (Resolver + Tests) wurde zeitgleich umgestellt.

---

## 4. Architekturentscheidungen

| # | Entscheidung | Begründung / Vorgabe |
|--|--|--|
| P1 | `TenantMembership` ersetzt statt ergänzt | Die Membership war die User-Tenant-Zuordnung; `User` + `UserIdentity` tragen dieselbe Aussage vollständig. Zwei Tabellen = ein Konzept doppelt, Auseinanderlaufen vorprogrammiert |
| P2 | Identity- und Tenant-Resolution in **einem** Resolver | Beide entstehen aus derselben Vertrauensabfrage (Subject → User → Tenant). Zwei Resolver würden dieselbe Query doppelt fahren oder aus dem Rhythmus geraten |
| P3 | `CurrentUser` enthält keine Rechte/Rollen | Authorization ≠ Identity (backend #40, root #14). Rollen/Permissions folgen in der Authorization-Phase als eigenes Modell |
| P4 | `IIdentityProvider`-Kette statt Config-Switch | pro Provider eine Implementierung mit eigenem Claim-Mapping; `FirstOrDefault(map)` — Entra/OIDC/SAML/LDAP registrieren sich später einfach dazu (TryAddEnumerable) |
| P5 | Service-Account-Erkennung im Provider, User-Art in der Domäne | Claim-Auswertung ist Provider-Sache; `UserKind` ist das stabile Domänenkonzept. `CurrentUser.IsServiceAccount` = OR aus beiden (Token-Behauptung oder User-Stamm) |
| P6 | UserIdentities tenant-scoped + User-Verweis | Isolation erbt über den globalen Filter; Auflösung läuft bewusst filterfrei (Trust Root), alles Weitere unterliegt dem Filter |
| P7 | Kein `/me`-Endpoint, kein User-CRUD, keine Gruppen/Rollen | YAGNI: noch kein Consumer. Verwaltungsoberfläche folgt mit der Authorization-Phase |

---

## 5. Verifikation (Phase-5-Abschlusskriterien)

- `dotnet build`: **0 Fehler**
- `dotnet test`: **51/51 grün** (7 Architektur + 23 Infrastructure + 21 API-Integration)
- Architektur-Tests bestätigen: Domain/Application weiterhin frei von ASP.NET/EF/Provider-SDKs; `IIdentityProvider` arbeitet auf BCL-`ClaimsPrincipal` (backend #36)

Geforderte Testfälle:

| Anforderung | Test |
|--|--|
| authenticated user | `Authenticated_identity_resolves_user_and_tenant` (+ Provider-Mapping via InternalIdentityProvider) |
| unauthenticated request | `Principal_without_subject_is_unauthenticated`, `Unauthenticated_principal_cannot_be_mapped` (Principal ohne AuthenticationType), API: `Unauthenticated_requests_have_no_tenant_context` (401) |
| tenant association | `Selected_tenant_is_honored_only_with_valid_association` (Spoofing → Forbidden), `Multiple_tenants_without_selection_are_ambiguous`, API-Pendants |
| invalid identity | `Unknown_identity_is_rejected` (NoMembership), `Inactive_user_is_rejected`, `Identity_links_are_unique_per_provider_subject_and_user` |
| service identity | `Service_identity_resolves_with_service_account_flag`, `App_only_principal_of_a_person_user_is_flagged_by_claim`, API: `Service_identity_uses_the_same_validation_path` (200), `Service_identity_is_not_exempt_from_tenant_validation` (403 — kein Bypass) |

Tenant-Isolation aus Phase 4 unverändert durch `TenantIsolationTests` abgesichert (jetzt auf `User`-Entität statt Membership). Dependency Direction unverändert sauber.

---

## 6. Tech-Debt-Register / zurückgestellt

### Direkt nachgelagerte Phasen
- **Authorization-Phase:** Rollen/Gruppen/Permissions-Modell auf `User`-Basis; `CurrentUser` liefert die Identität, niemals Rechte
- **User-Administration:** CRUD für User/Identitäten + Tenant-Onboarding; heute gibt es keinen Endpoint (YAGNI)
- **Echte Provider:** Entra ID (`idtyp`, oid), OIDC (generisch), SAML, LDAP — jeweils eine weitere `IIdentityProvider`-Implementierung + JWT-Bearer-Authentifizierung; `TestAuthHandler` wird dabei produktiv durch `AddAuthentication().AddJwtBearer(...)` ersetzt

### Später
- Claim-Typ-Config pro Provider (Mapping `nameidentifier` ↔ `sub` ↔ `oid`) sollte mit dem ersten externen Provider konfigurierbar werden (offen aus Phase 4)
- Cache für Identity-Resolution über `TenantCacheKey`, sobald messbar

### Offene Punkte
- `tenant_id`-Claim heißt weiterhin so; bei OIDC-Multi-Tenant ggf. akteureigenes Header-Konzept definieren
- Migration `IdentityFoundation` droppt `tenant_memberships` — bei echter Beta-DB ggf. Datenmigration nötig (aktuell keine Bestandsdaten)

---

Phase 5 gilt mit diesem Stand als abgeschlossen. Änderungen sind nicht committet (kein Commit ohne ausdrückliche Anweisung).
