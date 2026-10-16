# Reviewed Windows application updates

This workflow updates only the unarmed new installation. It requires the exact eight channels explicitly stopped, no running intent or owned children, a healthy drained journal, and zero configured outputs. It does not release production mounts or change the former broadcasting service. It is not a rolling update mechanism for on-air stations.

## Operator workflow

1. Extract the complete new Windows package to its own local folder. Keep the entire package, including its setup and console components.
2. In the current console, choose System → Close console. Broadcasts continue independently in their service. Open setup/OnAirNext.Setup.exe from the new extracted package; a different console version must be closed first.
3. In System → Application update, choose Review this package. Preparation runs as the original Windows operator and changes no service, configuration or database. On a filtered Windows token, reading the LocalService process may require Windows administrator approval. A bounded read-only helper uses that same operator account; another administrator account is rejected. This approval does not apply the update. It verifies the current and new manifests, ownership/configuration receipts, exact running process and compatible schema-one contract.
4. Review the two build identities and package location. Choose Apply update, read the confirmation, and approve Windows administrator access. The helper stages a separate candidate, retains the original image, fences only the new service's SCM recovery, and waits for its exact process to exit.
5. The helper holds the new playback writer lock while validating persisted stopped intent and taking a verified metadata snapshot. It never copies the old live database or media. It flushes the update direction before either directory transition, then restores canonical service startup/recovery and authenticates the stopped eight-channel status.
6. Read the completion receipt and reconnect the engine. A lost acknowledgement can be retried with the same operation without restarting an already verified service.

An approved elevated helper continues if the console closes. Reopen the same complete package contents to recover its saved review and protected progress. A moved package is accepted only when its full reviewed manifest and file bytes still match; no review/configuration editing is needed. Resume update uses the same operation. Roll back application restores the original verified application image while keeping the current database, queue, library and immutable receipts. It never automatically rewinds the database to the snapshot. Retained rollback intent rejects a stale Apply retry; another update needs a fresh review.

Keep the external package, both application images and protected update records available. No recursive cleanup is performed by the updater. A released layout with no active installation remains fenced until the same reviewed operation resumes or rolls back; an unknown/tampered layout is never adopted.

## Protection and evidence scope

The elevated helper holds the global setup lock. Records and guard files receive Administrator ownership before publication. The original operator has read access for status polling; it cannot replace elevated records. Readers and atomic intent replacement share a short retained sidecar lock so the UI cannot observe Windows rename/share races. Cancellation is bounded and wrong hashes, missing guards and malformed records are rejected.

The new source adds a disposable Windows setup round trip, same-operation retry, rollback from a relocated unchanged package, stale Apply rejection, and a real service command committed after the snapshot and retained after application rollback. This fixture uses an explicit encoder-listing test double and a synthetic replacement manifest identity with the same compiled binaries. It tests filesystem/SCM/current-data behavior; it does not establish compatibility with every older executable, real audio, actual-PC UAC/mouse acceptance, physical power loss or production readiness.

This new execution/UI integration is pending fresh CI and actual mouse acceptance. The previous foundation passed 285 unit cases, 49 service checks and 37 setup checks in run 38911582084, plus scoped real audio and codec qualification. Those results apply to source 55a88ca6 and do not qualify the newer execution/UI code.

## Recovery and IO refinements pending fresh qualification

Newer source detects the retained installation lease using a read-only probe after a console reopen, so another in-progress maintenance operation disables duplicate Apply/Rollback controls. The helper briefly retries a shared-file acquisition rather than racing the UI probe. Physical candidate and metadata copy fallbacks are paced to 2 MiB/s with a pooled buffer and below-normal process priority; existing matching files and immutable hard links need no content copy.

Additional software checks prepare the review through the authenticated external console route, reopen a relocated console to recover the same review, verify rollback availability by manifest identity, hold the real installation lease to confirm duplicate controls are disabled, and read back rollback completion with stale Apply disabled. These refinements require fresh CI and do not replace actual mouse/UAC acceptance.

The broadcasting-PC mouse review on 2026-10-16 rejected normal-token access to the LocalService process before any service or image change. A new approval fallback retains the full process/image/start-time checks. It binds private request/result metadata to the original operator, exact package manifest and unique request, rejects arbitrary input paths before reading, and never overwrites a completed result. Added unit and disposable setup checks are pending fresh CI. The elevated runner can test the handoff contract, not a real filtered-token UAC dialog; actual-PC mouse approval remains required.
