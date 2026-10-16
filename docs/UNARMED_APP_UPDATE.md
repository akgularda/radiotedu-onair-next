# Reviewed application maintenance — implementation status

The initial Windows installer deliberately rejects a different setup plan. Replacing the installed application through an ad hoc file copy would bypass that ownership check. A source-assets console is an engineering preview, not a deployed engine update.

## Verified image transition foundation

`UpdateSwap` implements the file transition required by a future reviewed maintenance operation. It checks the full application manifest, every retained encoder file, and a bounded inventory before accepting an original, prepared, released, or applied layout. Unknown files, links, substituted runtime bytes, missing files, conflicting directories, and an unchanged build identity are rejected.

The candidate and retained original are siblings of the trusted installation root; their names derive from a validated operation ID. There is no recursive deletion, media access, configuration rewrite, database restore, or service command in this helper. Both images remain available after an update and rollback. A rollback retains the current database, queue and history instead of rewinding them to a former snapshot. This helper does not establish schema compatibility.

`UpdateOperationStore` pins an immutable review and flushes the requested direction before every transition. It serializes calls for the same operation, preserves rollback priority after interruption, rejects stale Apply retries after rollback, and ignores orphan partial records. The elevated installer must supply a protected record folder, hold the global setup lock, verify the stopped service and its maintenance fence before every directory rename, and set ACLs when staging. An interrupted operation is classified from the actual file identities rather than a saved progress label. The same filesystem layout can be a prepared update or a completed rollback, so the durable direction is required to interpret it.

Unit scenarios interrupt both rename boundaries in each direction, deny the second service fence, cancel after retaining the original, tamper with either image, introduce conflicting files/layouts, and verify that current data and the encoder remain unchanged. These are filesystem fixtures, not Windows service, power-loss, audio, or mouse acceptance evidence.

`UpdateAdmission` independently checks the authenticated status payload and installed configuration shape. It requires the exact eight station IDs, stopped states, explicit false running intent, empty output definitions, no owned children, and a drained journal without a current error. The configuration must retain the fixed service pipe, operator, data and encoder paths and false AutoStart defaults. Missing fields are rejected. This parser does not itself authenticate a pipe, freeze concurrent commands, or prove an unchanged configuration; those remain responsibilities of the elevated maintenance workflow.

## Windows maintenance fence under qualification

The service helper now retains the reviewed process handle, disables the isolated service start type, deletes SCM restart actions, clears the failure flag, reads the fence back, and requests a graceful stop. It waits for the exact process to exit before allowing file transitions. Microsoft documents that an already queued recovery restart cannot be canceled; disabling the service also prevents that queued restart from reclaiming application files. The former broadcasting service is never addressed. Resuming after interruption can complete a partially installed fence. Restoration reinstates the exact delayed-auto and 5/15/60-second policy.

A guarded, disposable CI setup probe exercises stale-process rejection, the actual SCM disabled-start refusal, repeated fencing, interrupted partial fencing, restored authenticated eight-station status and unchanged configuration bytes. It uses an explicit encoder-listing test double with zero outputs, not audio. It refuses execution outside Windows GitHub CI or when the old broadcasting data root exists. This source is pending fresh CI qualification; no operational update control or target-PC maintenance has executed it.

Intent polling now holds a retained per-operation snapshot sidecar lock only while reading the bounded committed record. Atomic replacement holds the same short lock, preventing Windows sharing and temporary-name races across helper/console processes. Operator readers need only read permission on the guard; ACL failures, missing guards for existing records, malformed JSON and review mismatches still fail. A separate store instance concurrently polls monotonic complete revisions and retained rollback direction.

References: [SERVICE_FAILURE_ACTIONS](https://learn.microsoft.com/en-us/windows/win32/api/winsvc/ns-winsvc-service_failure_actionsw), [ChangeServiceConfig2 queued recovery behavior](https://learn.microsoft.com/en-us/windows/win32/api/winsvc/nf-winsvc-changeserviceconfig2w).

## Remaining gates before exposing update controls

- An elevated helper must bind the review to the existing protected ownership/configuration, package bytes and journal schema contract.
- This first maintenance workflow must require all new stations explicitly stopped, no requested running intent, and zero configured outputs. It is not a rolling update mechanism for on-air stations.
- Integrate the operation/intent journal with resumable staging, metadata backup while holding the engine writer lock, service-only maintenance/recovery fencing, post-start IPC readback, and targeted rollback.
- Run the new console and helper from a complete external package so installed executables and their working directories do not block the rename.
- Provide System review/apply/resume/rollback controls and actual Windows SCM integration tests, then mouse acceptance on the broadcasting PC.

Until those gates pass, no update endpoint or operational button calls this helper. The existing installed engine remains in place and the former broadcast program keeps source ownership.

## Package review and stopped-journal inspection under qualification

The next source adds a read-only external-package review binding the original operator, protected installation/configuration/ownership receipts, exact running service process and start time, both verified manifests and unchanged encoder runtime. The replacement must declare the explicit schema-one application-only contract; current data is retained on rollback. Installed/candidate/previous and new-data paths cannot be used as package sources. This review has no file/service mutation and remains an internal helper pending System integration.

A separate early Host inspection mode reads only the fixed new journal or a fixed reviewed-operation snapshot path. It verifies the actual schema_info version, exact eight persisted stopped intents, preserved enabled states and SQLite quick_check without opening EventJournal, setting pragmas, checkpointing WAL or changing schema/state. Regression cases include committed running intent still in WAL, unsupported schema, missing/unknown channels and byte preservation. The disposable service fixture holds writer.lock during actual packaged inspection after service exit.

Fresh CI is required for these additions. They do not expose a complete update/apply workflow or establish production readiness.
