# ADR 0013: Storage mode from a watched file; validated on every reload; last good mode kept on error

- **Status:** Accepted
- **Date:** 2026-10-17
- **Spec:** §7.3

## Decision

- The mode lives in a separate JSON file (`COUNTERSHIFT_STORAGE_MODE_FILE`, default `storage-mode.json`), loaded with `reloadOnChange: true` and bound to `IOptionsMonitor<StorageModeOptions>`.
- `StorageModeProvider` turns options into an **immutable** `StorageMode`, validates it, and swaps it atomically. Each request reads the mode **once**, so a switch never splits one request across two modes.
- **Invalid at startup → the process exits** (there's no last good mode). **Invalid on reload → rejected:** the last good mode stays, an error is logged with the reason, and `cs.storage.mode.reload_failures` is incremented. A typo in a ConfigMap must not take down traffic.
- `/admin/storage-mode` shows the active mode, its version and load time, and live parity totals.

## Kubernetes notes (applied in M9)

- Mount the ConfigMap as a **directory**. A `subPath` mount never receives updates, and environment variables never change in a running pod.
- The kubelet refreshes mounted ConfigMaps periodically (about a minute by default), and that delay **is the rollback time**. The drill in M8/M10 measures it.
- ConfigMap updates swap a symlink (`..data`). .NET's file watcher can miss that, so set `DOTNET_USE_POLLING_FILE_WATCHER=true` in the pod.
- Replicas pick up a change at slightly different moments. Every intermediate combination must be safe, which is why the plan changes one knob at a time and the validator enforces "never read what isn't written".

## Alternatives considered

| Option | Why not |
|---|---|
| Environment variables | Require a restart, so a rollback is a redeploy. |
| Mode stored in the database, changed by an admin API | Works and propagates faster, but now the migration control plane depends on the store being migrated, and it needs auth. |
| A feature-flag service | The right answer at company scale. Here it's another moving part to run on one box. |

## Verified

Against a real file on disk (Windows file watcher) the mode went v1 → v2 → v3 with no restart, and an unsafe edit was rejected with v3 kept. Unit and end-to-end tests drive the same reload path through a configuration source that fires `OnReload()`.
