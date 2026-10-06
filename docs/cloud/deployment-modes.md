# Nostos deployment capabilities

Nostos has one product, one domain model, and one Angular customer application.
The public `Nostos.Backend` executable runs SelfHosted. The official hosted service
uses a separate private executable that composes the same public `Nostos.Product`
and frontend source.

## Product contract

The backend publishes the server-authoritative capability manifest:

```http
GET /api/runtime/capabilities
```

SelfHosted reports:

```json
{
  "deploymentMode": "SelfHosted",
  "requiresAuthentication": false,
  "canConfigureAiProvider": true,
  "managedAi": false,
  "managedVoiceTranscription": false,
  "usesCloudStorage": false,
  "supportsLocalBackupConfiguration": true,
  "usageMeteringAvailable": false
}
```

The same public contract defines the capabilities used by the private hosted
executable:

```json
{
  "deploymentMode": "Cloud",
  "requiresAuthentication": true,
  "canConfigureAiProvider": false,
  "managedAi": true,
  "managedVoiceTranscription": true,
  "usesCloudStorage": true,
  "supportsLocalBackupConfiguration": false,
  "usageMeteringAvailable": true
}
```

Capability names describe product behavior. They do not expose infrastructure
vendors, tenant identifiers, or provider credentials. Hosted API implementations
and their configuration live in the private Nostos-Cloud repository.

## Public SelfHosted composition

The public executable always registers:

```text
SQLite + local filesystem media + local backup/restore + customer-configured BYOK
```

It does not load hosted auth, billing, tenant provisioning, managed-provider,
object-storage, or operator-recovery implementations. Starting a public clone
does not require private repository access or hosted credentials.

## Frontend rule

The Angular application is built once from public source and is shared with the
private hosted executable at the pinned public commit. Frontend components use
`DeploymentCapabilitiesService`; they do not infer deployment from a hostname or
use a private frontend fork.

## Optional hosted browser integration

A hosted executable may opt in with `Nostos:HostedBrowserIntegrationEnabled=true`
(environment variable `Nostos__HostedBrowserIntegrationEnabled`). The capability
`hostedBrowserIntegrationEnabled` defaults to `false`. SelfHosted always reports
`false` and never loads host code, even when this setting is present.

When both Cloud mode and this capability are reported, the app asynchronously
loads one classic script from the fixed same-origin route
`/api/runtime/hosted-browser-integration.js` per document. The host must map this
route to JavaScript, outside product entitlement restrictions, and install:

```javascript
window.nostosHostedBrowserIntegration = {
  sessionChanged: function (session) {
    // session = { authenticated: boolean, accountId: string | null }
    // Resolve any provider identity via your authenticated server endpoint.
  }
};
```

The callback runs after the host script's load event when a session is known,
then when the authenticated account changes. Session readiness and script
readiness can arrive in either order. Repeated identical session reads do not
repeat the callback; a delayed earlier session read cannot restore context after
an explicit refresh or invalidation. The payload omits display names, email,
provider IDs and credentials. It is a lifecycle hint, never authorization: the
server must validate authentication and resolve the account for every
customer-specific request, with responses excluded from shared caches.

Sign-out emits `{ authenticated: false, accountId: null }` before the BFF form
submission. A same-origin `/api` HTTP 401 also invalidates the session; 403 and
external-provider errors do not. These hints do not monitor idle cookie expiry
or another tab's session: the host must revalidate through its authenticated
endpoint as needed (including focus/visibility changes), reject stale in-flight
identity responses, and clear provider context or perform a clean document
reload on session loss or account switching. Host code must not keep a previous
customer's notifications or engagement active in another session.

The loader runs at app bootstrap, including signed-out and authenticated
recovery entry screens, without waiting for product readiness. The host must
avoid provider requests for anonymous users or missing configuration/bindings.
Script load failures and thrown callbacks do not affect entry, navigation or
sign-out; a failed installation is not retried in the same document. Host code
owns its provider initialization/update/reset lifecycle and must isolate its
asynchronous errors. It must not grant product access or update billing state
from browser callbacks. Account management remains at the configured account
management URL.

The public product supplies the contract and loader only. Provider SDKs,
configuration, customer resolution, operational dashboards and hosted
acceptance evidence remain owned by the hosted executable. No host script route
or provider implementation is supplied by public SelfHosted.
