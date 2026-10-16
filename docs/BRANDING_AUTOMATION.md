# Branding automation implementation and acceptance

The completion journal already keeps independent songs-since-jingle and songs-since-sweeper counters. Only an actual `ended` occurrence changes them; duplicate, failed, skipped and interrupted attempts cannot count as completed songs. A completed jingle resets only its own counter, and a completed sweeper resets only its own counter.

`BrandingRotation` now supplies a bounded, immutable selection policy for validated branding assets. The default rules are disabled, with intervals of two and three completed songs. Both intervals allow 1–100. Stable ordinal asset IDs and **completed branding play** ordinals determine rotation; preparation attempts do not consume an asset. The policy reports due, deferred and unavailable categories separately. A missing pool preserves its debt and returns no insertion when neither category is available, allowing normal music selection to continue. Collision order is explicit; the initial policy is jingle first, then the still-due sweeper after that jingle actually completes.

The engine now accepts an explicit `IBrandingSource` and inserts approved automatic clips without advancing ordinary music rotation. **The installed host and operator controls are not yet connected to that source.** This change does not validate/import branding files, persist rules, assign legacy assets, implement scheduling, or authorize migration. Engine qualification must not be described as operator acceptance or listener-audio acceptance.

## Required programme integration

The current live-folder planner performs no file or database access in `SelectPrepared`. Preserve that property. The audio engine prepares the next decoder before the current item's completion is committed. Reading only the last committed journal counters at that point would insert branding late. Advancing counters at selection time would count failed and skipped songs and consume preparation retries.

Each engine session takes one committed `BrandingProgress` snapshot after recovery and projects its own ordered prepared terminal boundaries exactly once. It never adds asynchronous committed acknowledgements back into that projection. Lookahead predicts completion, then reconciles the chosen source against the actual ended/skipped/failed outcome. Explicit queued items and programmes keep their operator priority. A held automatic selection preserves the ordinary rotation index.

At most two branding categories and an ordinary music fallback are prepared alongside the current decoder. Both collision clips can therefore be warm even when the first clip is short. A branding decoder still preparing at the boundary yields to the already prepared ordinary song and retains its preparation. An unavailable/failed branding asset cannot consume a song or exhaust the healthy music failure budget. Failed asset IDs are excluded for the immutable pool's lifetime; a pool replacement clears that exclusion. Each role may be attempted at most once per completed-music epoch, including failed or skipped attempts. This hold is durable, does not consume a completed-play ordinal, and releases only when another music item actually completes.

Branding selection performs no file or SQLite access on the audio clock. `BrandingSource.Publish` atomically replaces a prepared immutable inventory outside that clock. A withdrawn clip that has not announced its first sample is skipped; an already audible clip finishes. Recovery resolves a held clip against the current enabled pool, including path, role and content identity. A no-longer-approved unfinished clip is discarded with explicit lower-bound history while retaining its ordinary music index.

Completed branding-play ordinals and completed-music epochs are stored with the existing deduplicated completion transaction. Additive migrations retain existing song debt and start new epochs at zero; they neither scan nor retrospectively count old history. Category-specific SQLite triggers track the existing completion updates, including writes by preceding development binaries after an application rollback. A deduplicated attempt table prevents a replayed failure from moving its old attempt into a newer music epoch. Failed/skipped attempts never consume completed-play ordinals. These migrations have not been applied to the installed database.

An unfinished held-rotation checkpoint requires `held-rotation-v1` recovery semantics. The matching browser console declares that feature. Status requests from old maintenance clients fail while such a checkpoint exists, before their service-stop admission; the stopped-journal inspector also rejects maintenance with that checkpoint. The deliberately narrow unarmed update workflow does not offer a downgrade preserving unfinished automatic clips. The original setup plan remains unchanged.

Host integration must supply validated role-specific inventories and an operator-visible warning for empty or failed pools. Importing a clip must not enable automation.

## Operator and legacy integration still required

Provide per-channel file/folder import, private audition, explicit Jingle/Sweeper classification, enable switches, intervals, collision order, a running-order preview, unavailable-asset warnings and saved readback. Importing a file must not arm automation. Legacy active/inactive state and asset role require a visible review; do not silently activate inactive legacy references or classify every old jingle as both roles.

Respect higher-priority live/program/event takeover. Carry due branding across a held automation boundary without interrupting the current song or injecting branding into a live show. Only completed music advances the song counters; advertisements and prerecorded programmes do not.

## Acceptance still required

Remote policy tests cover the six-song 2/3 sequence, both collision orders, missing pools, preparation retries, reconstruction order, frozen inventories, invalid counters and bounded inputs. The processing lab now includes real FFmpeg finite sequences for both collision orders, measured branding PCM amplitude/duration, interrupted-music reconciliation, a missing jingle, and Stop/restart inside a held jingle. These additions require a successful source-bound remote run before qualification. Remaining acceptance includes source removal; pool/rule edits during preparation; crash before/after completion commit; event/live takeover and return; worst-case decoded output continuity; and the connected mouse workflow on the broadcasting PC before any station handoff.
