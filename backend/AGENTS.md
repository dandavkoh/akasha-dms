# AGENTS.md

## Scope

This file defines the backend-specific architecture and engineering rules for **Akasha DMS**.

These rules supplement the repository-level `AGENTS.md`.

If a rule in this file conflicts with a higher-level repository or system requirement, the higher-level requirement takes precedence.

The backend must implement the product principles defined in the root `AGENTS.md`, especially:

* strict tenant isolation
* server-side authorization
* customer-owned storage
* storage-provider abstraction
* logical document identity
* external storage changes
* SaaS and self-hosted support
* offline-capable self-hosted operation
* modular architecture
* PostgreSQL
* optional AI
* auditable operations

---

# 1. Backend Technology

The backend is implemented using:

* C#
* .NET
* ASP.NET Core
* Controllers
* REST API
* OpenAPI
* PostgreSQL
* Entity Framework Core where appropriate

The backend should be Docker-friendly.

The backend must support:

* SaaS
* self-hosted
* offline self-hosted installations

The backend must use the same fundamental application architecture for all deployment models.

Do not create separate SaaS and self-hosted codebases.

---

# 2. Architecture Style

Akasha should initially be implemented as a **modular monolith**.

Do not introduce microservices prematurely.

The backend should have clear architectural boundaries.

Recommended structure:

```text
Akasha.Api
Akasha.Application
Akasha.Domain
Akasha.Infrastructure
```

Possible functional modules include:

```text
Tenancy
Identity
Authorization
Documents
Akten
Metadata
Storage
Search
Processing
Workflows
AI
Licensing
Billing
Audit
Integrations
```

Modules should have explicit responsibilities and dependencies.

Avoid creating one giant shared service layer containing unrelated business logic.

---

# 3. Dependency Direction

Dependencies must point inward.

Conceptually:

```text
Akasha.Api
    ↓
Akasha.Application
    ↓
Akasha.Domain

Akasha.Infrastructure
    ↓
implements abstractions defined by inner layers
```

The domain must not depend on infrastructure.

The domain must not depend directly on:

* ASP.NET Core
* Entity Framework Core
* PostgreSQL
* Microsoft Graph
* SharePoint SDKs
* Dropbox SDKs
* OpenAI SDKs
* Paddle SDKs
* LDAP libraries
* specific identity providers
* storage-provider SDKs

Infrastructure may depend on external technologies and providers.

Application code may orchestrate domain logic through abstractions.

Controllers must remain thin.

---

# 4. Domain Layer

The domain contains business concepts and rules.

The domain should contain concepts such as:

* Tenant
* User
* Group
* Role
* Permission
* Document
* Akte
* MetadataDefinition
* MetadataValue
* StorageReference
* Workflow
* WorkflowInstance
* Subscription
* Entitlement
* License
* AuditEntry

The domain must remain independent of:

* HTTP
* database implementation
* external APIs
* provider SDKs
* serialization concerns
* UI concerns

Do not place infrastructure concerns directly into domain entities.

---

# 5. Application Layer

The application layer coordinates use cases.

Examples:

* create document
* retrieve document
* move document
* rename document
* delete document
* create Akte
* assign document to Akte
* update metadata
* search documents
* synchronize storage
* process external storage changes
* execute workflow
* perform AI operation
* validate license
* manage connectors

Application services/use cases should:

* establish the correct tenant context
* invoke authorization
* coordinate domain logic
* invoke external abstractions
* manage transactions where appropriate
* publish or schedule background work
* produce appropriate audit events

Application services must not contain provider-specific implementation details.

---

# 6. Infrastructure Layer

Infrastructure implements external concerns.

Examples:

* PostgreSQL persistence
* Entity Framework Core
* storage connectors
* identity providers
* AI providers
* billing providers
* search implementation
* background-job infrastructure
* email
* external APIs
* cryptographic license validation

Infrastructure-specific implementation must not leak into the domain.

Provider-specific models should be mapped into application/domain representations.

---

# 7. Tenant Context

Tenant context is security-critical.

Every request or operation that accesses tenant-scoped data must have a validated tenant context.

Tenant context must be derived from authenticated identity and server-side authorization.

Never trust a client-provided `tenantId` as the sole source of tenant authorization.

Do not allow application services to arbitrarily switch tenant context.

Background jobs must explicitly carry their tenant context.

Conceptually:

```text
Authenticated Identity
        ↓
Tenant Resolution
        ↓
Authorization
        ↓
Application Use Case
        ↓
Tenant-Scoped Data
```

---

# 8. Tenant Isolation

Tenant isolation must be enforced throughout the backend.

Every tenant-scoped entity must have an appropriate tenant relationship.

Typical tenant-scoped entities include:

* documents
* Akten
* metadata
* users
* groups
* permissions
* workflows
* connectors
* search data
* AI data
* audit data
* subscriptions
* licenses
* integrations

Cross-tenant access must fail securely.

Do not rely solely on developers remembering to add `Where(x => x.TenantId == tenantId)` to every query.

Use architectural mechanisms that make accidental cross-tenant access difficult.

PostgreSQL Row-Level Security may be used as defense in depth.

---

# 9. Authorization

Authorization must be performed server-side.

The backend must support:

* users
* groups
* roles
* individual permissions
* inherited permissions
* explicit overrides
* service accounts
* API clients

Resources may include:

* tenant
* document
* folder
* Akte
* workflow
* metadata
* connector
* administrative configuration

Actions may include:

```text
READ
WRITE
DELETE
DOWNLOAD
UPLOAD
MOVE
COPY
RENAME
SHARE
EDIT_METADATA
START_WORKFLOW
APPROVE
ADMIN
```

Authorization must be applied consistently to:

* controllers
* application services
* background jobs
* workflows
* search
* AI
* MCP
* API clients
* storage operations

Frontend authorization is never a security boundary.

---

# 10. Authorization Before Data Access

Do not retrieve sensitive document content first and authorize afterward if the architecture can avoid doing so.

Authorization should be established before exposing:

* document metadata
* document content
* search results
* extracted text
* OCR text
* embeddings
* AI-derived information

Search and indexing must not become authorization bypasses.

---

# 11. Document Identity

A logical Akasha document must have a stable internal identity.

The primary logical identity is:

```text
DocumentId
```

It must not be derived solely from:

* filename
* path
* URL
* storage location
* provider-specific path

A document may change its physical location while retaining the same `DocumentId`.

The Akte relationship must reference the logical document identity.

---

# 12. Storage Reference

A document must maintain a distinction between logical identity and physical storage identity.

Conceptually:

```text
Document
│
├── DocumentId
├── TenantId
├── Name
├── Metadata
│
└── StorageReference
      ├── ConnectorId
      ├── ExternalId
      ├── Path
      ├── Version
      ├── ETag
      └── LastKnownModifiedAt
```

Not every provider supports every property.

The model must therefore support optional provider capabilities without making a provider-specific field mandatory for all connectors.

The storage reference identifies where and how the physical file is currently represented in external storage.

---

# 13. Logical Document vs. Storage Object

Do not equate:

```text
Akasha Document
```

with:

```text
Storage File
```

A logical document can survive:

* rename
* move
* temporary disappearance
* restoration
* external modification

The physical storage object may change its path or metadata while the logical document remains the same.

This distinction must be reflected in persistence and application logic.

---

# 14. Storage Connector Abstraction

All physical document operations must go through a storage connector abstraction.

Conceptually:

```csharp
public interface IStorageConnector
{
    ...
}
```

The exact interface should evolve with actual requirements.

Potential operations include:

```text
GetDocument
ListDocuments
CreateDocument
UpdateDocument
DeleteDocument
MoveDocument
CopyDocument
RenameDocument
DownloadDocument
UploadDocument
GetVersions
GetMetadata
UpdateMetadata
GetPermissions
GetChanges
WatchChanges
```

Not every connector must support every operation.

Capabilities must be explicit.

---

# 15. Connector Capabilities

Connectors should expose capabilities rather than forcing core logic to inspect provider names.

Potential capabilities include:

```text
Read
Write
Delete
Move
Copy
Rename
Versioning
Metadata
Permissions
ChangeTracking
WatchChanges
Locking
RecycleBin
NativeSearch
```

Application logic should ask:

```text
Does this connector support Move?
```

rather than:

```text
Is this SharePoint?
```

Never implement provider-specific conditionals throughout business logic.

---

# 16. Provider-Specific Logic

Provider-specific behavior belongs inside the connector implementation.

Examples:

```text
SharePointStorageConnector
OneDriveStorageConnector
DropboxStorageConnector
SmbStorageConnector
LocalFileSystemStorageConnector
S3StorageConnector
```

These connectors may use provider-specific SDKs, APIs and protocols.

The rest of the application must depend on the normalized connector abstraction.

Do not leak:

* Microsoft Graph types
* Dropbox types
* SMB-specific implementation objects
* provider-specific exceptions
* provider-specific identifiers

through the domain layer.

---

# 17. Storage Permissions

Akasha authorization must never grant more access than the underlying storage system permits.

Conceptually:

```text
Effective Permission
=
Akasha Permission
∩
Storage Permission
```

If the storage provider denies access, the backend must not bypass that restriction.

Storage permission checks belong inside the appropriate connector/application boundary.

---

# 18. External Storage Changes

External changes are a first-class backend concern.

A storage user or external application may:

* create a file
* modify a file
* rename a file
* move a file
* delete a file
* restore a file

without going through Akasha.

The backend must not assume that the physical storage state remains unchanged.

The synchronization architecture must detect and reconcile these changes.

---

# 19. Change Detection

The storage connector should expose normalized change detection where supported.

Conceptually:

```csharp
GetChangesAsync(...)
```

Normalized change types may include:

```text
Created
Modified
Renamed
Moved
Deleted
Restored
```

Provider-specific mechanisms remain inside the connector.

Examples of mechanisms that may be used by connectors:

* change tokens
* delta APIs
* provider revisions
* ETags
* webhooks
* change notifications
* stable external IDs
* metadata comparison

The core application must consume normalized change information.

---

# 20. Change Detection Strategy

Prefer provider-native incremental change tracking.

The backend should avoid repeatedly scanning entire storage systems when an incremental mechanism is available.

The connector should maintain whatever provider-specific cursor, token or checkpoint is necessary.

Synchronization must be:

* incremental where possible
* retryable
* idempotent
* tenant-aware
* observable

Lost or missed change notifications must be recoverable through reconciliation.

---

# 21. Synchronization vs. Reconciliation

The backend must distinguish conceptually between:

### Discovery

What currently exists in the storage system?

### Synchronization

What has changed since the last known state?

### Reconciliation

How can Akasha's known state and the actual storage state be brought back into consistency?

These are related but distinct concerns.

A connector outage or missed change event must not permanently corrupt the logical DMS state.

---

# 22. External Rename

When a file is renamed externally:

```text
contract.pdf
    ↓
vertrag.pdf
```

the backend must:

1. identify the existing logical document
2. retain its `DocumentId`
3. update the storage reference
4. update the current document name where appropriate
5. retain the Akte relationship
6. record the change for audit/history where required
7. trigger dependent processing if necessary

The backend must not create a new logical document merely because the filename changed.

---

# 23. External Move

When a document is moved externally:

```text
/folder-a/contract.pdf
        ↓
/folder-b/contract.pdf
```

the backend must:

1. preserve the logical `DocumentId`
2. update the storage reference
3. preserve Akte relationships
4. update the current path/location
5. record the change where required

A path is not a document identity.

---

# 24. External Modification

When the physical document changes externally, the backend must detect the modification where the connector supports change detection.

The backend may then schedule processing such as:

* metadata refresh
* text extraction
* OCR
* classification
* full-text indexing
* semantic indexing
* embedding regeneration
* AI metadata extraction
* workflow triggers

Processing should normally occur asynchronously.

Do not perform expensive OCR, embedding or AI work synchronously inside a normal HTTP request unless there is a concrete reason.

---

# 25. External Deletion

When a physical document is deleted externally, the backend must not automatically delete the logical DMS document.

The logical document should transition into an appropriate state such as:

```text
Missing
Unavailable
```

The exact state model belongs to the Documents module.

The backend should retain, where appropriate:

* `DocumentId`
* Akte relationships
* metadata
* audit history
* last known filename
* last known path
* external storage identity
* last known version
* last known modification timestamp

The logical document should remain identifiable.

---

# 26. External Restoration

If a previously missing document becomes available again, the backend should attempt to associate it with the existing logical document.

Stable provider-specific external IDs should be preferred where available.

If reliable identity cannot be established, the system must not silently associate an unrelated file with the existing document.

Ambiguous restoration scenarios should result in a safe reconciliation state requiring explicit handling.

---

# 27. Document Replacement

A physical file may be replaced by another file at the same path.

The backend must not assume that:

```text
same path = same document
```

Where supported, use:

* external ID
* version ID
* ETag
* provider revision
* content identity
* modification metadata

to determine whether the underlying object is still the same logical storage object.

Replacement must be handled explicitly rather than silently assumed to be a normal modification.

---

# 28. External Concurrency

A document may be modified simultaneously by:

* an Akasha user
* an external storage user
* an API client
* a workflow
* an AI operation
* a synchronization process
* another application

The backend must not silently overwrite newer changes.

Where supported, use optimistic concurrency based on:

* ETag
* version ID
* revision ID
* provider-specific version token

Example:

```text
Akasha knows: version 5

Storage currently:
version 6

Akasha attempts modification
        ↓
Concurrency conflict
```

The backend should detect the conflict and handle it explicitly.

---

# 29. Akasha-Initiated Storage Operations

When Akasha itself performs a physical storage operation, the storage operation must succeed before the DMS commits the corresponding logical state change whenever practical.

For example, for a move:

```text
1. Authorize operation
2. Call connector
3. Storage confirms successful move
4. Update Akasha storage reference
5. Commit transaction
6. Audit operation
```

Do not update the database first and assume the storage operation will succeed.

Where true distributed transactions are impossible, use explicit state transitions and reconciliation.

---

# 30. Failed Storage Operations

Storage operations can fail.

Examples:

* permission denied
* file not found
* connector unavailable
* authentication expired
* network timeout
* destination unavailable
* provider conflict

The backend must distinguish between:

* temporary failure
* permanent failure
* authorization failure
* missing resource
* concurrency conflict
* configuration failure

A failed storage operation must not leave the DMS silently claiming that the operation succeeded.

---

# 31. Storage Synchronization State

The backend should maintain sufficient synchronization state to understand the relationship between Akasha and the underlying storage.

Potential states include:

```text
Available
Pending
Missing
Unavailable
SyncError
AuthenticationRequired
```

The exact state machine belongs to the Documents/Storage modules.

State transitions must be explicit and auditable where appropriate.

---

# 32. Search Index Consistency

External document changes must eventually propagate to search indexes.

For example:

```text
External modification
        ↓
Change detection
        ↓
Document state update
        ↓
Processing job
        ↓
Text/OCR extraction
        ↓
Search index update
```

Search indexes must not expose content that violates current permissions.

When a document becomes unavailable or deleted, derived search content must be handled according to the product's retention and search policies.

---

# 33. Background Jobs

Use asynchronous background processing for operations such as:

* storage synchronization
* external change processing
* OCR
* text extraction
* indexing
* embedding generation
* AI processing
* workflow execution
* scheduled tasks
* notifications

Every tenant-scoped background job must carry sufficient context to establish:

* tenant ID
* operation ID
* correlation ID
* initiating identity where applicable

Never run tenant-sensitive jobs without explicit tenant context.

---

# 34. Job Idempotency

Background jobs must be idempotent where possible.

The same external storage change may be delivered more than once.

For example:

```text
Modified event
Modified event
Modified event
```

must not result in corrupted state.

Use stable operation/change identifiers where the provider supplies them.

Where the provider does not supply stable identifiers, use appropriate deduplication and state checks.

---

# 35. Retry Strategy

Transient failures should be retryable.

Retries must use appropriate backoff.

Do not retry permanent failures indefinitely.

Examples of potentially retryable failures:

* network timeout
* temporary provider outage
* rate limit
* temporary service unavailable

Examples of potentially permanent failures:

* invalid credentials
* missing configuration
* unsupported operation
* permanent authorization denial

The system should expose persistent failures for administrative resolution.

---

# 36. API Controllers

Controllers must remain thin.

Controllers should primarily handle:

* HTTP input
* model binding
* authentication context
* request validation
* invoking application use cases
* mapping results to HTTP responses

Do not place business logic inside controllers.

Avoid:

```text
Controller
    ↓
direct EF queries
    ↓
business logic
    ↓
provider SDK
```

Prefer:

```text
Controller
    ↓
Application Use Case
    ↓
Domain / Abstractions
    ↓
Infrastructure
```

---

# 37. REST API

The REST API must be:

* consistent
* versionable
* documented
* authorization-aware
* tenant-aware
* auditable

Use OpenAPI as the API contract.

Avoid exposing internal database entities directly as API contracts.

Use dedicated request/response models where appropriate.

---

# 38. API Authorization

Every API endpoint must have a clearly defined authorization requirement.

Do not assume that because an endpoint is not visible in the frontend it is secure.

API endpoints must enforce:

* authentication
* tenant context
* permissions
* entitlements
* resource-level authorization where required

Administrative endpoints require elevated authorization.

---

# 39. Identity Provider Abstraction

Identity providers must be abstracted.

Potential providers include:

* internal identity
* Entra ID
* Active Directory
* LDAP
* OpenLDAP
* OIDC
* SAML

Conceptually:

```text
IIdentityProvider
```

The application must not depend on a specific provider's user object.

External identities should be mapped into Akasha's internal identity model.

---

# 40. Identity and Authorization Separation

Identity answers:

```text
Who is the user?
```

Authorization answers:

```text
What may the user do?
```

Do not embed authorization rules inside provider-specific authentication code.

A user authenticated through Entra ID must use the same Akasha authorization model as an internal user where the product configuration allows it.

---

# 41. Metadata

Metadata should be modeled explicitly.

Core metadata should use relational structures where appropriate.

Examples:

* document type
* status
* date
* customer
* contract number
* custom fields

Do not store the entire metadata model as an opaque JSON blob.

JSON may be used for genuinely dynamic or provider-specific information, but core business data should remain queryable and relational.

---

# 42. Akten and Relationships

An Akte is a logical aggregate/business concept.

Document-to-Akte relationships must use logical document identity.

Do not store relationships using only:

* path
* filename
* provider URL

Relationships must survive physical rename and move operations.

An Akte may contain documents from multiple storage connectors.

---

# 43. Document Relations

Use PostgreSQL relational structures for document relationships initially.

Potential relationships include:

```text
Document → Document
Document → Akte
Document → Customer
Document → Workflow
```

Do not introduce a graph database merely because relationships exist.

Neo4j or another graph database must only be considered if demonstrated requirements justify it.

---

# 44. Search Architecture

Search must be implemented behind an appropriate abstraction.

Potential capabilities include:

* full-text search
* metadata filtering
* permission filtering
* faceting
* semantic search
* vector search

The search layer must always receive sufficient tenant and authorization context.

A search query must never return a resource merely because the search index contains it.

Authorization remains authoritative.

---

# 45. Vector and Semantic Search

Vector search is optional.

Do not make a vector database mandatory for the basic DMS.

PostgreSQL with `pgvector` may be considered as an initial implementation where appropriate.

External/customer-hosted vector infrastructure may be supported later.

Vector data must respect:

* tenant isolation
* document permissions
* deletion/retention rules
* document updates

When a source document changes, derived embeddings may become stale and should be regenerated as appropriate.

---

# 46. Document Processing

Processing should be modular and asynchronous.

Potential processing stages include:

```text
Document
    ↓
Text Extraction
    ↓
OCR
    ↓
Metadata Extraction
    ↓
Classification
    ↓
Full-Text Index
    ↓
Embedding
    ↓
AI Processing
```

A processing failure must not necessarily make the physical document unavailable.

The system should distinguish document availability from processing status.

---

# 47. AI Provider Abstraction

AI must be accessed through an abstraction.

Conceptually:

```text
IAiProvider
```

Potential implementations:

* OpenAI
* Azure OpenAI
* OpenAI-compatible APIs
* customer-hosted LLM
* local model
* future providers

Provider-specific SDKs must remain in Infrastructure.

AI credentials must never be exposed to clients unnecessarily.

---

# 48. AI Authorization

AI operations must execute within the authorization context of the requesting user or explicitly authorized service identity.

An AI assistant must never:

* search another tenant
* retrieve unauthorized documents
* bypass document permissions
* bypass storage permissions
* perform unauthorized mutations

AI authorization must be enforced by backend services, not by prompts.

---

# 49. MCP Backend Integration

MCP tools must call the same application services used by normal application/API operations where practical.

Avoid creating a second business logic implementation specifically for MCP.

Conceptually:

```text
REST API ───────┐
                ├── Application Use Case
MCP ────────────┘
```

This ensures that:

* authorization
* tenant isolation
* business rules
* auditing

remain consistent.

---

# 50. MCP Security

MCP must never expose unrestricted:

* SQL
* filesystem
* shell
* HTTP
* database
* provider SDK

access.

MCP tools must be narrowly defined application capabilities.

Examples:

```text
SearchDocuments
GetDocument
GetDocumentMetadata
SearchAkten
UpdateMetadata
MoveDocument
RenameDocument
StartWorkflow
```

Each operation must perform normal authorization.

---

# 51. Workflows

Workflow execution belongs in the application/backend layer.

Workflow definitions should be stored as structured domain data.

Workflow execution must preserve:

* tenant context
* authorization context
* audit context
* correlation IDs

Workflow actions must use controlled application services.

Do not allow arbitrary workflow definitions to execute unrestricted code.

---

# 52. Workflow and External Changes

External storage changes may trigger workflows.

Examples:

```text
External document modification
        ↓
Document change detected
        ↓
Document processing
        ↓
Status/metadata change
        ↓
Workflow trigger
```

Workflow triggers must be idempotent where appropriate.

The same external change must not unintentionally start the same workflow multiple times.

---

# 53. Licensing

Licensing must be enforced by the backend.

Feature flags and commercial entitlements are distinct concepts.

Potential entitlement sources:

### SaaS

* subscription/tenant configuration

### Self-hosted online

* license service

### Self-hosted offline

* cryptographically signed license file

The backend must validate the license independently of frontend configuration.

---

# 54. Offline License Validation

Offline licenses must be cryptographically verifiable without Internet access.

A license may contain:

* product identifier
* license/customer identifier
* enabled features
* limits
* validity information
* product/version constraints

The backend must verify the cryptographic signature.

Never treat an editable configuration file as proof of a valid license.

---

# 55. Billing Abstraction

Billing must be abstracted.

Conceptually:

```text
IBillingProvider
```

The domain should use internal concepts such as:

```text
Subscription
Plan
Entitlement
License
SeatLimit
```

Paddle may be used as an implementation.

Paddle-specific types must not leak into domain logic.

---

# 56. Audit

Important operations should create audit records.

Examples:

* document creation
* document update
* document move
* document rename
* document deletion
* document restoration
* permission change
* metadata change
* workflow approval
* AI action
* MCP action
* administrative configuration
* connector changes
* authentication events

Audit records should contain appropriate:

* tenant
* actor
* action
* resource
* timestamp
* operation ID
* correlation ID

Audit data must itself be tenant-isolated.

---

# 57. Secrets

Secrets must be handled through secure configuration/secrets mechanisms.

Examples:

* storage credentials
* OAuth secrets
* LDAP passwords
* API keys
* AI keys
* billing credentials
* encryption keys

Never:

* hardcode secrets
* commit secrets
* log secrets
* expose secrets in API responses
* store secrets in plain application data unnecessarily

Secrets must be isolated per tenant where applicable.

---

# 58. Logging

Logs must be useful for diagnosis without exposing sensitive data.

Never log:

* passwords
* API keys
* OAuth secrets
* AI credentials
* complete document contents
* sensitive extracted text unnecessarily
* authorization tokens

Use structured logging.

Include appropriate:

* tenant context
* operation ID
* correlation ID
* resource ID

where safe and useful.

---

# 59. Error Handling

Errors returned by external providers must be normalized into application-level errors where appropriate.

Do not expose raw provider exceptions to API consumers.

The backend should distinguish between:

* validation errors
* authentication failures
* authorization failures
* not found
* conflict
* unsupported operation
* connector unavailable
* external service failure
* internal failure

Do not leak internal implementation details through error responses.

---

# 60. Concurrency and Transactions

Use database transactions where multiple related database changes must be atomic.

Do not assume that a database transaction can also atomically include an external storage provider.

External storage and PostgreSQL are separate systems.

For cross-system operations, use explicit state transitions and reconciliation rather than pretending a distributed transaction exists.

---

# 61. Persistence

PostgreSQL is the only required relational database.

EF Core may be used as the persistence implementation.

Database entities should not automatically become domain entities.

Use mapping where it helps preserve architectural boundaries.

Persistence concerns must not leak into the domain unnecessarily.

---

# 62. Database Constraints

Use PostgreSQL constraints to reinforce business rules.

Prefer:

* foreign keys
* unique constraints
* check constraints
* not-null constraints
* indexes
* appropriate cascading rules

Important tenant-scoped uniqueness rules must include tenant context where required.

For example:

```text
TenantId + ExternalId
```

may be unique where a provider guarantees uniqueness only within a tenant.

---

# 63. Database Migrations

All schema changes must use version-controlled migrations.

Do not require manual SQL modifications for normal application upgrades.

Migrations must be safe for:

* SaaS deployments
* self-hosted deployments
* offline upgrades

Consider migration ordering and compatibility during upgrades.

---

# 64. Caching

Caching may be used for:

* identity metadata
* permissions
* entitlements
* connector metadata
* configuration
* provider capabilities

Security-sensitive cache keys must include tenant context where appropriate.

Examples:

```text
tenant:{tenantId}:permissions:{userId}
tenant:{tenantId}:entitlements
tenant:{tenantId}:connector:{connectorId}
```

Never allow cache entries from one tenant to satisfy another tenant's request.

Cache invalidation must be considered for:

* permission changes
* role changes
* entitlement changes
* connector changes
* identity changes

---

# 65. File Downloads and Streaming

Document downloads must:

* authenticate the caller
* establish tenant context
* authorize document access
* respect storage permissions
* stream safely where possible
* avoid unnecessary buffering of large files
* avoid exposing storage credentials

Do not expose raw storage-provider URLs unless the security model explicitly supports it.

Temporary download URLs must be appropriately scoped and time-limited.

---

# 66. File Uploads

Uploads must:

* validate authorization
* validate tenant context
* validate destination
* respect connector capabilities
* validate file size limits
* safely handle filenames
* prevent path traversal
* handle provider conflicts
* be auditable where appropriate

Do not trust client-provided paths.

---

# 67. Filename and Path Security

Never trust filenames or paths supplied by clients or external storage.

Protect against:

* path traversal
* absolute paths
* invalid path components
* reserved names
* malicious Unicode/path representations
* oversized names
* unexpected separators

The correct validation depends on the connector.

Provider-specific path rules belong in the connector implementation.

---

# 68. Temporary Document Processing

When document contents must be materialized locally:

* use secure temporary locations
* isolate temporary work where practical
* use short-lived files
* clean up after successful processing
* clean up after failures
* avoid leaking temporary paths
* avoid cross-tenant reuse

Document content in temporary storage remains sensitive customer data.

---

# 69. External Connector Credentials

Connector credentials must be securely stored.

Potential credentials include:

* SMB credentials
* OAuth tokens
* client secrets
* access keys
* service account credentials

Credentials must be associated with the appropriate tenant and connector.

Never expose credentials through:

* API responses
* logs
* frontend state
* error messages

---

# 70. Connector Lifecycle

Connectors should have explicit lifecycle states where useful.

Examples:

```text
Configured
Connected
Unavailable
AuthenticationRequired
Disabled
Error
```

Connector configuration changes must be validated.

A connector becoming unavailable must not delete or corrupt existing logical document metadata.

---

# 71. Connector Reconfiguration

Changing connector configuration must be treated carefully.

Examples:

* changing credentials
* changing root path
* changing SharePoint site
* changing bucket
* changing account

The backend must not silently reinterpret unrelated existing documents as belonging to a different storage location.

Reconfiguration may require explicit reconciliation or migration behavior.

---

# 72. Connector Deletion

Deleting a connector must not automatically delete logical documents unless explicitly defined by product behavior.

Existing documents may become:

```text
Unavailable
ConnectorMissing
```

The DMS should preserve logical relationships and metadata.

Connector deletion should be auditable.

---

# 73. Storage Provider Versioning

Where providers support document versions, the backend should preserve enough information to understand the current version.

Version support must remain connector-specific.

Do not assume every provider has the same versioning semantics.

The application should expose normalized version concepts where useful without pretending provider implementations are identical.

---

# 74. Event Model

The backend may use internal application events for decoupling modules.

Examples:

```text
DocumentCreated
DocumentModified
DocumentMoved
DocumentRenamed
DocumentDeleted
DocumentRestored
MetadataChanged
PermissionChanged
```

Events must contain sufficient tenant and resource context.

Do not introduce a distributed event bus merely because events exist.

An in-process application event mechanism may be sufficient for the modular monolith.

---

# 75. Event Idempotency

Consumers of events must not assume exactly-once delivery.

Important event handlers should be idempotent.

For example:

```text
DocumentModified
```

may cause:

* indexing
* OCR
* embedding generation
* workflow triggers

The same event must not corrupt state if processed more than once.

---

# 76. Module Boundaries

Modules should communicate through explicit contracts.

Avoid arbitrary direct access to another module's:

* database tables
* internal services
* repositories
* domain internals

Prefer application-level contracts and domain concepts.

The goal is to keep modules independently understandable and testable.

---

# 77. Repository Pattern

Do not introduce repositories solely for ceremony.

Use persistence abstractions where they provide a meaningful architectural boundary.

The domain must not depend on EF Core.

Application code should not be tightly coupled to EF Core-specific query behavior unless the architecture explicitly accepts that trade-off.

---

# 78. Validation

Validate input at appropriate boundaries.

Examples:

* API request validation
* domain invariant validation
* connector-specific validation
* configuration validation
* license validation

Do not rely solely on frontend validation.

Security-sensitive validation must occur on the backend.

---

# 79. Background Job Security

Background jobs must not bypass normal authorization merely because they run internally.

Jobs acting on behalf of a user should preserve the initiating identity where relevant.

System jobs may use a dedicated service identity with explicit permissions.

Never use an unrestricted "admin" context for all background processing by default.

---

# 80. AI Background Jobs

AI processing jobs must carry:

* tenant
* document/resource identity
* initiating identity where applicable
* authorization context or sufficient information to re-evaluate authorization
* AI provider configuration reference

Never enqueue raw API credentials.

Never assume that a document remains authorized merely because it was authorized when the job was created.

Where appropriate, authorization should be re-evaluated when the job executes.

---

# 81. Search Authorization

Search must apply authorization before returning results.

Do not:

```text
search everything
    ↓
filter later in an unsafe layer
```

Instead, the search architecture must incorporate tenant and permission constraints as part of the query/execution model.

Search results must not reveal unauthorized:

* filenames
* metadata
* snippets
* OCR text
* document IDs
* locations

unless explicitly permitted.

---

# 82. AI Search Authorization

Semantic/vector search must obey the same authorization rules as normal search.

A vector index is not an authorization boundary.

If embeddings contain information derived from a restricted document, access to those embeddings must be restricted accordingly.

---

# 83. Data Deletion

Deletion must distinguish between:

* logical document deletion
* physical file deletion
* derived-data deletion
* audit retention

Deleting a logical document must not accidentally delete customer storage data unless the user explicitly authorized the physical operation and the connector supports it.

Likewise, deleting a physical file externally must not automatically erase all DMS history.

---

# 84. Audit vs. Deletion

Audit records may need to survive document deletion depending on retention requirements.

Do not automatically cascade-delete all audit history merely because a document was deleted.

Retention rules should be explicit.

---

# 85. Security Boundaries

The following are security boundaries:

```text
HTTP request
Tenant
User
Resource
Storage provider
AI provider
Background job
MCP tool
API client
```

Every boundary must have explicit authentication/authorization semantics.

Never assume internal code is automatically trusted if it handles tenant-sensitive data.

---

# 86. Configuration

Configuration should be strongly typed where practical.

Separate:

* application configuration
* tenant configuration
* connector configuration
* feature flags
* commercial entitlements
* secrets
* license information

Do not mix licensing proof with editable application configuration.

Do not expose internal configuration directly through generic admin endpoints.

---

# 87. Feature Flags vs. Entitlements

A technical feature flag answers:

> Is this functionality enabled technically?

An entitlement answers:

> Is this customer allowed to use this functionality?

These must remain conceptually separate.

Example:

```text
AI_ENABLED = true
```

does not necessarily mean:

```text
Tenant has entitlement to AI
```

The effective feature state may require both.

---

# 88. API Rate Limiting

Where rate limits are part of the product or security model, enforce them server-side.

Rate limits may depend on:

* tenant
* user
* API client
* endpoint
* subscription
* entitlement

Never rely solely on frontend throttling.

---

# 89. Health Checks

The backend should expose appropriate health information for operational monitoring.

Distinguish between:

* application process health
* database availability
* connector availability
* optional integration availability

An unavailable optional integration should not necessarily make the entire application report itself as fundamentally unhealthy.

---

# 90. Observability

Use structured logging and tracing where appropriate.

Important operations should carry:

* tenant ID
* operation ID
* correlation ID
* resource ID

Do not log sensitive content.

Metrics should help identify:

* request failures
* connector failures
* synchronization lag
* processing failures
* queue backlog
* workflow failures
* search performance
* AI usage
* rate-limit events

---

# 91. Performance

Optimize only after correctness and security.

Important performance considerations include:

* database indexes
* tenant-scoped queries
* pagination
* streaming
* background processing
* connector batching
* incremental synchronization
* search indexing
* caching

Never optimize by removing authorization checks.

Never optimize by bypassing tenant isolation.

---

# 92. Pagination

List APIs must use explicit pagination for potentially large collections.

Examples:

* documents
* Akten
* users
* audit records
* search results
* connectors

Do not load arbitrarily large tenant datasets into memory.

Pagination must remain tenant- and permission-aware.

---

# 93. API Versioning

Breaking API changes must be versioned or handled through a compatible migration strategy.

Do not silently change the semantics of existing API operations.

OpenAPI documentation must reflect the actual backend contract.

---

# 94. Testing Strategy

Tests are mandatory for security-critical behavior.

At minimum, test:

### Tenancy

* tenant isolation
* tenant context resolution
* cross-tenant access attempts
* background-job tenant isolation

### Authorization

* role permissions
* group permissions
* inherited permissions
* explicit overrides
* resource-level authorization
* API authorization
* search authorization
* AI authorization
* MCP authorization

### Storage

* connector capabilities
* upload
* download
* move
* rename
* delete
* external changes
* synchronization
* restoration
* concurrency conflicts

### Documents

* stable identity
* Akte relationships
* metadata
* missing documents
* external modification
* physical replacement

### Licensing

* offline signature validation
* feature entitlements
* seat limits
* disabled features

### Workflows

* triggers
* actions
* retries
* idempotency
* authorization

---

# 95. Integration Tests

Use real PostgreSQL for integration tests where practical.

Important integration scenarios include:

```text
Tenant A → document access
Tenant B → same document must fail

External rename → same DocumentId

External move → same Akte relationship

External deletion → logical document becomes Missing

External restoration → logical document can become Available

Concurrent update → conflict detected

Search → unauthorized document absent

AI → unauthorized document inaccessible

MCP → same authorization as REST
```

---

# 96. Architecture Tests

Architecture tests should prevent accidental dependencies such as:

```text
Domain → Infrastructure
Domain → ASP.NET
Domain → EF Core
Domain → Provider SDK
Domain → Paddle
Domain → OpenAI
```

Architecture tests should also prevent provider-specific logic from leaking into core modules.

---

# 97. No Provider Conditionals in Core

Do not write business logic such as:

```csharp
if (connector.Type == "SharePoint")
{
    ...
}
else if (connector.Type == "Dropbox")
{
    ...
}
```

inside domain/application business logic when the behavior can be expressed through connector capabilities.

Provider-specific behavior belongs in the connector.

---

# 98. No Vendor Models in Domain

Do not use types such as:

```text
GraphDriveItem
DropboxFile
PaddleSubscription
OpenAIResponse
```

as domain entities.

Map external representations into internal models.

The domain must remain vendor-neutral.

---

# 99. No Infrastructure Leakage

Avoid returning infrastructure-specific exceptions, entities or implementation details from application APIs.

Normalize them into application-level concepts.

The domain should not know:

```text
SqlException
HttpRequestException
GraphServiceException
DropboxApiException
```

unless there is an explicitly justified abstraction.

---

# 100. External Change Processing Flow

A typical external modification should follow a flow similar to:

```text
Storage Provider
      ↓
Connector Change Detection
      ↓
Normalized Storage Change
      ↓
Tenant-aware Application Processing
      ↓
Document Resolution
      ↓
Concurrency / Identity Validation
      ↓
Logical Document State Update
      ↓
Audit Event
      ↓
Background Processing
      ↓
Search / OCR / AI / Workflow
```

Each stage must preserve tenant context.

---

# 101. External Change Identity Resolution

When processing an external change, the backend should identify the logical document using the strongest available identity.

Preferred order may include:

1. stable provider external ID
2. provider-specific immutable identifier
3. reliable version/object identity
4. carefully controlled fallback reconciliation

Never assume that:

```text
path + filename
```

is globally stable document identity.

If identity cannot be established safely, do not silently attach the change to an existing logical document.

---

# 102. Change Ordering

External changes may arrive:

* out of order
* multiple times
* late
* after temporary disconnection

The synchronization system must not blindly apply an older change over a newer known state.

Where provider version information exists, use it.

Where it does not, use available timestamps and reconciliation logic carefully.

---

# 103. Connector Checkpoints

Incremental connectors should persist enough state to resume synchronization.

Examples:

* delta token
* change cursor
* page token
* revision checkpoint

Checkpoint state must be tenant- and connector-specific.

A failed synchronization must be restartable without corrupting state.

---

# 104. Connector Reconciliation

A connector should support reconciliation where incremental change tracking is insufficient.

Reconciliation may compare:

* external IDs
* paths
* filenames
* versions
* modification timestamps
* sizes
* other provider-specific metadata

Reconciliation must be conservative.

When identity is ambiguous, prefer a safe unresolved state over an incorrect automatic association.

---

# 105. Storage Outages

If a connector becomes unavailable:

* do not delete documents
* do not mark all documents as deleted
* do not overwrite storage references
* preserve known state
* record connector availability state
* retry according to policy

The DMS should distinguish:

```text
Document is missing
```

from:

```text
Storage system cannot currently be reached
```

These are not equivalent.

---

# 106. Search During Storage Outage

A storage outage must not automatically cause all logical documents to disappear from search.

Depending on product policy, search may continue using known indexed metadata/content.

However, the backend must not expose document content if current authorization cannot be established or if storage permissions are required and cannot be safely evaluated.

The exact behavior must be defined per storage/authorization model.

---

# 107. Permission Changes Outside Akasha

Storage permissions may also change outside Akasha.

Connectors that support permission discovery/change tracking should surface those changes.

The backend must not assume that Akasha's permission state is the complete authorization state.

Effective access must account for the storage provider where required.

---

# 108. External Metadata Changes

If a storage provider supports relevant metadata changes, the connector may surface them.

The backend must distinguish:

* Akasha-managed metadata
* storage-provider metadata
* derived metadata

Do not overwrite Akasha-managed metadata merely because external provider metadata changed.

Ownership of each metadata field must be explicit.

---

# 109. Document State Machine

The document lifecycle should distinguish logical existence from physical availability.

A conceptual model may include:

```text
Pending
   ↓
Available
   ↓
Modified
   ↓
Available

Available
   ↓
Missing
   ↓
Available
```

Additional operational states may include:

```text
SyncError
Unavailable
Processing
```

Do not encode all operational state into a single ambiguous status field if separate state dimensions are more appropriate.

---

# 110. Processing State vs. Availability State

Keep these concepts separate.

For example:

```text
Availability:
Available

Processing:
OCRFailed
```

means:

> The physical document exists, but processing failed.

This is different from:

```text
Availability:
Missing
```

The backend should avoid conflating these states.

---

# 111. Document Version Semantics

Where a provider supports versions, the backend should distinguish:

* logical document identity
* current storage version
* processing version
* indexed version

This prevents stale processing results from being applied to newer document versions.

Example:

```text
Storage version 7
Processing result for version 6
```

The backend must recognize that the processing result is stale.

---

# 112. Processing Idempotency and Version Checks

Background processing should include the document version it was created for where appropriate.

Before committing derived results, verify that the underlying document has not changed.

If it has changed:

```text
discard stale result
```

or:

```text
schedule processing for newer version
```

Do not silently attach version-6 OCR to version-7 content.

---

# 113. Document Content Access

Document content retrieval must be performed through the storage abstraction.

Do not allow application code to directly access:

```text
File.Open(...)
HttpClient → provider
Graph SDK
Dropbox SDK
```

outside the appropriate connector/infrastructure boundary.

---

# 114. Storage Connector Streaming

Large files should be streamed where practical.

Avoid loading entire documents into memory unnecessarily.

Connector APIs should support streaming for:

* downloads
* uploads
* processing pipelines

Memory usage must not scale linearly with arbitrary document size when streaming is possible.

---

# 115. Content-Type and File Metadata

Do not blindly trust client-provided MIME types.

Where necessary, validate or detect content type using safe mechanisms.

Store normalized metadata where useful:

* filename
* extension
* MIME type
* size
* timestamps
* version
* hash where appropriate

Provider-specific metadata remains connector-specific.

---

# 116. Content Hashes

Content hashes may be used for:

* change detection
* deduplication
* integrity checking
* processing validation

Do not assume a content hash is always available or sufficient for logical identity.

A hash does not replace provider external IDs where stable provider identity exists.

---

# 117. Document Deduplication

Do not automatically merge logical documents solely because two files have identical content.

Identical files may legitimately represent separate business documents.

Deduplication must be an explicit product capability if introduced.

---

# 118. Transaction Boundaries

Keep database transactions focused.

Do not hold a PostgreSQL transaction open while waiting for a slow external storage operation unless there is a compelling reason.

For operations spanning PostgreSQL and external storage, prefer:

```text
external operation
        ↓
state update
        ↓
commit
```

with reconciliation for partial failures.

---

# 119. Distributed Consistency

Do not assume strong atomic consistency across:

* PostgreSQL
* storage provider
* search engine
* AI provider
* vector store
* external APIs

Use explicit state transitions, retry mechanisms and reconciliation.

Eventual consistency is acceptable for derived data such as:

* search indexes
* embeddings
* OCR
* AI summaries

but not for authorization decisions.

---

# 120. Authorization Is Always Current

Cached permissions may be used for performance, but authorization decisions must account for permission changes.

After a significant permission change, relevant caches must be invalidated.

Do not allow stale authorization caches to become a permanent security hole.

---

# 121. API and MCP Shared Application Logic

Where practical:

```text
REST Controller
       ↓
Application Service

MCP Tool
       ↓
Application Service
```

Both paths must use the same:

* tenant resolution
* authorization
* business rules
* storage abstractions
* auditing

Do not duplicate business rules between REST and MCP.

---

# 122. Service Accounts

Service accounts may be used for:

* integrations
* background operations
* automation
* MCP
* external applications

Service accounts must:

* belong to an appropriate tenant
* have explicit permissions
* be auditable
* have limited privileges
* not automatically receive unrestricted administrative access

---

# 123. API Clients

API clients must have explicit authentication and authorization.

Do not create one unrestricted global API key for all integrations.

API access should support appropriate:

* client identity
* tenant association
* scopes/permissions
* rate limits
* auditing
* revocation

---

# 124. Administrative Operations

Administrative APIs must be strongly protected.

Examples:

* connector configuration
* identity-provider configuration
* user/group management
* permission configuration
* workflow configuration
* AI configuration
* MCP configuration
* licensing
* subscription configuration

Administrative access must remain tenant-scoped unless the operation is explicitly a platform-level SaaS operation.

---

# 125. SaaS Platform Administration

Platform-level administration may operate across tenants only through explicitly authorized platform administration mechanisms.

Do not reuse normal tenant-user authorization as an implicit global administrator mechanism.

Platform administration must be:

* strongly authenticated
* explicitly authorized
* highly auditable

---

# 126. Self-Hosted Administration

Self-hosted administration must remain within the customer's installation.

Do not require a central SaaS control plane for fundamental self-hosted operation.

Offline installations must be able to perform supported administrative tasks without Internet access.

---

# 127. Database Backup and Restore

The backend should be designed so that PostgreSQL data can be backed up and restored predictably.

Restore scenarios must consider:

* tenant data
* document metadata
* storage references
* synchronization checkpoints
* workflow state
* licenses
* audit data

The physical files may reside outside PostgreSQL and therefore require separate customer-managed storage backup strategies.

---

# 128. Storage Backup Assumptions

Do not assume Akasha can restore physical customer files merely because the PostgreSQL database was restored.

The backend should clearly distinguish:

```text
DMS metadata backup
```

from:

```text
Physical storage backup
```

Customer storage remains the customer's responsibility unless an explicit product feature provides otherwise.

---

# 129. Data Import

Imports must use the same document and storage abstractions as normal document creation where possible.

Do not create special import-only document identities that bypass normal document rules.

Imported documents must receive:

* tenant context
* logical identity
* appropriate metadata
* authorization
* audit information

---

# 130. Data Export

Exports must:

* enforce authorization
* respect tenant boundaries
* respect document permissions
* be auditable where appropriate
* avoid exposing unrelated tenant data

Large exports should use asynchronous processing where appropriate.

---

# 131. API DTOs

Do not expose database entities directly unless there is a deliberate architectural reason.

API DTOs should represent stable contracts.

Avoid returning internal fields such as:

* internal database IDs that are not part of the contract
* connector credentials
* internal storage implementation details
* authorization internals
* licensing secrets

---

# 132. Naming

Use clear, domain-oriented names.

Prefer:

```text
Document
StorageReference
StorageConnector
Akte
Tenant
Permission
Workflow
Entitlement
```

Avoid vague names such as:

```text
Data
Item
Object
Manager
Helper
Utils
CommonService
```

unless their meaning is genuinely clear.

---

# 133. Generic Helpers

Avoid creating large generic helper libraries that become hidden dependencies between modules.

Shared code should be:

* small
* stable
* genuinely cross-cutting
* dependency-light

Do not put domain-specific business rules into generic utility classes.

---

# 134. Exception Handling

Exceptions should represent exceptional conditions rather than normal control flow.

Expected business outcomes should use explicit result/error models where appropriate.

External provider exceptions should be mapped into meaningful application-level categories.

Do not catch all exceptions and silently continue.

---

# 135. Cancellation

Long-running operations should support cancellation where practical.

Cancellation should propagate through:

* HTTP request
* application service
* connector
* database query
* background processing

Do not leave abandoned external operations running indefinitely where cancellation is supported.

---

# 136. Timeouts

External calls must use appropriate timeouts.

Do not allow:

* storage calls
* identity calls
* AI calls
* external APIs

to hang indefinitely.

Timeouts should result in explicit operational states rather than silent failures.

---

# 137. Rate Limits from Providers

Storage and AI providers may impose rate limits.

Connectors should normalize rate-limit behavior where practical.

The backend should use:

* retry-after information
* exponential backoff
* bounded retries

Do not aggressively retry rate-limited providers.

---

# 138. Provider Authentication Expiration

Connector authentication may expire.

The backend should represent this explicitly:

```text
AuthenticationRequired
```

Do not treat expired credentials as a generic document deletion or synchronization failure.

---

# 139. Connector Testing

Each connector must have tests for:

* authentication
* capability reporting
* read
* write
* rename
* move
* delete
* versioning where supported
* change detection
* permission handling
* external failures
* rate limiting
* concurrency
* restoration

Provider-specific integration tests may be separated from generic connector contract tests.

---

# 140. Connector Contract Tests

All connectors should satisfy a common connector contract where their capabilities support the relevant operation.

For example:

```text
If SupportsRename == true:
    Rename must preserve external identity where provider semantics allow it.
```

Connector contract tests help ensure consistent behavior across providers.

---

# 141. Local Filesystem Connector

The local filesystem connector must still obey the same abstraction and security model as cloud connectors.

Do not treat local filesystem access as inherently trusted.

Consider:

* path traversal
* symbolic links
* permissions
* file locks
* concurrent modification
* external deletion
* external rename
* large files

---

# 142. SMB Connector

The SMB connector must remain isolated behind the storage abstraction.

SMB may have weaker change-tracking semantics than modern cloud APIs.

The connector may need to use:

* filesystem metadata
* directory scanning
* file IDs where available
* timestamps
* size
* hashes where necessary

The core DMS must not depend on SMB-specific detection strategies.

---

# 143. Cloud Connectors

Cloud connectors should prefer provider-native stable identifiers and change tracking.

Where providers support:

* immutable IDs
* delta APIs
* webhooks
* ETags
* version history

use them where appropriate.

Do not reduce cloud storage identity to paths when stronger identity is available.

---

# 144. Search Index Rebuilds

The backend must support rebuilding search indexes without changing logical document identity.

A full reindex must not:

* create new `DocumentId`s
* break Akte relationships
* alter storage references
* bypass permissions

Index rebuilds should be safe and repeatable.

---

# 145. AI Index Rebuilds

Embeddings and AI-derived indexes must likewise be rebuildable.

A vector index is derived data.

It must be possible to regenerate it from authoritative document data and metadata.

Do not treat embeddings as the authoritative source of document identity or permissions.

---

# 146. Authoritative Data

The backend should clearly distinguish authoritative data from derived data.

Authoritative:

* logical document identity
* Akte relationships
* metadata
* permissions
* storage references
* workflow definitions
* licensing state

Derived:

* search indexes
* OCR output where reproducible
* embeddings
* AI summaries
* classifications where configured as derived

Derived data may be rebuilt.

---

# 147. Derived Data Consistency

Derived data must include enough information to determine its source version.

For example:

```text
DocumentId
SourceVersion
GeneratedAt
ProcessorVersion
```

This helps identify stale results after external document changes.

---

# 148. Processor Versioning

Processing components may evolve.

Where practical, store the processor/version used for derived data such as:

* OCR
* extraction
* classification
* embeddings

This supports controlled reprocessing after upgrades.

---

# 149. Security During Processing

Document processing must run under controlled permissions.

Processing services must not automatically gain unrestricted access to all tenant documents.

A processing worker should receive only the document/resource context necessary for its operation.

---

# 150. No Direct Shell Execution

The backend must not expose arbitrary shell execution through:

* API
* MCP
* AI
* workflows
* document metadata
* user input

If an external processing tool requires a process invocation, it must be narrowly controlled and isolated.

---

# 151. No Arbitrary SQL Execution

Do not expose arbitrary SQL execution through:

* API
* MCP
* AI
* workflows
* administrative endpoints

All database operations must go through controlled application/persistence abstractions.

---

# 152. No Arbitrary HTTP Proxy

Do not expose unrestricted HTTP requests through:

* AI
* MCP
* workflows
* document metadata
* user-controlled configuration

External integrations must use explicitly configured and authorized providers/endpoints.

---

# 153. Prompt Injection

Document contents, OCR text, metadata and external content must be treated as untrusted input.

Prompt injection must not be able to:

* change authorization
* reveal protected documents
* invoke unrestricted tools
* modify tenant context
* execute arbitrary code
* access secrets

Security decisions must always be enforced outside the LLM.

---

# 154. AI Tool Permissions

Each AI tool must have explicit permissions.

For example:

```text
SearchDocuments → READ
GetDocument → READ
UpdateMetadata → EDIT_METADATA
MoveDocument → MOVE
RenameDocument → RENAME
StartWorkflow → START_WORKFLOW
```

The AI must not receive an unrestricted administrative toolset merely because the user is using AI chat.

---

# 155. Audit AI Operations

AI actions that modify data should be auditable.

Audit should distinguish:

* human initiated operation
* AI-assisted operation
* automated workflow
* system synchronization

The audit trail should preserve the initiating identity where appropriate.

---

# 156. Workflow Authorization

Workflow actions must not bypass authorization.

If a workflow moves or modifies a document, the workflow execution identity must have the required permission.

Do not automatically run every workflow as an unrestricted administrator.

---

# 157. Scheduled Operations

Scheduled tasks must have explicit tenant context.

A scheduler must not accidentally execute a tenant-specific job against another tenant.

Recurring jobs should be identifiable and auditable.

---

# 158. Multi-Tenant Background Scheduling

When scheduling work for multiple tenants:

```text
Scheduler
   ↓
Tenant-specific job
   ↓
Tenant context
   ↓
Authorization / service identity
   ↓
Operation
```

Never use mutable global tenant state.

---

# 159. Thread Safety

Do not store tenant context, user identity or authorization state in mutable global/static state.

Request-specific context must be scoped appropriately.

Background jobs must establish their own context.

---

# 160. Async Code

Use asynchronous APIs for I/O-bound operations.

Avoid blocking:

```text
.Result
.Wait()
```

on asynchronous operations in request processing.

Cancellation tokens should be propagated where appropriate.

---

# 161. Database Query Safety

Queries must:

* be tenant-scoped
* be authorization-aware where necessary
* use appropriate indexes
* avoid unbounded result sets
* avoid N+1 patterns where practical

Do not load entire tenant datasets merely to perform filtering in memory.

---

# 162. EF Core Query Safety

Be especially careful with:

* global query filters
* tenant context
* navigation loading
* raw SQL
* compiled queries
* caching

Global query filters may help enforce tenant isolation, but they must not be treated as the only authorization mechanism.

Explicit security boundaries should remain clear.

---

# 163. Raw SQL

Raw SQL may be used where justified for:

* performance
* PostgreSQL-specific functionality
* advanced search
* migrations

It must still enforce:

* tenant isolation
* parameterization
* authorization requirements

Never concatenate untrusted user input into SQL.

---

# 164. PostgreSQL Row-Level Security

PostgreSQL RLS may be used as defense in depth.

If implemented, tenant context must be securely propagated to the database session/transaction.

Never allow a client to arbitrarily set the PostgreSQL tenant context.

RLS must complement, not replace, application authorization.

---

# 165. Database Indexing

Indexes should support common tenant-scoped queries.

Examples may include combinations of:

```text
TenantId + DocumentId
TenantId + Name
TenantId + ExternalId
TenantId + Status
TenantId + ModifiedAt
```

Exact indexes should be driven by actual query patterns.

---

# 166. Unique Constraints

Unique constraints must account for tenant boundaries.

For example, if external IDs are only unique within a tenant:

```text
UNIQUE (TenantId, ConnectorId, ExternalId)
```

may be appropriate.

Do not accidentally create globally unique constraints for tenant-scoped identifiers unless required.

---

# 167. Foreign Keys

Use foreign keys for important relationships.

Examples:

```text
Document → Tenant
Akte → Tenant
DocumentAkte → Document
DocumentAkte → Akte
StorageReference → Connector
Connector → Tenant
```

Do not rely solely on application code for relational integrity.

---

# 168. Cascading Deletes

Be careful with cascading deletes.

Do not configure cascades that can accidentally delete large portions of tenant data.

Especially review relationships involving:

* documents
* audit
* storage references
* workflows
* derived data

Deletion semantics must reflect business rules.

---

# 169. Audit Retention

Audit retention must be explicit.

Do not make audit data disappear accidentally because a related domain object was deleted.

Retention requirements may differ from document retention.

---

# 170. Security Reviews

For security-sensitive backend changes, explicitly review:

* tenant isolation
* authorization
* identity
* storage permissions
* secrets
* audit
* concurrency
* external change handling
* data exposure
* logging

Security should be considered during design, not added afterward.

---

# 171. New Backend Feature Checklist

Before implementing a new backend feature, answer:

1. What tenant does this operation belong to?
2. How is tenant context established?
3. Which identity performs the operation?
4. Which permission is required?
5. Does the operation touch physical customer storage?
6. If yes, which connector abstraction is used?
7. What happens if the storage provider fails?
8. What happens if the physical document changed externally?
9. What happens if the operation races with another change?
10. What happens if the document is missing?
11. Does the operation need an audit entry?
12. Does it require an entitlement?
13. Does it create background work?
14. Is the background work tenant-safe?
15. Is the operation idempotent where necessary?
16. Does it expose data through search, AI or MCP?
17. Does it preserve logical `DocumentId`?
18. Does it preserve Akte relationships?
19. Does it work in SaaS?
20. Does it work self-hosted?
21. Does it work offline where applicable?
22. Can it be tested independently?

If these questions cannot be answered clearly, the feature design is not complete.

---

# 172. Final Backend Rules

The following rules are mandatory:

1. Never trust a client-provided tenant ID as the sole authorization mechanism.
2. Never rely on frontend authorization.
3. Never bypass the authorization layer through API, AI, MCP or background jobs.
4. Never identify logical documents solely by path or filename.
5. Never put provider-specific storage logic in the domain.
6. Never silently overwrite newer external document changes.
7. Never treat external deletion as automatic logical deletion.
8. Never assume customer storage is immutable.
9. Never expose storage credentials to clients.
10. Never log secrets or document contents unnecessarily.
11. Never expose unrestricted SQL, shell or HTTP through AI/MCP/workflows.
12. Never make AI mandatory for core DMS functionality.
13. Never make Internet access mandatory for supported offline self-hosted functionality.
14. Never introduce a second relational database without a demonstrated requirement.
15. Never introduce microservices merely for architectural fashion.
16. Never allow search indexes or vector stores to become authorization bypasses.
17. Never treat derived AI/search data as more authoritative than the source document and DMS state.
18. Always preserve logical document identity across external rename and move operations.
19. Always account for external modifications, deletions, restorations and concurrent changes.
20. Always preserve tenant context in asynchronous processing.
21. Always enforce commercial entitlements on the backend.
22. Always keep external providers behind explicit abstractions.
23. Always prefer PostgreSQL relational structures for core business data.
24. Always make security-critical behavior testable.
25. Always prefer safe reconciliation over incorrect automatic association when external storage identity is ambiguous.

