# Station queue storage foundation

This change adds station-scoped storage and attempt ownership. It is **not yet connected to the active host, service pipe, library or browser**. It does not enable queue controls or change music selection. The global historical `StudioStore` queue is not adopted.

Each channel has a revision and at most 128 unfinished entries. Append and stopped-channel reorder/remove commands share FULL-WAL transactions with payload-bound command receipts. A lost response can be retried with the same ID without appending twice. A stale revision cannot overwrite another change; a rejected transaction does not retain a receipt. Removed and consumed rows remain available as references for immutable playback history.

Actual start, interruption and terminal events update queue state in the same transaction as their event and checkpoint. The entry retains its current occurrence owner: a delayed terminal from an interrupted attempt cannot consume a later restart. Completed, skipped and failed entries cannot be resurrected by a delayed start. Recovery retains unfinished content, consumes a recovered durable Skip and preserves music rotation. The engine's existing recovery path starts the new attempt at sample zero.

Published queue views are immutable, bounded and read without SQLite on the programme clock. Event acknowledgement follows both FULL-WAL commit and publication of the committed view. Monotonic revisions prevent delayed continuations from replacing a newer view. Readers must initialize their views after reopening the journal.

Reorder and remove currently require a stopped channel. The unfinished recovery entry and its position are protected. Application maintenance rejects unfinished queues or queued checkpoints; incompatible console readers cannot obtain maintenance admission while such state exists. Empty additive tables and consumed history do not block older readers.

## Remaining integration and acceptance

- Authorize selected catalog identity, live-folder revision, edit version and source stamp through the library adapter; never accept an arbitrary browser media path.
- Publish eligible queue items alongside live-folder availability. Withdrawn, changed or unavailable sources must not be selected again accidentally.
- Arbitrate live edits against current, prepared and uncommitted audio; wire refresh and reservation release without blocking the programme clock.
- Preserve the explicit queue/programme priority and approved jingle/sweeper cadence policy.
- Add service commands and browser append, ordering, removal/read-back and unknown-acknowledgement retry controls.
- Qualify the integrated planner with real loopback audio, crash/Skip/EOF races, folder changes and mouse acceptance. Storage tests alone do not establish those results.

The broadcasting PC retains its existing live service. No queue operation in this change was applied to a real installation, database or media folder. All AI features remain deferred.
