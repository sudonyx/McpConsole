# Repeatable local setup

Stop manually started copies of the services. From McpConsole:

```powershell
.\local.ps1 setup
.\local.ps1 start
```

Setup saves ordinary settings, prompts for model/OAuth credentials, and generates
matching internal service keys. Secret input is hidden; blank input keeps a stored
secret. Credentials already in the setup terminal's environment can be imported
without printing them. OAuth app credentials are optional when working CRM grants
are already stored, but are needed for future guided reauthorization.

Start validates the profile, builds the applications, starts DecisionApi, Harness
and StateMachine in the background, waits for readiness, verifies connections and
reviewed read scopes, and opens workflow chat. Stored CRM grants are reused.
Missing grants trigger operator browser consent before chat; no LLM can initiate
that management flow. A missing authorization-policy grant stops startup: the
launcher never creates, exports or approves policy grants. `/exit` or Ctrl+C stops
the processes started by this invocation.

If local script execution is restricted, use .NET directly without changing
machine execution policy:

```powershell
dotnet run --project .\LocalSetup\LocalStack -- setup
dotnet run --project .\LocalSetup\LocalStack -- start
```

## Saved settings

The default profile is `demo`. Files live outside the repositories:

```text
%LOCALAPPDATA%\KyoPrep\LocalStack\
  keys\                         Windows-protected configuration encryption keys
  profiles\demo\
    profile.json                repository root and reviewed chat-config path
    decision\appsettings.Local.json
    harness\appsettings.Local.json
    workflow\appsettings.Local.json
    chat\appsettings.Local.json
    secrets\                    encrypted settings, one file per service
    data\                       new databases when existing ones are not selected
    logs\                       bounded service logs
    processes.json              process ownership metadata while running
```

Edit ordinary values in each `appsettings.Local.json`; generated files use flat
.NET keys such as `ExecutionAuthorization:BaseUrl`. Never put secrets there.
Rerun setup to change secrets. It preserves internal keys and synchronizes peer
URLs from each API's `Urls` setting; `check` detects disagreement. Changing the
model endpoint's origin requires configuring its key again rather than silently
sending the stored key to another host.

Setup preserves reviewed `chat.local.json`, policy files and catalog bindings.
It selects an existing `Harness.Api/harness.development.db` and existing
`%TEMP%\mcp-chat-workflow.db` when present, otherwise profile data paths. It prints
the workflow database selected. Databases are never copied, moved or deleted.
An existing TEMP database is retained in place; deliberate backup/migration to
permanent storage remains separate work. Keep the existing harness OAuth store
and its encryption keys too.

Profiles are configuration choices, not tenant isolation: they may reference the
same reviewed files or databases. Do not run profiles concurrently against a
database unless that service explicitly supports the deployment.

## Secret handling

Configuration secrets use a separate ASP.NET Core Data Protection key ring
protected by Windows DPAPI for the current user. Encrypted files are bound to
profile and service; writes use atomic replacement after disk flush. This key
ring is separate from the harness's existing protected OAuth grant store.

DecisionApi receives its policy-service key; Harness its management/policy keys;
StateMachine its ingestion key; chat its model/harness/workflow keys and optional
OAuth onboarding credentials. Services load their own protected settings. Keys
are not placed on command lines, copied into ordinary JSON, or distributed to
every process's environment. They remain plaintext in the service's memory.

The launcher strips stale application settings and unrelated terminal secrets
from child environments, retaining an OS/.NET/proxy allowlist plus non-secret
profile/home/instance identifiers. Standalone applications retain environment
overrides. All processes still run as one Windows user: this is not isolation
against a compromised process under that account. Production needs separate
identities/roles and a suitable vault. Losing protection keys requires restoring
them or creating a new profile and reentering credentials.

## Commands

```powershell
.\local.ps1 check
.\local.ps1 status
.\local.ps1 stop
.\local.ps1 start -NoBuild
.\local.ps1 start -NoChat
.\local.ps1 connect
.\local.ps1 connect -Capability hubspot.demo-companies
.\local.ps1 setup -Profile another
.\local.ps1 self-test
```

Use status/stop/connect in another terminal while start is attached. `-NoBuild`
requires previously built outputs. `-NoChat` starts the APIs and stays attached
until stopped. The launcher refuses occupied ports, never adopts unrelated
processes, and checks PID plus creation time before stopping owned processes.
Startup/authorization failure shuts down services started by that invocation;
dependency exit ends chat. Bounded logs live under the profile and may contain
application diagnostics, so protect them.

The APIs expose `/local/ready`; Harness retains its API-key boundary. Probes
check service/profile/instance identity. Decision readiness checks policy-file
availability and structure. The launcher checks registered reads against existing
grants, but every execution still receives a fresh external policy decision.
Readiness is not authorization. Model billing/quota and actual CRM responses are
checked when used, without speculative model/tool requests during startup.

## Independent services and validation

Each application supports its own optional repository-local
`appsettings.Local.json`. With `KYO_LOCAL_PROFILE` set it also loads profile
settings and protected secrets. Environment variables (and API command-line
options) can override these for standalone development. The launcher supplies a
clean, consistent environment. Missing protected keys fail startup in profile mode.

The API repositories include a self-contained version-1 configuration bootstrap,
keeping standalone builds independent of the chat repository. The setup self-test
checks its copies match the canonical source; a shared package can replace that
later. Core decision, execution and state-transition behavior is unchanged. The
profile does not configure every separate Orion/Asteria/qualification client.

Self-tests cover protected storage, service key matching/distribution, model-key
origin binding, configuration precedence, policy preservation, child-environment
isolation and process ownership. An isolated smoke test used real service
binaries, synthetic credentials/policy and separate databases to verify readiness,
status, owned shutdown, failure rollback, and full startup into workflow chat
followed by `/exit`. It made no real CRM or model requests.

Validation on 19 November 2026: 204 Harness tests, 281 Meridian tests and 34
relevant StateMachine tests passed, together with console/setup self-tests and
the isolated lifecycle checks. The PowerShell wrapper was checked in Windows
PowerShell 5.1. The previously known Asteria process-test failures are outside
these targeted checks; the full StateMachine suite was not rerun for this change.
