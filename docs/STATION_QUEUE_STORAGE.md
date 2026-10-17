# Station queues and operator controls

Station-scoped storage is connected to the library, host, service pipe and browser. This integration is under qualification; it has not been installed or accepted with a mouse on the broadcasting PC. The global historical `StudioStore` queue is not adopted.

Each channel has a revision and at most 128 unfinished entries. Append and reorder/remove commands share FULL-WAL transactions with payload-bound command receipts. A lost response can be retried with the same ID without appending twice. A stale revision cannot overwrite another change; a rejected transaction does not retain a receipt. Removed and consumed rows remain available as references for immutable playback history.

Actual start, interruption and terminal events update queue state in the same transaction as their event and checkpoint. The entry retains its current occurrence owner: a delayed terminal from an interrupted attempt cannot consume a later restart. Completed, skipped and failed entries cannot be resurrected by a delayed start. Recovery retains unfinished content, consumes a recovered durable Skip and preserves music rotation. The engine's existing recovery path starts the new attempt at sample zero.

Published queue views are immutable, bounded and read without SQLite on the programme clock. Event acknowledgement follows both FULL-WAL commit and publication of the committed view. Monotonic revisions prevent delayed continuations from replacing a newer view. Readers must initialize their views after reopening the journal.

Live reorder/remove uses short in-memory reservation leases. Current, ready-to-play and uncommitted audio stays locked; other entries can move without restarting the station or encoders. The stopped-channel unfinished recovery entry also stays protected. Only the compatibility storage API retains its stopped-channel restriction. Application maintenance rejects unfinished queues or queued checkpoints; incompatible console readers cannot obtain maintenance admission while such state exists. Empty additive tables and consumed history do not block older readers.

## Library and playback

The browser submits catalog identity, live-folder revision, edit version and source stamp. It cannot submit an arbitrary media path. A source file is guarded outside the journal writer; catalog identity is checked again in the command transaction. A receipt retry is acknowledged even after the source changes, without reauthorizing or appending it again.

Selection uses a published sorted catalog with compact identity arrays. No file access, full-catalog copy or SQLite read occurs on the programme clock. Missing/removed files wait in the saved queue while other validated music continues. A changed file, changed metadata or replaced live folder requires removing the old entry and queueing the current validated item; the UI explains that action. Returning unchanged media becomes eligible again automatically.

Queued music follows the same independent jingle-every-two and sweeper-every-three completed-song counters as rotation. Queued programmes/advertisements retain their explicit priority in the engine; their library classification and scheduling workflows remain unfinished. The normal rotation cursor is preserved across queued items. If an unfinished queued source is unavailable on restart, its checkpoint can be deferred while its pending entry and previous actual attempt remain linked. An unannounced withdrawn source is deferred without recording a fabricated audio start.

## Operator flow

1. Connect the compatible engine in System and select a channel.
2. In Library, choose/import music and wait for validation. Use **Add to queue** on an item.
3. On Air shows the saved order and automatic next item. Use **Up**, **Down** or **Remove** on unlocked queued entries. Current and ready-to-play entries stay protected.
4. If a response is lost, use **Retry same request**. The exact request is retained before submission; a browser that cannot save/read back the reminder sends no command.
5. Use **Refresh queue** to read committed state. Channel changes and stale/unavailable samples disable controls rather than displaying another channel's queue as editable.

Removing a queue entry does not remove a library asset. Item metadata is frozen when queued and actual occurrences remain immutable.

## Acceptance still required

The added synthetic test scenario independently decodes AAC-LC, FDK HE-AACv2 and Ogg FLAC while exercising live append/reorder, retry, locked-current rejection and branding cadence. Exact-source results must be inspected before describing that scenario as passed. Storage/model tests alone do not establish decoded audio or browser operation.

Actual connected mouse acceptance, integrated process-kill/EOF/Skip/withdrawal timing races, late file changes at prepared boundaries, worst-case resources and the full 24/72-hour gates remain open. Microphone, programme classification/schedules, conditional recording and complete reporting/migration workflows remain separate requirements. No production-readiness claim is made.

The broadcasting PC retains its existing live service. No queue operation in this change was applied to a real installation, database or media folder. All AI features remain deferred.
