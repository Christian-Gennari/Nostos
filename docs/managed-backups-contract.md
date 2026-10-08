# Managed backups customer contract

The product exposes a provider-neutral, read-only list for hosts that advertise
`supportsManagedBackups: true` from `GET /api/runtime/capabilities`. The
capability is false by default, is never inferred from deployment mode, and is
forced false for SelfHosted. The Settings card is hidden unless the capability
is explicitly true.

## Public route

`GET /api/managed-backups` requires an authenticated user and accepts no tenant,
account, or backup selector. The host must derive the current tenant from its
server-side authenticated context and return only that customer's backups.
There are no create, download, delete, or restore operations in this slice. A
host without a managed-backup provider returns `501 Not Implemented` through
the product's default `IManagedBackupCatalog`.

Successful responses use this shape:

```json
{
  "retentionDays": 14,
  "backups": [
    {
      "id": "c7e2d932-f32d-4a22-8ca1-06a750176f9f",
      "createdAtUtc": "2026-10-07T00:30:00Z",
      "archiveBytes": 1024,
      "mediaBytes": 2048,
      "state": "completed"
    }
  ]
}
```

`archiveBytes` is the archive object's size. `mediaBytes` is the amount of
protected media represented by that backup; it is shown separately because
hosts may deduplicate media objects between backups. Only completed backup
records belong in this list. The current stable state value is `completed`; it
does not claim a restore has been exercised or independently verified.

`retentionDays` describes the retention window for these listed nightly
backups. It must come from the host's actual backup retention policy. It does
not describe library replacement copies, database point-in-time recovery, or
disaster-recovery copies, which can have different windows. The Settings text
uses “up to” because lifecycle expiry does not promise an exact deletion time
for an individual entry.

## Cloud composition

The shared frontend calls only `/api/managed-backups`. The Cloud host registers
an `IManagedBackupCatalog` adapter that delegates to the same account-scoped
recovery service used by its existing `GET /api/cloud/recovery/backups`
endpoint. That endpoint currently returns `CloudOperationalBackupSummary`
values with `BackupId`, `CreatedAtUtc`, `ArchiveBytes`, and `MediaBytes`; map
those fields to the public shape and set `state` to `completed` for each listed
completed backup. Supply `retentionDays` from the Cloud nightly archive policy
(currently 14 days).

The adapter must preserve the recovery service's current tenant scoping and
account ownership checks. Cloud's fallback policy requires authenticated
product access, but the public route has explicit authorization metadata so
that SelfHosted also protects it. When mapping shared product endpoints, Cloud
must set `ManagedBackupsAuthorizationPolicy` to
`CloudAuthPolicies.ProductAccess`; the adapter must resolve the catalog using
the same authenticated account context. Never implement this route by querying
operator status or by accepting a tenant identifier from the browser.
