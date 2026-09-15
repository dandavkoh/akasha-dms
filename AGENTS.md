# AGENTS.md

## Project

This repository contains the source code for **Akasha DMS**, a modern, modular, secure and storage-agnostic Document Management System.

Akasha DMS is developed by **Softwareentwicklung Kohsyk**.

The product is designed to run both as:

1. a multi-tenant SaaS application
2. a self-hosted installation in the customer's infrastructure

Both deployment models must use the same fundamental architecture and business logic.

---

# 1. Product Vision

Akasha DMS is a logical document management layer that sits above customer-controlled storage systems.

Core principle:

> **One DMS. Many storage systems. Customer-owned data. One unified experience.**

Akasha must not require customers to migrate their files into a proprietary Akasha storage system.

Physical document files remain in infrastructure controlled by the customer.

Possible storage systems include:

* local filesystem
* SMB/network shares
* SharePoint
* OneDrive
* Google Drive
* Dropbox
* S3-compatible storage
* other future storage providers

The architecture must allow additional storage providers without modifying the core domain model.

---

# 2. Customer-Owned Data

Akasha DMS does not inherently own the customer's physical document files.

The DMS manages:

* logical document identity
* metadata
* document relationships
* Akten
* permissions
* workflows
* search indexes
* audit information
* optional derived AI data

The actual document content remains in the configured customer storage system.

Any cached, indexed, extracted or AI-derived representation of document content must be treated as potentially sensitive customer data.

Such data must:

* respect tenant isolation
* respect document permissions
* be securely stored
* be removable when required
* never be exposed to unauthorized tenants or users

---

# 3. Logical Documents vs. Physical Files

A document in Akasha is a **logical entity** and must not be identified solely by:

* file path
* filename
* folder
* URL
* storage location

A logical Akasha document must have its own stable identity.

Conceptually:

```text
Akasha Document
        │
        ├── DocumentId
        ├── Metadata
        ├── Akten relationships
        ├── Permissions
        └── Storage Reference
                │
                ├── Connector
                ├── External ID
                ├── Current path
                └── Current filename
```

The physical location of a document may change without changing the logical identity of the document.

This distinction is fundamental to the architecture.

---

# 4. External Changes Are Expected

Akasha must assume that documents can be modified outside of Akasha.

Users or external systems may:

* rename files
* move files
* modify files
* replace files
* delete files
* restore files
* create new files
* change metadata
* change permissions

These operations may happen through the underlying storage provider rather than through Akasha.

This is not an exceptional situation.

It is a normal and supported operating scenario.

Akasha must therefore not assume that the storage state always matches the last state known by the DMS.

---

# 5. External Change Handling

The DMS must be able to recognize and react to changes in connected storage systems.

Examples:

```text
Storage change
      │
      ├── Created
      ├── Modified
      ├── Renamed
      ├── Moved
      ├── Deleted
      └── Restored
```

Provider-specific mechanisms for detecting these changes belong inside the corresponding storage connector.

The core DMS must consume normalized change information rather than implementing provider-specific logic.

Where supported, connectors should use provider-native mechanisms such as:

* change tokens
* delta APIs
* webhooks/change notifications
* version identifiers
* ETags
* stable external IDs

For storage systems with limited change tracking capabilities, the connector may need to use reconciliation or metadata comparison.

---

# 6. Rename and Move Semantics

Renaming or moving a physical file must not break its logical relationship inside Akasha.

For example:

```text
Before:

Akte 2026-001
└── contract.pdf


External user renames file:

contract.pdf
        ↓
vertrag.pdf


After:

Akte 2026-001
└── vertrag.pdf
```

The logical `DocumentId` remains unchanged.

The Akte relationship remains unchanged.

The storage reference is updated to reflect the new physical location or filename.

Never identify an Akasha document solely by its path.

---

# 7. External Modification Semantics

If a document is modified outside Akasha, Akasha must recognize that the physical document has changed.

Depending on the configured processing pipeline, the change may trigger:

* metadata refresh
* text extraction
* OCR
* classification
* full-text reindexing
* semantic indexing
* embedding regeneration
* AI-derived metadata refresh
* workflow triggers
* audit events

The exact processing behavior belongs to the relevant modules.

The core architecture must nevertheless support external modification as a first-class event.

---

# 8. External Deletion Semantics

If a physical document is deleted outside Akasha, the logical document must not automatically disappear from the DMS.

Akasha should retain, where appropriate:

* logical `DocumentId`
* Akte relationships
* metadata
* audit history
* last known filename
* last known location
* last known storage identity
* information that the physical document is currently unavailable

Conceptually:

```text
Document
State: Missing / Unavailable
```

The exact state model belongs to the implementation architecture.

The important product principle is:

> A missing physical file does not necessarily mean that the logical DMS document and its history should be deleted.

If the physical document is later restored and can be reliably associated with the existing logical document, Akasha should be able to restore the document's availability state.

---

# 9. External Concurrency

Multiple actors may modify the same document:

* Akasha users
* external users
* storage-provider applications
* synchronization processes
* workflows
* AI operations
* APIs
* integrations

The system must not silently overwrite newer external changes.

Where supported, storage version information such as:

* ETag
* version ID
* revision ID
* modification timestamp

should be used for optimistic concurrency.

If Akasha attempts to modify a document based on an outdated version, the system should detect the conflict rather than silently overwriting the newer version.

---

# 10. Akten

An **Akte** is a logical business concept.

An Akte is not necessarily equivalent to:

* a physical folder
* a directory
* a SharePoint folder
* an SMB directory

An Akte may contain documents originating from different storage systems.

For example:

```text
Akte: Customer ABC

├── SharePoint
│   └── Contract.pdf
│
├── SMB
│   └── Invoice.pdf
│
└── OneDrive
    └── Correspondence.pdf
```

The Akte relationship exists at the logical DMS level.

Physical storage structure must not dictate the domain model.

---

# 11. Storage Abstraction

The core application must never contain provider-specific business logic.

The architecture must use storage abstractions such as:

```text
IStorageConnector
```

Potential capabilities include:

* Read
* Write
* Delete
* Move
* Copy
* Rename
* Versioning
* Metadata
* Permissions
* ChangeTracking
* WatchChanges
* Locking
* RecycleBin
* NativeSearch

The exact interface belongs to the backend architecture.

The core must reason in terms of capabilities rather than provider names.

Avoid patterns such as:

```text
if provider == SharePoint
if provider == Dropbox
if provider == SMB
```

inside domain/business logic.

Provider-specific behavior belongs in connector implementations.

---

# 12. Storage Permissions

Akasha permissions must never be used to bypass permissions imposed by the underlying storage provider.

Conceptually:

```text
Effective Access
=
Akasha Permission
∩
Storage Permission
```

If the underlying storage system denies access, Akasha must not grant access through its own authorization layer.

The DMS must therefore respect both:

1. Akasha authorization
2. underlying storage authorization

---

# 13. Multi-Tenancy

Tenant isolation is a fundamental security requirement.

Users of tenant A must never be able to access:

* documents
* metadata
* Akten
* users
* groups
* permissions
* workflows
* search results
* AI data
* MCP tools
* connectors
* licenses
* subscriptions
* audit information

belonging to tenant B.

Tenant isolation must be enforced server-side.

Frontend checks are never sufficient.

Tenant context must be derived from and validated against authenticated identity.

A client-provided `tenantId` must never be trusted as the sole source of authorization.

---

# 14. Authorization

Authorization must be enforced on the backend.

The system must support an extensible authorization model including:

* users
* groups
* roles
* individual permissions
* inherited permissions
* explicit overrides
* service accounts
* API clients

Potential actions include:

* READ
* WRITE
* DELETE
* DOWNLOAD
* UPLOAD
* MOVE
* COPY
* RENAME
* SHARE
* EDIT_METADATA
* START_WORKFLOW
* APPROVE
* ADMIN

Authorization must apply consistently to:

* frontend
* REST API
* background jobs
* workflows
* AI
* MCP
* integrations
* storage operations

No alternative access path may bypass the authorization model.

---

# 15. Identity

Akasha must not be tightly coupled to one identity provider.

The architecture should support an identity-provider abstraction.

Potential providers include:

* internal users
* Entra ID
* Active Directory
* LDAP
* OpenLDAP
* OIDC
* SAML

Identity and authorization are separate concerns.

Identity answers:

> Who is this user?

Authorization answers:

> What may this user do?

---

# 16. Search

Search is a core DMS capability.

Search should eventually support:

* filenames
* full text
* metadata
* document types
* dates
* Akten
* permissions
* filters
* OCR content
* semantic/vector search
* AI-assisted search

Every search operation must respect:

* tenant isolation
* document permissions
* Akte permissions where applicable

Search must never become a side channel for accessing documents a user cannot access directly.

---

# 17. Processing Pipeline

Document processing should be modular.

A conceptual pipeline is:

```text
Storage
   ↓
Change Detection
   ↓
Document Discovery
   ↓
Text Extraction
   ↓
OCR
   ↓
Metadata Extraction
   ↓
Classification
   ↓
Indexing
   ↓
Optional Embeddings
   ↓
Optional AI Processing
```

Not every document or deployment needs every processing stage.

AI and vector processing are optional.

The core DMS must remain functional without AI.

---

# 18. AI

AI is an optional capability and must never become a mandatory dependency for the basic DMS.

Potential AI functionality includes:

* document classification
* metadata extraction
* summarization
* semantic search
* RAG
* AI chat
* validation
* workflow actions
* document operations

AI providers must be abstracted.

Potential providers include:

* customer API keys
* Azure OpenAI
* OpenAI-compatible APIs
* customer-hosted models
* local models
* future providers

Provider-specific SDKs must not leak into the core domain.

---

# 19. AI Security

AI must never have more access than the current authenticated user or service identity.

AI operations must respect:

* tenant isolation
* authorization
* document permissions
* storage permissions
* feature entitlements

Documents may contain malicious or adversarial instructions.

Document content must therefore be treated as **untrusted input**.

Prompt injection must never be able to bypass authorization.

LLM output must also be treated as untrusted.

Never allow an LLM to directly execute:

* arbitrary SQL
* arbitrary shell commands
* arbitrary filesystem operations
* unrestricted HTTP requests
* unrestricted code execution

All AI actions must go through controlled application capabilities.

---

# 20. MCP

MCP is an integration mechanism and must use the same authorization model as the normal application.

Potential MCP capabilities include:

* search documents
* retrieve document metadata
* retrieve permitted document content
* search Akten
* find document relationships
* update metadata
* move documents
* rename documents
* start workflows

Every MCP operation must:

* authenticate the caller
* establish tenant context
* authorize the operation
* enforce storage permissions where applicable
* be auditable

MCP must never provide an unrestricted backdoor into the database or filesystem.

---

# 21. Workflows

Akasha should support configurable workflows.

Potential triggers include:

* document created
* document updated
* metadata changed
* status changed
* deadline reached
* scheduled event
* manual execution
* external event
* AI validation result

Potential actions include:

* assign task
* update status
* update metadata
* notify user
* wait
* approve
* reject
* start another workflow
* perform AI validation
* call an external API

The workflow architecture should remain extensible.

Do not introduce a massive BPMN engine unless the actual product requirements justify it.

---

# 22. API

REST API is a first-class application interface.

The API should use:

* ASP.NET Core
* controllers
* REST principles
* OpenAPI

API access must use the same:

* authentication
* authorization
* tenant isolation
* audit
* entitlement checks

as the normal application.

The API may be enabled or disabled depending on deployment and licensing configuration.

Never expose an unrestricted administrative API by default.

---

# 23. SaaS and Self-Hosted

The same application architecture must support:

### SaaS

* multi-tenant
* centrally operated
* subscription-based
* tenant isolation

### Self-hosted

* customer infrastructure
* customer-controlled storage
* optional Internet access
* optional complete offline operation

Offline self-hosting is a supported scenario, not an exceptional edge case.

Do not introduce separate application architectures for SaaS and self-hosted deployments.

---

# 24. Offline Operation

The self-hosted product must be able to operate without Internet connectivity where the functionality itself does not inherently require Internet access.

Examples of functionality that may work offline:

* document management
* local/SMB storage
* metadata
* Akten
* permissions
* workflows
* search
* local processing
* local identity providers
* local AI infrastructure

Internet-dependent integrations may naturally be unavailable offline.

The application must distinguish between:

* an unavailable optional external service
* a fundamental application failure

---

# 25. Licensing and Entitlements

Technical feature flags and commercial entitlements are separate concepts.

Examples of features/limits:

```text
API_ENABLED
AI_ENABLED
MCP_ENABLED
ADVANCED_WORKFLOWS_ENABLED
MAX_USERS
API_RATE_LIMIT
AI_PROVIDER
```

SaaS entitlements may come from the subscription system.

Self-hosted deployments may use:

* online license validation
* locally stored signed license files

Offline licenses must be cryptographically signed and verifiable without Internet access.

Do not treat editable configuration files as proof of licensing.

---

# 26. Billing

Billing providers must be abstracted.

The business/domain layer should use concepts such as:

* Subscription
* Plan
* Entitlement
* License
* SeatLimit

rather than provider-specific models.

Paddle may be used initially, but the domain must not depend directly on Paddle.

Future billing providers must be replaceable without redesigning the domain model.

---

# 27. Security

Security is a cross-cutting requirement.

The system must protect:

* authentication credentials
* API credentials
* storage credentials
* OAuth secrets
* AI API keys
* LDAP passwords
* encryption keys
* licensing information
* customer document content

Secrets must never be:

* committed to source control
* logged
* exposed through APIs
* returned to clients unnecessarily

Sensitive customer document contents must not appear in normal application logs.

---

# 28. Temporary Files

Operations involving document contents may require temporary files.

Temporary files must be:

* securely created
* tenant-aware
* short-lived
* inaccessible to unauthorized users
* deleted after processing
* cleaned up after failures

The system must consider:

* path traversal
* malicious filenames
* oversized files
* archive bombs
* malicious document content

---

# 29. Background Processing

Long-running or asynchronous work should not block HTTP requests unnecessarily.

Potential background jobs include:

* OCR
* text extraction
* indexing
* embeddings
* connector synchronization
* external change detection
* workflow execution
* scheduled tasks
* notifications
* AI processing

Background jobs must preserve relevant context such as:

* tenant
* initiating identity where applicable
* operation ID
* correlation ID

Background processing must never accidentally cross tenant boundaries.

Jobs should be:

* retryable where appropriate
* idempotent where possible
* cancellable where appropriate
* observable
* resilient to temporary external failures

---

# 30. External Service Failures

External systems can fail.

Examples:

* SharePoint unavailable
* SMB unavailable
* LDAP unavailable
* AI provider unavailable
* external API unavailable
* authentication expired
* network disconnected

Optional integrations must not unnecessarily make the entire DMS unusable.

The system should distinguish between states such as:

* ConnectorUnavailable
* SyncPending
* SyncFailed
* AuthenticationRequired
* ProcessingFailed

A connector outage must not corrupt unrelated DMS metadata.

---

# 31. Resilience and Synchronization

Synchronization with external storage must be:

* incremental where possible
* tenant-aware
* retryable
* idempotent
* observable

The architecture should prefer provider-native change tracking where available.

Avoid unnecessary full re-indexing after every small change.

The system must support reconciliation between:

```text
Known Akasha state
        ↕
Actual storage state
```

The synchronization architecture must account for:

* external modifications
* external deletion
* external rename
* external move
* restoration
* concurrent modification
* temporary connector failures
* lost change notifications
* connector reconfiguration

---

# 32. Concurrency

Multiple systems may modify the same document simultaneously.

Potential actors include:

* DMS users
* external storage users
* APIs
* workflows
* AI
* synchronization processes
* third-party integrations

The application must use appropriate concurrency mechanisms.

Where supported, optimistic concurrency using:

* ETags
* version IDs
* revision IDs
* provider-specific version tokens

should be preferred.

The system must not silently overwrite newer changes.

---

# 33. Database

PostgreSQL is the required relational database.

The database should use explicit relational structures for core business data.

Prefer:

* normalized tables
* foreign keys
* constraints
* indexes
* unique constraints
* explicit relationships

Do not model the entire application as arbitrary JSON documents.

JSON may be used where it is genuinely appropriate, but core business relationships and structured data must remain relational.

Database migrations must be version controlled.

---

# 34. Tenant Isolation in the Database

Tenant isolation must be enforced throughout persistence.

Application-level tenant filtering is mandatory.

PostgreSQL Row-Level Security may additionally be used as defense in depth.

Developers must not have to remember arbitrary tenant filters for every individual query as the only security mechanism.

Database access patterns should make accidental cross-tenant access difficult.

---

# 35. Architecture

The application should initially be implemented as a **modular monolith**.

Do not introduce microservices prematurely.

The architecture should support clear module boundaries so that components can later be extracted if justified by actual requirements.

Conceptually:

```text
Akasha.Api
Akasha.Application
Akasha.Domain
Akasha.Infrastructure
```

Possible functional modules include:

* Tenancy
* Identity
* Authorization
* Documents
* Akten
* Metadata
* Storage
* Search
* Processing
* Workflows
* AI
* Licensing
* Billing
* Audit
* Integrations

---

# 36. Dependency Direction

The core domain must remain independent of infrastructure and external vendors.

Conceptually:

```text
API
 ↓
Application
 ↓
Domain

Infrastructure
implements abstractions required by the inner layers
```

The domain must not depend directly on:

* ASP.NET Core
* Entity Framework implementation details
* PostgreSQL
* SharePoint SDK
* Microsoft Graph
* Dropbox SDK
* Paddle
* OpenAI SDKs
* specific AI providers
* specific identity providers

---

# 37. Provider Abstraction

External providers must always be abstracted where replacement is a realistic requirement.

Examples:

```text
IStorageConnector
IIdentityProvider
IAiProvider
IBillingProvider
```

The business logic must depend on these abstractions rather than concrete providers.

Do not leak provider-specific models throughout the application.

---

# 38. Feature Entitlements

Every commercially restricted feature must have a clear entitlement boundary.

Examples:

* API
* AI
* MCP
* advanced workflows
* maximum users
* storage connectors
* premium processing

The backend must enforce entitlements.

Frontend feature hiding is not sufficient.

---

# 39. Auditability

Important security and business operations should be auditable.

Examples include:

* document creation
* document deletion
* document movement
* document rename
* permission changes
* metadata changes
* workflow approvals
* AI actions
* MCP actions
* administrative changes
* authentication events
* connector changes

Audit records should contain enough context to understand:

* who performed the operation
* which tenant it belonged to
* what resource was affected
* what action occurred
* when it occurred
* relevant operation/correlation information

Audit data itself must be tenant-isolated and protected.

---

# 40. Testing

Testing is mandatory for security-critical and architectural behavior.

Tests should cover at minimum:

* tenant isolation
* authentication
* authorization
* permission inheritance
* identity-provider mapping
* storage connectors
* external change detection
* document synchronization
* rename/move handling
* external deletion
* restoration
* concurrency
* search authorization
* AI authorization
* MCP authorization
* API authorization
* licensing
* entitlements
* seat limits
* workflows
* offline license validation

Integration tests should use real PostgreSQL where practical.

Architecture tests should prevent unwanted dependencies such as:

```text
Domain → Infrastructure
Domain → Provider SDK
Domain → ASP.NET
```

---

# 41. Data Protection

Customer data must be handled according to the principle of least privilege.

Do not copy or duplicate document contents unnecessarily.

Derived data such as:

* OCR text
* search indexes
* embeddings
* AI summaries
* extracted metadata

must be considered sensitive.

Access to derived data must respect the same authorization boundaries as the original document.

---

# 42. Versioning and Upgrades

Self-hosted installations must support controlled upgrades.

Database migrations must be:

* versioned
* repeatable
* deterministic
* included with the application version

Self-hosted upgrades must not inherently require an Internet connection.

Avoid requiring manual database manipulation for normal upgrades.

---

# 43. Observability

The application should provide sufficient observability to diagnose:

* connector failures
* synchronization failures
* processing failures
* workflow failures
* authentication failures
* authorization failures
* background-job failures
* external service outages

Logs must not contain secrets or unnecessary document contents.

Correlation IDs and operation IDs should be used to trace complex operations across modules and background jobs.

---

# 44. Performance

Performance optimizations must not compromise:

* tenant isolation
* authorization
* consistency
* auditability
* correctness

Caching may be used for:

* identity data
* permissions
* entitlements
* connector metadata
* configuration

Security-sensitive caches must:

* include tenant context
* use appropriate invalidation
* never return another tenant's data

---

# 45. Code Quality

Prefer code that is:

* explicit
* readable
* testable
* modular
* maintainable
* strongly typed
* dependency-injected
* observable

Avoid:

* giant classes
* giant services
* hidden global state
* magic strings for security decisions
* provider-specific logic in the domain
* duplicated authorization logic
* duplicated tenant-resolution logic
* controller business logic

Controllers should remain thin.

Business rules belong in the appropriate application/domain components.

---

# 46. Do Not Build Prematurely

Do not introduce complexity without a concrete requirement.

Avoid prematurely introducing:

* microservices
* Kubernetes
* complex event buses
* distributed transactions
* Neo4j
* multiple relational databases
* massive BPMN engines
* mandatory vector databases
* mandatory AI infrastructure
* mandatory cloud services

PostgreSQL should remain the primary relational data store.

A modular monolith is the preferred starting architecture.

---

# 47. Important Product Constraints

The following are fundamental product constraints:

1. Customer files remain customer-controlled.
2. Akasha is storage-agnostic.
3. Logical documents are independent from physical file paths.
4. External document changes are expected and must be handled.
5. Akten are logical entities, not physical folders.
6. Tenant isolation is mandatory.
7. Authorization is server-side.
8. AI is optional.
9. Internet access is not mandatory for self-hosted installations.
10. SaaS and self-hosted use the same fundamental architecture.
11. Provider-specific logic belongs behind abstractions.
12. PostgreSQL is the required relational database.
13. The application starts as a modular monolith.
14. Commercial entitlements must be enforced server-side.
15. Security must not depend on frontend behavior.
16. External changes must not silently corrupt logical document relationships.
17. Concurrent external modifications must not be silently overwritten.

---

# 48. Final Engineering Rule

Before implementing any backend or full-stack feature, verify:

1. Does it preserve tenant isolation?
2. Is authorization enforced server-side?
3. Does it work in SaaS?
4. Does it work self-hosted?
5. Does it work offline when no Internet dependency is inherent?
6. Does it preserve customer ownership/control of physical files?
7. Does it use the appropriate abstraction rather than provider-specific core logic?
8. Is licensing/entitlement enforced where required?
9. Is it testable?
10. Does it preserve the modular architecture?
11. Does it correctly handle external changes to customer storage?
12. Does it preserve logical document identity when physical files move or are renamed?
13. Does it avoid silently overwriting concurrent external changes?
14. Does it respect both Akasha and underlying storage permissions?
15. Does it preserve auditability?

If the answer to any of these questions is no, reconsider the design before implementing the feature.

