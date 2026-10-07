# Manage library — slice 1 design

## Intent and entry point

Group whole-library tasks by what a person wants to do: move a library to
another Nostos, protect a SelfHosted installation, or recover the previous
library after a replacement. The dedicated page lives at `/settings/library`.
Settings → Library & data keeps one short, capability-aware summary card and a
single **Manage library** button that opens it. E-reader access and highlight
import remain ordinary Library & data settings.

## What each deployment shows

The page is available even when only one group applies. It follows the current
server capability flags rather than inferring features from the deployment name:

| Deployment and flags | Manage library content |
| --- | --- |
| SelfHosted with `supportsLocalBackupConfiguration` | Existing Backup and Backup History cards under “Protect this installation”. |
| SelfHosted with `supportsLibraryMigration` | Existing “Move your library” card under “Move to another Nostos”. Safe-activation actions remain governed by `supportsSafeActivation`; the existing import flow owns any previous-library recovery action. |
| Cloud with `supportsLibraryMigration` | Existing “Move your library” card under “Move to another Nostos”; safe-activation actions remain governed by `supportsSafeActivation`. No SelfHosted backup cards are introduced. |
| Cloud without `supportsLibraryMigration` | Existing “Data export” card under “Move to another Nostos”. |

If both migration and local-backup capabilities apply, both groups appear. A
group with no applicable capability is absent. The e-reader cards stay in
Library & Data regardless of this page.

## Behaviour and compatibility

The page composes the current backup, history, portable-export, and library
transfer UI. Its entry button only navigates; opening the page never starts,
restores, imports, replaces, or deletes anything. Existing confirmation and
safe-activation gates remain the authority for destructive actions. Preserve
the current card and action `data-testid` values so their contract tests keep
covering the same controls after they move. Do not change transfer/backup
components, APIs, archive formats, or action ordering in this slice.

## Later slices

The first-run import entry should navigate to this page with import selected;
the duplicate welcome-screen flow can then be removed. A persistent transfer
indicator that returns to this page while an upload is running is also separate
work, along with any additional cleanup enabled by consolidation.
