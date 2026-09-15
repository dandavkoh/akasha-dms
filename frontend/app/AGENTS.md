# AGENTS.md

## Scope

This file defines the frontend-specific architecture and engineering rules for **Akasha DMS**.

These rules supplement the repository-level `AGENTS.md`.

The frontend must implement the product principles defined in the root `AGENTS.md`, especially:

* strict tenant isolation
* server-side authorization
* customer-owned storage
* logical document identity
* external storage changes
* SaaS and self-hosted support
* offline-capable self-hosted operation
* modular architecture
* optional AI
* auditable operations
* accessible and responsive user experience

The frontend is a client of the backend. It is **never a security boundary**.

---

# 1. Frontend Technology

The frontend is implemented using:

* React
* TypeScript
* Tailwind CSS

Use modern React patterns and functional components.

Avoid introducing unnecessary frontend frameworks or state-management libraries.

The frontend should be Docker-friendly and deployable in:

* SaaS
* self-hosted
* offline self-hosted environments

The same fundamental frontend codebase should support all deployment models.

---

# 2. Frontend Architecture

Organize the frontend by business/domain modules rather than by arbitrary technical file types alone.

A possible structure:

```text
src/
├── app/
├── components/
├── features/
│   ├── auth/
│   ├── documents/
│   ├── akten/
│   ├── metadata/
│   ├── search/
│   ├── workflows/
│   ├── ai/
│   ├── admin/
│   ├── connectors/
│   ├── licensing/
│   └── billing/
├── api/
├── hooks/
├── state/
├── routes/
├── layouts/
├── types/
├── utils/
└── styles/
```

The exact structure may evolve.

The important principle is that business functionality should have clear module boundaries.

---

# 3. Feature-Based Organization

Prefer:

```text
features/documents/
features/akten/
features/search/
features/workflows/
```

over a large global structure such as:

```text
components/
services/
helpers/
models/
pages/
```

where all unrelated functionality becomes mixed together.

Shared components belong in shared areas only when they are genuinely reusable.

Do not put business-specific components into generic shared folders merely to avoid duplication.

---

# 4. Component Responsibilities

Components should have clear responsibilities.

Prefer:

```text
Page
  ↓
Feature Container
  ↓
Hooks / Application State
  ↓
API Client
  ↓
Backend
```

Avoid putting large amounts of:

* API logic
* authorization logic
* business rules
* data transformation
* state orchestration

directly into JSX components.

---

# 5. Thin Presentation Components

Reusable UI components should primarily handle:

* presentation
* user interaction
* accessibility
* local UI state

Business operations should be handled by feature-level hooks/services.

For example:

```text
DocumentRow
    ↓
useDocumentActions()
    ↓
documentsApi
    ↓
Backend
```

Avoid:

```text
DocumentRow
    ↓
fetch(...)
    ↓
permission logic
    ↓
storage operation
    ↓
business rules
```

---

# 6. Backend as Source of Truth

The frontend must treat the backend as authoritative for:

* permissions
* tenant membership
* document state
* storage state
* workflow state
* entitlements
* licensing
* billing state
* identity
* authorization

Never create a frontend-only version of authoritative business state.

The frontend may cache state for performance and user experience, but cached state must not override backend truth.

---

# 7. Frontend Is Not a Security Boundary

Never rely on:

* hidden buttons
* disabled buttons
* route guards
* frontend permission checks
* feature flags
* JavaScript state
* obscured API endpoints

for actual security.

A user must not gain access to a protected operation merely by manually calling the API.

The backend must always enforce authorization.

Frontend authorization is for:

* user experience
* navigation
* reducing confusion
* hiding unavailable actions

It is not security.

---

# 8. Tenant Context

The frontend may display tenant information and maintain the currently selected tenant context where the product supports multiple tenants.

However:

> The frontend must never be trusted to establish authorization for a tenant.

The backend must validate tenant membership and authorization.

Never assume that changing a frontend `tenantId` is sufficient to switch tenant access.

---

# 9. Tenant-Aware UI State

Client-side state that is tenant-specific must be scoped appropriately.

Examples:

* selected Akte
* document filters
* search state
* cached documents
* workflows
* connector configuration
* permissions
* metadata definitions

Do not accidentally reuse tenant A's cached state while rendering tenant B.

When switching tenants, tenant-specific state should be invalidated or isolated.

---

# 10. Authentication

Authentication should be integrated through the backend/application authentication model.

The frontend must not implement its own independent identity system.

Supported identity mechanisms may include:

* internal authentication
* Entra ID
* Active Directory
* LDAP
* OpenLDAP
* OIDC
* SAML

Provider-specific authentication behavior should be abstracted from most application features.

---

# 11. Authentication Tokens

Authentication tokens must be handled securely.

Never:

* display tokens
* log tokens
* store secrets unnecessarily
* expose refresh tokens to application components
* include credentials in URLs

Prefer secure browser authentication mechanisms appropriate to the backend architecture.

Authentication state should be centrally managed.

---

# 12. API Client

All backend communication should go through a centralized API layer.

Avoid scattered calls such as:

```typescript
fetch("/api/documents")
```

throughout arbitrary components.

Prefer:

```text
features/documents
    ↓
documentsApi
    ↓
apiClient
    ↓
backend
```

The API client should centrally handle common concerns such as:

* authentication
* request headers
* error normalization
* correlation information where appropriate
* retries where safe
* cancellation
* serialization

---

# 13. API Contracts

The frontend should consume the backend's OpenAPI contract where practical.

Generated TypeScript types or clients may be used if they improve consistency.

Do not manually duplicate backend DTO definitions throughout the application.

When the backend API changes, update the frontend contract deliberately.

---

# 14. No Direct Storage Access

The frontend must not directly access customer storage providers unless an explicitly designed frontend integration requires it.

Normal document operations should go through the Akasha backend.

Do not embed:

* SharePoint SDK logic
* Dropbox API logic
* SMB logic
* S3 credentials
* storage passwords

into ordinary frontend components.

---

# 15. Storage Provider Abstraction in UI

The frontend should consume normalized backend concepts.

For example, the UI should generally understand:

```text
StorageConnector
StorageCapability
StorageReference
DocumentAvailability
```

rather than building core UX around:

```text
if SharePoint
if Dropbox
if SMB
```

Provider-specific UI is allowed where a provider genuinely has unique configuration or capabilities.

The core document experience should remain provider-neutral.

---

# 16. Document Identity

The frontend must use the logical `DocumentId` as the primary document identity.

Do not use:

* filename
* path
* URL
* index position

as the identity of a document.

The UI must continue to reference the same logical document after:

* rename
* move
* external modification
* temporary disappearance
* restoration

---

# 17. Document State

The UI must distinguish between logical document identity and physical availability.

Possible states include:

```text
Available
Pending
Missing
Unavailable
SyncError
AuthenticationRequired
Processing
```

The exact state model is defined by the backend.

The frontend must not assume:

```text
document exists in database
=
physical file currently available
```

---

# 18. External Document Changes

The UI must be prepared for documents to change outside Akasha.

Examples:

* filename changed
* path changed
* content changed
* document deleted
* document restored
* document version changed

The frontend must update from backend state rather than assuming that its local state remains current.

---

# 19. External Rename

If a document is renamed externally, the frontend should eventually display the new name.

It must not:

* create a duplicate document
* lose the Akte relationship
* continue treating the old filename as the document identity

The UI should use the stable `DocumentId`.

---

# 20. External Move

If a document is moved externally:

* preserve the logical document identity
* display the current known location where appropriate
* preserve Akte relationships
* update lists/navigation when backend state changes

Do not treat a path as the document identity.

---

# 21. External Modification

If a document changes externally, the frontend should reflect the updated state.

Potential UI behavior:

```text
Document changed externally
        ↓
Backend detects change
        ↓
Frontend receives refreshed state
        ↓
UI updates
```

If the user currently has an outdated representation open, the UI should warn about stale state where relevant.

---

# 22. External Deletion

If a document is externally deleted, the UI must not silently behave as though it remains downloadable.

Instead, display an appropriate state such as:

```text
Document unavailable
```

or:

```text
Document missing
```

Depending on product UX, the UI may provide:

* refresh
* locate/reconcile
* restore
* administrative information

where supported.

---

# 23. External Restoration

If a missing document is restored externally, the frontend should reflect the restored availability after backend synchronization.

Do not create a new frontend document representation if the backend identifies it as the existing logical document.

---

# 24. Stale Data

The frontend must assume that displayed data can become stale.

This is especially important for:

* documents
* permissions
* metadata
* workflows
* connector state
* administrative configuration

Use appropriate refresh, polling, invalidation or realtime mechanisms.

Do not silently overwrite newer server state with stale client state.

---

# 25. Optimistic Updates

Optimistic UI updates may be used for safe operations where appropriate.

However, they must be reversible.

For operations involving:

* documents
* permissions
* storage
* workflows
* destructive actions

the UI should only show the operation as successfully committed once the backend confirms it.

For storage operations, do not permanently update the UI before successful backend confirmation unless the temporary optimistic state is clearly represented.

---

# 26. Concurrency Conflicts

The UI must handle backend concurrency conflicts.

Example:

```text
User A opens document
User B modifies document
User A attempts to modify document
        ↓
Backend detects newer version
        ↓
Conflict response
```

The frontend should:

* inform the user
* preserve their unsaved input where possible
* offer refresh/reload
* avoid silently overwriting the newer version

Do not hide concurrency conflicts.

---

# 27. Document Editing

For editable metadata or document properties, the UI should know whether the displayed version is still current.

Where the backend provides:

* version
* ETag
* revision
* last modified timestamp

the frontend may use that information to improve conflict handling.

The backend remains authoritative.

---

# 28. Unsaved Changes

Forms containing user-entered information should protect against accidental loss.

Examples:

* metadata editing
* workflow configuration
* connector configuration
* user/group editing
* AI configuration
* document properties

Warn before navigating away when meaningful unsaved changes exist.

Do not warn unnecessarily for forms with no changes.

---

# 29. Destructive Actions

Destructive operations require deliberate UX.

Examples:

* delete document
* remove connector
* revoke access
* remove user
* delete workflow
* delete metadata definition

Use confirmation where the operation is consequential.

Confirmation dialogs should clearly explain:

* what will happen
* whether physical storage is affected
* whether the operation is reversible
* whether other data is affected

Do not use vague messages such as:

> Are you sure?

---

# 30. Physical vs. Logical Deletion

The UI must distinguish between:

```text
Remove document from Akasha
```

and:

```text
Delete physical file from storage
```

if both operations exist.

The user must understand which operation is being performed.

Never imply that a logical deletion necessarily deletes the customer's physical file.

---

# 31. Permissions UI

The frontend should display actions based on backend-provided authorization information.

Examples:

* download
* upload
* rename
* move
* delete
* edit metadata
* share
* start workflow
* approve

However, hiding an action does not replace backend authorization.

The UI should gracefully handle `403 Forbidden` even if it believed the user had permission.

---

# 32. Permission Changes During Session

Permissions may change while a user is logged in.

The frontend must not assume permissions remain constant indefinitely.

If an operation returns:

```text
403 Forbidden
```

the UI should:

* show an appropriate message
* refresh relevant permission state where useful
* avoid repeatedly retrying unauthorized operations

Do not trap the user in a stale-permission state.

---

# 33. Search

Search UI should support, where available:

* filename
* full text
* metadata
* document type
* date
* status
* Akte
* filters
* sorting
* pagination

Search results must reflect backend authorization.

The frontend must not attempt to "hide" unauthorized results as its security mechanism.

---

# 34. Search Filters

Filters should be represented as structured state.

Avoid building arbitrary query strings throughout components.

Prefer:

```typescript
type DocumentSearchQuery = {
    text?: string;
    akteId?: string;
    documentType?: string;
    status?: string;
    dateFrom?: string;
    dateTo?: string;
};
```

The exact type should follow the backend contract.

---

# 35. Search State

Search state should be shareable through URLs where useful.

For example:

```text
/search?q=vertrag&status=active
```

However, URLs must never contain:

* access tokens
* passwords
* secrets
* sensitive credentials

Sensitive filters should only be encoded in URLs if the product's privacy model explicitly permits it.

---

# 36. Search Results

Search results should clearly communicate relevant states.

For example:

```text
Contract.pdf
Available

OldContract.pdf
Missing
```

Do not present a missing document as though it can definitely be opened.

---

# 37. Pagination

Large collections must be paginated or virtualized where appropriate.

Examples:

* documents
* search results
* users
* audit records
* workflows
* connectors

Do not render thousands of complex rows unnecessarily.

---

# 38. Large Document Lists

Document lists should be optimized for large datasets.

Consider:

* pagination
* virtualization
* incremental loading
* server-side sorting
* server-side filtering

Do not load the entire tenant document library into the browser.

---

# 39. Document Preview

Document previews should use backend-authorized access.

Do not expose permanent public storage URLs.

Preview access must respect:

* tenant
* document permissions
* storage permissions
* expiration/security requirements

---

# 40. Downloads

Downloads must go through an authorized backend flow.

The UI must handle:

* download permission denied
* document missing
* connector unavailable
* expired access
* provider errors

Do not assume a download will succeed simply because the document appears in a list.

---

# 41. Uploads

Upload UI must support appropriate:

* progress
* cancellation
* validation
* size limits
* error reporting
* retry where safe

The frontend must not assume that client-side MIME validation is sufficient.

The backend remains authoritative.

---

# 42. Upload Progress

For large uploads, display meaningful progress.

The UI should distinguish:

```text
Uploading
Processing
Indexing
Available
```

Do not present an uploaded file as fully processed when OCR/indexing is still pending.

---

# 43. Processing State UI

The UI should distinguish physical availability from processing.

For example:

```text
Document:
Available

Processing:
OCR in progress
Indexing:
Pending
```

A processing failure should not necessarily make the document appear deleted.

---

# 44. Background Processing Feedback

Long-running operations should provide useful status where practical.

Examples:

* OCR processing
* AI extraction
* indexing
* workflow execution
* connector synchronization

Avoid indefinite spinners.

Prefer:

* progress
* status
* timestamp
* retry action
* failure reason
* background completion notification

where appropriate.

---

# 45. Notifications

Use notifications for meaningful asynchronous events.

Examples:

* document processing completed
* workflow approval requested
* connector authentication expired
* synchronization failed
* AI operation completed

Do not overwhelm the user with notifications for every internal event.

---

# 46. Real-Time Updates

Realtime mechanisms may be used where useful:

* WebSockets
* Server-Sent Events
* polling
* push notifications

The frontend must not require realtime functionality for correctness.

If realtime updates are unavailable, the UI should still remain correct through refresh/revalidation.

---

# 47. Revalidation

Important screens should have a strategy for refreshing stale data.

Examples:

* document details
* Akte contents
* connector status
* workflows
* permissions
* admin settings

Avoid excessive polling.

Use event-driven invalidation where practical.

---

# 48. State Management

Use the simplest state-management mechanism that fits the problem.

Distinguish between:

### Server state

Examples:

* documents
* users
* Akten
* search results
* workflows
* connectors

### Client/UI state

Examples:

* dialog open
* selected row
* sidebar collapsed
* current tab
* temporary form values

Do not put all server data into one giant global state store.

---

# 49. Server State Caching

If a server-state library is used, configure cache keys carefully.

Tenant context must be part of tenant-specific cache identity where necessary.

For example:

```text
["documents", tenantId, query]
```

is safer than:

```text
["documents", query]
```

when the same browser session may access multiple tenants.

---

# 50. Cache Invalidation

Invalidate or refresh cached data after mutations.

Examples:

```text
Rename document
    ↓
invalidate document
invalidate relevant document lists
invalidate search where necessary
```

Do not leave stale UI state indefinitely after successful mutations.

---

# 51. Tenant Switch

If the application supports switching between tenants:

1. validate the selected tenant through the backend
2. clear or isolate tenant-specific cached state
3. reload tenant-specific configuration
4. reload permissions
5. reload navigation/feature entitlements
6. reload relevant server state

Never reuse tenant A's data while rendering tenant B.

---

# 52. Routing

Routes should reflect product concepts.

Examples:

```text
/documents
/akten
/search
/workflows
/tasks
/admin
/admin/users
/admin/connectors
/admin/metadata
/admin/ai
/admin/licensing
```

Avoid exposing implementation details in routes.

---

# 53. Route Guards

Route guards may improve UX by preventing navigation to unavailable areas.

However:

> Route guards are not security.

The backend must still enforce authorization.

If a user manually navigates to an unauthorized route, the application should gracefully handle the backend response.

---

# 54. Deep Links

Deep links should work correctly after:

* refresh
* login
* application restart

where supported.

A deep link must not bypass authentication or authorization.

For example:

```text
/documents/123
```

must still require the backend to authorize access to document `123`.

---

# 55. Error Pages

Provide appropriate states for:

```text
401 Unauthorized
403 Forbidden
404 Not Found
409 Conflict
422 Validation Error
429 Rate Limited
500 Internal Error
503 Service Unavailable
```

Do not expose backend stack traces.

---

# 56. Permission Denied UX

A `403` should not look like a generic application crash.

The user should understand:

> You do not have permission to perform this action.

Avoid exposing unnecessary authorization details that could reveal protected resources.

---

# 57. Not Found vs. Unauthorized

The UI should respect backend semantics.

A backend may intentionally return `404` instead of `403` to avoid revealing the existence of a protected resource.

Do not infer sensitive information from status-code differences.

---

# 58. Conflict UX

For `409 Conflict`, show a clear message.

Examples:

```text
The document was changed by another user.
```

or:

```text
The connector state changed while you were editing it.
```

Provide a sensible recovery action.

---

# 59. Offline Self-Hosted Mode

The frontend must not assume Internet access.

Offline self-hosted installations must remain usable for functionality that does not inherently require external services.

Do not load mandatory application assets from:

* external CDNs
* Google Fonts
* external JavaScript hosts
* external analytics
* cloud-only APIs

without an explicit reason.

---

# 60. External Services in Offline Mode

Optional services such as:

* OpenAI
* Azure OpenAI
* cloud storage
* external identity providers
* billing services

may naturally require network connectivity.

The UI must distinguish:

```text
Feature unavailable because external service is not configured
```

from:

```text
Akasha application is broken
```

---

# 61. SaaS vs. Self-Hosted

The UI should use configuration/capability information rather than hardcoding deployment assumptions.

Examples:

```text
deploymentMode
enabledFeatures
availableConnectors
authenticationProviders
```

Do not create separate frontend applications for SaaS and self-hosted unless a concrete requirement demands it.

---

# 62. Feature Flags

Feature flags control technical availability.

Examples:

```text
API_ENABLED
AI_ENABLED
MCP_ENABLED
ADVANCED_WORKFLOWS_ENABLED
```

The frontend may use feature flags to hide or disable UI.

However, the backend must independently enforce the feature.

---

# 63. Entitlements

Commercial entitlements determine whether the tenant is allowed to use functionality.

Examples:

```text
AI_ENABLED
MAX_USERS
MAX_STORAGE_CONNECTORS
API_RATE_LIMIT
```

The frontend should consume backend-provided effective entitlements.

Never allow the frontend to modify an entitlement merely by changing local state.

---

# 64. License UI

Self-hosted installations may display:

* license status
* enabled features
* limits
* validity
* customer/license information

Do not expose:

* private signing keys
* sensitive license internals
* cryptographic secrets

The frontend displays the backend's validated license state.

---

# 65. Subscription UI

SaaS subscription screens may display:

* plan
* seats
* enabled features
* usage
* billing status

The frontend must not directly assume that a payment provider is Paddle.

Use backend concepts such as:

```text
Plan
Subscription
Entitlement
SeatLimit
```

---

# 66. Billing Provider Independence

Do not build the frontend around Paddle-specific concepts unless a billing-provider-specific administrative screen explicitly requires them.

Prefer:

```text
Subscription
Plan
Invoice
PaymentStatus
```

rather than:

```text
PaddleSubscription
PaddleCustomer
```

throughout the application.

---

# 67. Admin UI

Administrative areas should be clearly separated from normal user functionality.

Potential sections:

```text
Administration
├── Users
├── Groups
├── Roles
├── Permissions
├── Identity Providers
├── Storage Connectors
├── Metadata
├── Workflows
├── AI
├── MCP
├── API
├── Licensing
└── Subscription
```

The backend remains responsible for administrative authorization.

---

# 68. Connector Configuration UI

Connector configuration forms must be provider-aware only where necessary.

A generic connector configuration model may contain:

```text
Connector Type
Display Name
Status
Capabilities
Configuration
```

Provider-specific fields should be isolated inside the provider configuration UI.

Do not let provider-specific fields contaminate the entire application.

---

# 69. Connector Status

The UI should clearly display connector states such as:

```text
Connected
Unavailable
Authentication required
Disabled
Error
Synchronizing
```

Do not display all failures as simply:

```text
Offline
```

Different states require different user actions.

---

# 70. Synchronization UI

Where relevant, display:

* last successful synchronization
* current synchronization status
* synchronization errors
* pending changes
* authentication problems

A connector outage must not make all documents appear deleted.

---

# 71. External Change Indicators

If useful to the product experience, the UI may indicate:

```text
Changed externally
```

or:

```text
Updated by another user
```

Such indicators should be based on backend state.

Do not attempt to infer external changes solely from browser state.

---

# 72. Metadata UI

Metadata fields should be rendered from backend metadata definitions where the system supports configurable metadata.

The frontend should not hardcode every custom metadata field.

Metadata definitions may include:

* label
* type
* required
* options
* validation
* visibility
* ordering

The backend remains authoritative for validation.

---

# 73. Metadata Field Types

The UI should support appropriate types such as:

* text
* number
* date
* datetime
* boolean
* select
* multi-select
* user
* group
* relation

Do not implement arbitrary type-specific business logic in generic form components.

---

# 74. Form Architecture

Forms should have:

* explicit field state
* validation
* error handling
* loading state
* dirty state
* submit state

Validation should be shared with backend contracts where practical.

Never rely solely on frontend validation.

---

# 75. Validation Messages

Validation messages should be:

* understandable
* actionable
* close to the relevant field
* accessible

Avoid technical messages such as:

```text
HTTP 422
```

for normal user-facing validation.

---

# 76. Localization

The application should be designed for localization even if German is the initial language.

Do not scatter user-visible strings throughout business logic in a way that makes translation difficult.

Use a consistent localization strategy.

Dates, times and numbers must use locale-aware formatting.

---

# 77. German UX

The initial Akasha DMS UI may be German-first.

Use consistent terminology.

For example:

```text
Akte
Dokument
Metadaten
Workflow
Aufgabe
Speicher
Verbindung
Benutzer
Gruppe
Rolle
Berechtigung
```

Do not alternate arbitrarily between German and English terminology.

Technical provider names may remain in their official form.

---

# 78. Accessibility

Accessibility is a core requirement.

Use:

* semantic HTML
* keyboard navigation
* visible focus states
* labels
* accessible dialogs
* accessible forms
* appropriate ARIA only where necessary
* sufficient contrast
* meaningful error messages

Do not make functionality dependent solely on:

* hover
* color
* drag-and-drop
* mouse interaction

---

# 79. Keyboard Navigation

Important application functionality should be usable by keyboard.

Examples:

* document navigation
* search
* filters
* dialogs
* metadata forms
* admin screens
* workflows

Keyboard focus must remain logical after:

* opening dialogs
* closing dialogs
* navigation
* dynamic updates

---

# 80. Responsive Design

The application should work across appropriate:

* desktop
* laptop
* tablet
* smaller screens

The primary DMS experience may be desktop-oriented, but the UI must not break on smaller supported viewports.

Do not simply shrink desktop layouts indefinitely.

---

# 81. Tailwind CSS

Use Tailwind consistently.

Prefer reusable component patterns over arbitrary long utility strings repeated throughout the application.

If the same visual pattern occurs repeatedly, create a reusable component or abstraction.

Do not create a separate CSS framework alongside Tailwind without a concrete reason.

---

# 82. Design System

Akasha should develop a consistent design language.

Shared components may include:

* Button
* Input
* Select
* Checkbox
* Dialog
* Drawer
* Dropdown
* Table
* DataGrid
* Badge
* Alert
* Toast
* Tabs
* Breadcrumbs
* EmptyState
* LoadingState
* ErrorState

Components should have predictable APIs and accessibility behavior.

---

# 83. Visual Consistency

Do not implement each feature with unrelated:

* colors
* spacing
* typography
* button styles
* dialog styles

Use shared design tokens and components.

The application should feel like one coherent product.

---

# 84. Loading States

Every asynchronous UI operation should have an appropriate loading state.

Avoid unnecessary full-page spinners.

Prefer:

* skeletons
* inline loading
* button loading states
* progress indicators

where appropriate.

Do not disable the entire application merely because one API call is running.

---

# 85. Empty States

Empty states should distinguish between:

* no data exists
* search returned no results
* user lacks permission
* connector is unavailable
* data is still loading
* synchronization is pending

For example:

```text
Keine Dokumente gefunden.
```

is different from:

```text
Der Speicher ist derzeit nicht erreichbar.
```

---

# 86. Error States

Error states should provide:

* understandable explanation
* recovery action where possible
* retry where appropriate
* relevant context

Avoid generic:

```text
Something went wrong.
```

when a more useful message is available.

Do not expose sensitive backend details.

---

# 87. Retry UX

Retry actions should only be offered when retrying makes sense.

Good examples:

* temporary network error
* connector unavailable
* transient server failure

Bad examples:

* permission denied
* invalid form data
* invalid credentials that require user correction

---

# 88. Toast Notifications

Toasts are appropriate for short-lived confirmations and non-blocking information.

Do not put critical information exclusively in a toast.

Important errors should remain visible in the relevant UI context.

---

# 89. Dialogs

Dialogs should be used for focused actions.

Do not place entire complex workflows into enormous modal dialogs merely to avoid routing.

Complex workflows should normally have dedicated pages or panels.

Dialogs must:

* trap focus appropriately
* support Escape where appropriate
* restore focus
* have accessible names
* not become unusable on smaller screens

---

# 90. Tables

Tables should support:

* readable columns
* sorting where useful
* filtering where useful
* pagination
* responsive behavior
* keyboard accessibility

Avoid showing every possible metadata field in a default table.

Allow appropriate customization where product requirements support it.

---

# 91. Document List UX

Document lists should make important information easy to scan.

Potential columns:

* name
* document type
* status
* modified
* Akte
* location
* owner

Do not overwhelm the default view.

Advanced metadata may be available through:

* details panel
* configurable columns
* document view

---

# 92. Document Details

The document details view should clearly separate:

```text
Identity
Metadata
Storage
Permissions
Versions
Processing
Relations
Workflow
Audit
```

Only show sections the current user is authorized to access.

---

# 93. Akte UI

Akte should be presented as a first-class business concept.

The UI should not imply that an Akte is simply a physical folder.

An Akte may contain documents from different storage providers.

The UI should represent logical relationships rather than physical folder assumptions.

---

# 94. Document Relations UI

If documents are related, show those relationships explicitly.

Examples:

```text
Related documents
Related Akten
Parent/child document
Workflow
Customer
```

Relationships should use backend logical IDs.

Do not infer relationships merely from filenames unless explicitly implemented as a feature.

---

# 95. Permissions UI

Permission editing should clearly distinguish:

* inherited permissions
* explicit permissions
* user permissions
* group permissions
* role permissions

The UI should make overrides understandable.

Avoid presenting a complex permission model as a confusing list of unrelated checkboxes.

---

# 96. Effective Permissions

Where possible, show the effective result separately from individual assignments.

For example:

```text
Explicit:
READ

Inherited:
WRITE

Effective:
READ + WRITE
```

The backend remains authoritative for the actual calculation.

---

# 97. Permission Changes

Permission changes should provide appropriate confirmation for consequential actions.

After successful changes:

* refresh affected state
* invalidate relevant caches
* update visible actions

Do not assume the current UI permissions remain valid forever.

---

# 98. User and Group Administration

User/group screens should clearly distinguish:

* identity
* membership
* roles
* permissions
* status
* external identity

Do not expose provider credentials.

---

# 99. Identity Provider UI

Identity-provider configuration should clearly distinguish:

```text
Provider Type
Connection
Authentication
Mapping
Status
```

Provider-specific configuration belongs in the relevant provider section.

Do not expose secrets after they are stored.

For secret fields, show masked/managed states rather than existing secret values.

---

# 100. Workflow Builder

The workflow UI should remain understandable.

Avoid building a massive BPMN editor prematurely.

A practical initial UI may support:

```text
Trigger
    ↓
Condition
    ↓
Action
    ↓
Action
    ↓
End
```

The underlying backend should remain extensible.

---

# 101. Workflow Validation

Before publishing a workflow, the UI should validate:

* required fields
* invalid references
* missing actions
* unavailable features
* unavailable connectors
* permission requirements

Backend validation remains authoritative.

---

# 102. Workflow Execution

Workflow execution status should distinguish:

```text
Pending
Running
Waiting
Completed
Failed
Cancelled
```

The UI should make failed workflow instances inspectable where the user's permissions allow it.

---

# 103. AI UI

AI is optional.

The UI must continue to function normally when AI is:

* disabled
* unavailable
* unconfigured
* temporarily failing
* not licensed

Do not make core document management dependent on AI UI components.

---

# 104. AI Chat

AI chat should clearly communicate that AI responses are not necessarily authoritative.

Where useful, show:

* referenced documents
* sources
* confidence/limitations where supported
* tool actions
* operation status

Never imply that the LLM has unrestricted access.

---

# 105. AI Permissions

The frontend may show the AI tools/features available to the current user.

However, the backend must enforce actual authorization.

If an AI operation returns `403`, the UI must handle it gracefully.

Never attempt to work around authorization failures by changing client state.

---

# 106. AI Actions

If AI can modify data, the UI should make the action clear.

Examples:

```text
AI wants to rename document
AI wants to update metadata
AI wants to move document
AI wants to start workflow
```

Where appropriate, require explicit user confirmation before consequential mutations.

---

# 107. AI Prompt Input

Treat user prompts and document content as potentially sensitive.

Do not persist prompts in browser storage unnecessarily.

Do not expose tenant data through client-side debugging or telemetry.

---

# 108. MCP UI

MCP may be largely backend-driven.

If the UI exposes MCP configuration, show:

* enabled/disabled
* clients
* permissions/scopes
* status
* audit information

Do not expose internal MCP credentials or unrestricted tool definitions unnecessarily.

---

# 109. API Administration UI

If API access is configurable, the UI may provide:

* enabled/disabled
* clients
* scopes
* rate limits
* status
* revocation

Do not display secret API credentials after creation.

If a secret can only be shown once, communicate that clearly.

---

# 110. Audit UI

Audit logs should support appropriate:

* filtering
* sorting
* pagination
* timestamps
* actor
* action
* resource

Audit data may contain sensitive information.

Only authorized users may access it.

---

# 111. Date and Time Handling

Backend timestamps should be treated consistently.

Prefer storing/transmitting absolute timestamps and formatting them for the user's locale.

Do not manually manipulate timezone offsets throughout components.

Be careful around:

* daylight saving time
* deadlines
* workflow schedules
* audit timestamps
* modified dates

---

# 112. File Sizes

Format file sizes consistently.

Do not assume all files are small.

Use appropriate units and avoid integer overflow or precision problems for very large files.

---

# 113. Browser Storage

Use browser storage carefully.

Do not store:

* passwords
* long-lived sensitive tokens
* connector secrets
* AI API keys
* administrative secrets

in `localStorage` merely for convenience.

Only persist non-sensitive UI preferences where appropriate.

---

# 114. Sensitive UI State

Sensitive information should not remain visible unnecessarily.

Examples:

* connector secrets
* AI keys
* API secrets
* temporary authentication information

Clear sensitive form state when no longer required.

---

# 115. Clipboard

Be cautious when copying sensitive information to the clipboard.

If a secret is displayed once for copying, clearly communicate the security implications.

Do not automatically copy credentials without explicit user action.

---

# 116. Browser URLs

Never put secrets into URLs.

Do not put:

* API keys
* access tokens
* passwords
* connector credentials

into query strings or route parameters.

---

# 117. Frontend Logging

Frontend logs must not contain:

* authentication tokens
* passwords
* API keys
* customer document contents
* sensitive OCR text
* secret connector configuration

Production logging should be intentionally limited.

---

# 118. Error Reporting

If an error-reporting service is used, carefully control the data sent to it.

Do not automatically transmit:

* document contents
* customer metadata
* access tokens
* authentication headers
* secrets

Third-party telemetry must be optional/configurable where required for self-hosted and privacy-sensitive deployments.

---

# 119. External CDN Dependencies

Core frontend functionality must not depend on third-party CDNs.

This is especially important for:

* self-hosted deployments
* offline deployments
* enterprise environments

Build required assets into the application where practical.

---

# 120. Analytics

Analytics must never be required for core functionality.

If analytics are introduced:

* make the privacy model explicit
* avoid sending document contents
* avoid sending sensitive metadata
* respect deployment requirements
* support self-hosted/offline operation without analytics

---

# 121. Security Headers and Browser Security

The frontend deployment should use appropriate browser security mechanisms where applicable.

Examples include:

* Content Security Policy
* secure cookies
* appropriate CORS configuration
* frame restrictions
* MIME handling
* referrer policy

These are primarily deployment/backend concerns but must be considered by frontend architecture.

---

# 122. XSS Prevention

Never render untrusted HTML directly.

Be particularly careful with:

* document metadata
* filenames
* OCR text
* AI output
* user-generated content
* external provider content

Avoid unsafe HTML rendering unless absolutely necessary and properly sanitized.

---

# 123. AI Output Rendering

AI output is untrusted content.

If Markdown or HTML rendering is supported:

* sanitize appropriately
* do not allow arbitrary scripts
* do not allow arbitrary embedded resources without review

Never treat AI-generated HTML as trusted application markup.

---

# 124. Document Metadata Rendering

Filenames and metadata originate from external/customer-controlled sources.

Render them safely.

Do not assume:

```text
filename.pdf
```

is harmless.

A malicious filename must not result in executable HTML or JavaScript.

---

# 125. Drag and Drop

Drag-and-drop may be used for:

* uploads
* moving documents
* organizing Akten

But all drag-and-drop operations must have accessible alternatives.

Do not make critical functionality mouse-only.

---

# 126. Clipboard and File Inputs

User-selected files must be treated as untrusted.

The frontend may provide client-side validation for UX, but the backend must perform authoritative validation.

---

# 127. Frontend Feature Boundaries

A feature should own:

* components
* hooks
* API functions
* types
* state
* tests

where practical.

Avoid allowing every feature to import internal implementation details from every other feature.

---

# 128. Shared Components

Shared components should be domain-neutral.

Good:

```text
Button
Dialog
DataTable
FormField
DatePicker
EmptyState
```

Avoid:

```text
DocumentButton
WorkflowButton
SharePointDialog
CustomerSpecificThing
```

in the generic component library unless the component is genuinely reusable at that domain level.

---

# 129. Shared Hooks

Shared hooks should have clearly defined responsibilities.

Avoid giant hooks such as:

```text
useEverything()
useAppData()
useGlobalState()
```

Prefer focused hooks:

```text
useDocuments()
useDocument(id)
useDocumentPermissions(id)
useSearchDocuments()
useCurrentUser()
```

---

# 130. API Error Normalization

The API layer should normalize common backend errors into predictable frontend structures.

For example:

```typescript
type ApiError = {
    status: number;
    code?: string;
    message: string;
    fieldErrors?: Record<string, string[]>;
};
```

The exact structure should follow the backend contract.

Components should not need to understand raw provider exceptions.

---

# 131. Abort and Cancellation

Long-running requests should support cancellation where appropriate.

Examples:

* search
* document preview
* upload
* AI request
* large list loading

If a user navigates away, obsolete requests should not unnecessarily continue.

---

# 132. Race Conditions

The frontend must account for overlapping requests.

Example:

```text
Search: "contract"
Search: "contract 2026"
```

If the first response arrives after the second, it must not overwrite the newer result.

Use request identity, cancellation or server-state tooling appropriately.

---

# 133. Mutation Race Conditions

Similarly:

```text
Rename document
Delete document
```

performed close together must not leave the UI in an impossible state.

After important mutations, revalidate authoritative state.

---

# 134. Optimistic List Updates

If an item is optimistically removed or renamed in a list, failure must restore the previous state.

For destructive operations, prefer confirmed updates unless the UX strongly benefits from optimistic behavior.

---

# 135. Offline Browser Behavior

Offline self-hosted refers to the deployment not requiring Internet access.

It does not automatically mean the browser application must support arbitrary offline editing.

Do not implement offline-first synchronization for the browser unless it is explicitly required.

However, the UI must gracefully handle temporary network loss.

---

# 136. Network Loss

If the backend becomes temporarily unavailable:

* preserve unsaved form data where practical
* show connection status where useful
* allow retry
* avoid falsely displaying successful mutations
* avoid destructive automatic retries

---

# 137. Retry Safety

Automatically retry only operations known to be safe or idempotent.

Do not blindly retry:

* delete
* move
* rename
* workflow actions
* external side effects

unless the backend/API contract explicitly supports idempotency.

---

# 138. Idempotency Keys

For important client-triggered mutations, the frontend may use backend-supported idempotency keys where appropriate.

This is especially useful for:

* uploads
* workflow starts
* external operations
* payment-related operations

Do not invent frontend-only idempotency semantics.

---

# 139. Accessibility During Async Operations

When actions become asynchronous:

* announce meaningful state changes where appropriate
* keep focus logical
* expose errors accessibly
* avoid trapping keyboard users in indefinite loading states

---

# 140. Testing

Frontend tests must cover:

### Components

* rendering
* interaction
* accessibility
* loading
* empty states
* errors

### Features

* API interactions
* permissions
* mutations
* external change states
* concurrency conflicts

### Routing

* protected routes
* deep links
* unauthorized states

### Tenant handling

* tenant switch
* tenant-specific cache invalidation
* no cross-tenant UI state leakage

---

# 141. Security Tests

Frontend security tests should verify UX behavior, but must not pretend frontend tests prove backend security.

Test cases may include:

* `401`
* `403`
* `404`
* expired session
* missing permissions
* disabled feature
* unavailable connector

The actual security guarantees must be tested in backend/integration tests.

---

# 142. API Mocking

API mocks may be used for unit/component tests.

Mocks should resemble the real backend contract.

Do not create mock behavior that the backend could never actually produce.

Important scenarios should include:

```text
200
400
401
403
404
409
422
429
500
503
```

---

# 143. End-to-End Testing

Important end-to-end flows should be tested.

Examples:

```text
Login
→ Open Akte
→ View document
→ Edit metadata
→ Save
→ Search
→ Download
```

and:

```text
External document change
→ backend synchronization
→ frontend refresh
→ updated document state
```

where the test environment supports it.

---

# 144. Test Tenant Isolation UX

End-to-end tests should verify that switching or changing tenant context does not expose cached UI data from another tenant.

The backend remains responsible for actual isolation.

---

# 145. Visual Regression

Visual regression tests may be used for critical shared components and pages.

Focus on:

* design-system components
* navigation
* document list
* document details
* forms
* admin pages

Do not make visual snapshots the only form of frontend testing.

---

# 146. TypeScript

Use TypeScript strictly.

Avoid `any` unless there is a documented and justified reason.

Prefer explicit types for:

* API responses
* API requests
* domain-facing frontend models
* component props
* state
* event payloads

---

# 147. Type Safety

Do not use type assertions to hide actual type problems.

Avoid:

```typescript
value as SomeType
```

when the underlying data has not been validated.

Prefer runtime validation where external data crosses the application boundary.

---

# 148. Runtime Validation

API responses originate outside the frontend process.

Where appropriate, validate critical external data at runtime.

This is particularly useful for:

* configuration
* dynamic metadata
* feature/entitlement information
* external integration responses

TypeScript types alone do not validate runtime data.

---

# 149. No Business Logic in Types

Types describe data.

Do not encode complex business rules into giant TypeScript type systems merely because they are technically possible.

Business rules belong in backend/domain logic.

The frontend may provide UX validation and representation.

---

# 150. Date Types

Do not represent all dates as arbitrary strings throughout the application.

Define clear conventions for:

* UTC timestamps
* local dates
* local date-times

Be especially careful with:

* workflow deadlines
* document dates
* audit timestamps
* subscription periods

---

# 151. URL State

Use URL state for information that should survive navigation or be shareable.

Examples:

* search query
* filters
* pagination
* selected tab where appropriate

Do not put large application state into URLs.

---

# 152. Browser Back/Forward

Navigation should respect normal browser behavior.

Do not create custom navigation systems that make:

* back
* forward
* refresh

unreliable.

Forms with unsaved changes should be handled deliberately.

---

# 153. Accessibility of Dynamic Lists

Dynamic document/search lists must provide meaningful keyboard and screen-reader behavior.

When items are added/removed/updated, focus and announcements should remain understandable.

---

# 154. Performance

Frontend performance should prioritize:

* fast initial render
* efficient document lists
* lazy loading
* code splitting
* efficient state updates
* minimized unnecessary API requests

Do not sacrifice:

* authorization
* correctness
* tenant isolation

for performance.

---

# 155. Code Splitting

Large areas such as:

* administration
* workflow builder
* AI
* advanced search

may be lazy-loaded.

Core navigation and document functionality should remain responsive.

---

# 156. Bundle Dependencies

Do not add large dependencies for trivial functionality.

Before adding a dependency, consider:

* bundle size
* maintenance
* security
* license
* offline compatibility
* actual necessity

Prefer existing project capabilities when sufficient.

---

# 157. Dependency Security

Keep frontend dependencies maintained.

Avoid abandoned or unnecessary packages.

Pay particular attention to dependencies that process:

* HTML
* Markdown
* files
* URLs
* authentication
* cryptographic material

---

# 158. Environment Configuration

Frontend configuration must not contain secrets.

Public frontend configuration may include things such as:

* backend URL
* deployment mode
* public feature configuration
* build information

Never put:

* database passwords
* AI API keys
* storage credentials
* Paddle secret keys
* signing keys

into frontend environment variables that become part of the browser bundle.

---

# 159. Build-Time Configuration

Do not assume that a value is secret merely because it is stored in an environment variable.

Anything embedded into the frontend bundle is effectively public to the user.

---

# 160. Frontend Deployment

The frontend should be deployable as static assets or through the supported application hosting architecture.

Self-hosted deployments must not require an external frontend service.

Offline deployments must contain all required application assets.

---

# 161. Version Display

The UI may display application/version information where useful.

Version information should come from build/application configuration rather than being manually duplicated across components.

---

# 162. Compatibility

The frontend must remain compatible with supported backend versions according to the project's deployment strategy.

If frontend and backend are versioned together, incompatible versions should fail clearly rather than silently behaving incorrectly.

---

# 163. Migration UX

When backend migrations or upgrades affect the UI, display meaningful maintenance/error states.

Do not expose database migration details to normal users.

---

# 164. Feature Availability

When a feature is unavailable because of:

* license
* entitlement
* configuration
* connector capability
* deployment mode

the UI should explain the reason appropriately.

Do not simply render a broken empty screen.

---

# 165. Capability-Based UI

Where functionality depends on connector capabilities, the UI should use backend-provided capabilities.

Example:

```text
supportsMove
supportsRename
supportsVersioning
supportsRecycleBin
```

Do not hardcode:

```text
if SharePoint then show Move
```

unless the capability truly cannot be expressed generically.

---

# 166. Provider-Specific Configuration

Provider-specific UI may exist for:

* SharePoint
* OneDrive
* Dropbox
* SMB
* S3

but should remain isolated.

The generic connector UI should not become a collection of provider-specific assumptions.

---

# 167. Storage Location UI

Where a document's physical storage location is displayed, make clear that it is the current known storage reference.

Do not imply that the path is the permanent identity of the document.

---

# 168. Sync Timestamps

When displaying synchronization information, distinguish:

```text
Document modified:
10:42

Akasha last synchronized:
10:45
```

from:

```text
Document last modified:
10:45
```

Do not confuse synchronization time with source-document modification time.

---

# 169. Missing Documents

A missing document should retain enough UI context to remain useful.

Display, where authorized:

* document name
* Akte
* last known location
* last known modification
* availability state

Do not expose storage details the user is not authorized to see.

---

# 170. Connector Errors

Connector errors should be presented at the appropriate level.

A normal user may see:

> Der Speicher ist momentan nicht erreichbar.

An administrator may see more detail:

> Authentifizierung für den SharePoint-Connector ist erforderlich.

Do not expose raw stack traces.

---

# 171. Admin Diagnostics

Administrative users may receive additional diagnostics where authorized.

Diagnostic information must still avoid:

* credentials
* tokens
* secrets
* sensitive document contents

---

# 172. Final Frontend Rules

The following rules are mandatory:

1. Never treat the frontend as a security boundary.
2. Never trust frontend tenant IDs, permissions or feature flags for authorization.
3. Never access customer storage directly when the backend abstraction should be used.
4. Never use filename or path as logical document identity.
5. Never assume a document cannot change outside Akasha.
6. Never silently overwrite newer backend state.
7. Never hide a backend authorization failure as though the operation succeeded.
8. Never store secrets in the frontend bundle.
9. Never put credentials or tokens into URLs.
10. Never log document contents or secrets.
11. Never make AI mandatory for core DMS functionality.
12. Never make Internet access mandatory for offline self-hosted operation.
13. Never expose unrestricted SQL, shell or HTTP functionality through the frontend.
14. Never assume cached permissions remain permanently valid.
15. Never allow tenant-specific cached data to leak into another tenant's UI.
16. Never use provider-specific storage logic throughout the core UI.
17. Always use logical `DocumentId` for document identity.
18. Always distinguish logical document existence from physical storage availability.
19. Always handle external rename, move, modification, deletion and restoration.
20. Always handle concurrency conflicts explicitly.
21. Always treat AI output and document content as untrusted input.
22. Always enforce accessibility for important interactions.
23. Always provide meaningful loading, empty, error and unavailable states.
24. Always use backend-provided capabilities and entitlements where functionality varies.
25. Always keep SaaS and self-hosted behavior within the same fundamental frontend architecture.
26. Always ensure offline self-hosted deployments do not depend on external CDNs or mandatory cloud services.
27. Always keep business logic out of purely presentational components.
28. Always centralize backend communication through the API layer.
29. Always invalidate or isolate tenant-specific client state when tenant context changes.
30. Always design the UI so that the backend remains the final authority for data, permissions and business rules.

