# Branding automation implementation and acceptance

The completion journal already keeps independent songs-since-jingle and songs-since-sweeper counters. Only an actual `ended` occurrence changes them; duplicate, failed, skipped and interrupted attempts cannot count as completed songs. A completed jingle resets only its own counter, and a completed sweeper resets only its own counter.

`BrandingRotation` now supplies a bounded, immutable selection policy for validated branding assets. The default rules are disabled, with intervals of two and three completed songs. Both intervals allow 1–100. Stable ordinal asset IDs and **completed branding play** ordinals determine rotation; preparation attempts do not consume an asset. The policy reports due, deferred and unavailable categories separately. A missing pool preserves its debt and returns no insertion when neither category is available, allowing normal music selection to continue. Collision order is explicit; the initial policy is jingle first, then the still-due sweeper after that jingle actually completes.

This policy is **not yet connected to programme playback or operator controls**. It does not validate audio files, persist rules, assign legacy assets, implement scheduling, prove transitions, or authorize migration. Unit qualification of this policy must not be described as working automatic branding or listener-audio acceptance.

## Required programme integration

The current live-folder planner performs no file or database access in `SelectPrepared`. Preserve that property. The audio engine prepares the next decoder before the current item's completion is committed. Reading only the last committed journal counters at that point would insert branding late. Advancing counters at selection time would count failed and skipped songs and consume preparation retries.

The production planner therefore needs an immutable, journal-consistent completion snapshot plus an ordered projection of preceding in-flight completions. Deduplicate projected occurrences against that snapshot. Decoder failure, Skip, preparation replacement and restart must invalidate the affected prediction without counting an incomplete song. Keep music rotation position separate from inserted branding and explicit queued items. Do not introduce synchronous SQLite/file reads or discard the prepared music fallback on the sample clock.

Completed branding-play ordinals are now stored with the existing deduplicated completion transaction. An additive schema migration retains existing song debt and starts the new ordinal epoch at zero; it neither scans nor retrospectively counts old history. Category-specific SQLite triggers track the existing completion updates, including writes by the preceding development binary after an application rollback. Duplicate, skipped and failed attempts do not update those category fields, so they cannot consume rotation. The migration and reopen/retry cases require remote test qualification; this has not been applied to an installed database.

Programme integration must use these ordinals rather than an in-memory attempt counter. Pool changes must publish a replacement immutable inventory outside the audio callback. Retired, missing, changed or invalid assets cannot remain eligible. Empty pools must keep ordinary music available and produce an operator-visible warning.

## Operator and legacy integration still required

Provide per-channel file/folder import, private audition, explicit Jingle/Sweeper classification, enable switches, intervals, collision order, a running-order preview, unavailable-asset warnings and saved readback. Importing a file must not arm automation. Legacy active/inactive state and asset role require a visible review; do not silently activate inactive legacy references or classify every old jingle as both roles.

Respect higher-priority live/program/event takeover. Carry due branding across a held automation boundary without interrupting the current song or injecting branding into a live show. Only completed music advances the song counters; advertisements and prerecorded programmes do not.

## Acceptance still required

Remote policy tests cover the six-song 2/3 sequence, both collision orders, missing pools, preparation retries, reconstruction order, frozen inventories, invalid counters and bounded inputs. The following require engine and real-audio tests after integration: lookahead versus commit lag; Skip/decode failure at either side of a boundary; very short clips; source removal; pool/rule edits during preparation; crash before/after completion commit; queue/music position; event/live takeover and return; and decoded audio continuity. Finally perform the connected mouse workflow on the broadcasting PC before any station handoff.
