# Reviewed application maintenance — implementation status

The initial Windows installer deliberately rejects a different setup plan. Replacing the installed application through an ad hoc file copy would bypass that ownership check. A source-assets console is an engineering preview, not a deployed engine update.

## Verified image transition foundation

`UpdateSwap` implements the file transition required by a future reviewed maintenance operation. It checks the full application manifest, every retained encoder file, and a bounded inventory before accepting an original, prepared, released, or applied layout. Unknown files, links, substituted runtime bytes, missing files, conflicting directories, and an unchanged build identity are rejected.

The candidate and retained original are siblings of the trusted installation root; their names derive from a validated operation ID. There is no recursive deletion, media access, configuration rewrite, database restore, or service command in this helper. Both images remain available after an update and rollback. A rollback retains the current database, queue and history instead of rewinding them to a former snapshot. This helper does not establish schema compatibility.

`UpdateOperationStore` pins an immutable review and flushes the requested direction before every transition. It serializes calls for the same operation, preserves rollback priority after interruption, rejects stale Apply retries after rollback, and ignores orphan partial records. The elevated installer must supply a protected record folder, hold the global setup lock, verify the stopped service and its maintenance fence before every directory rename, and set ACLs when staging. An interrupted operation is classified from the actual file identities rather than a saved progress label. The same filesystem layout can be a prepared update or a completed rollback, so the durable direction is required to interpret it.

Unit scenarios interrupt both rename boundaries in each direction, deny the second service fence, cancel after retaining the original, tamper with either image, introduce conflicting files/layouts, and verify that current data and the encoder remain unchanged. These are filesystem fixtures, not Windows service, power-loss, audio, or mouse acceptance evidence.

`UpdateAdmission` independently checks the authenticated status payload and installed configuration shape. It requires the exact eight station IDs, stopped states, explicit false running intent, empty output definitions, no owned children, and a drained journal without a current error. The configuration must retain the fixed service pipe, operator, data and encoder paths and false AutoStart defaults. Missing fields are rejected. This parser does not itself authenticate a pipe, freeze concurrent commands, or prove an unchanged configuration; those remain responsibilities of the elevated maintenance workflow.

## Remaining gates before exposing update controls

- An elevated helper must bind the review to the existing protected ownership/configuration, package bytes and journal schema contract.
- This first maintenance workflow must require all new stations explicitly stopped, no requested running intent, and zero configured outputs. It is not a rolling update mechanism for on-air stations.
- Integrate the operation/intent journal with resumable staging, metadata backup while holding the engine writer lock, service-only maintenance/recovery fencing, post-start IPC readback, and targeted rollback.
- Run the new console and helper from a complete external package so installed executables and their working directories do not block the rename.
- Provide System review/apply/resume/rollback controls and actual Windows SCM integration tests, then mouse acceptance on the broadcasting PC.

Until those gates pass, no update endpoint or operational button calls this helper. The existing installed engine remains in place and the former broadcast program keeps source ownership.
