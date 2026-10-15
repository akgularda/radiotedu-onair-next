# RadioTEDU OnAir Next development plan

Baseline dated 15 October 2026; scope approved and operating requirements refined on the same date. The [current operating and delivery plan](docs/OPERATING_PLAN.md) controls the confirmed 18-output roster, live-folder behavior, restart-from-beginning recovery, microphone-only recording trigger and English-only interface. Its delivery order supersedes the preliminary phase order below.

Build a new Windows broadcast application with a new audio engine, native operator interface, database, installer, and maintenance tools. It must handle RadioTEDU's existing broadcast needs on the HP Z220 with 8 GB RAM while making every operational and administrative task available through the UI. The current program continues broadcasting throughout development.

The existing application supplies requirements, migration inputs, and examples of failures to prevent. The new application will run independently after installation and will not require the old backend, dashboard, scripts, or database to operate.

## Development location and reference evidence

The new project lives in `RadioTEDU-OnAir-Next` beside the original release archives. The inspected reference is in `RadioTEDU-OnAir-UX-Dev/RadioTEDU-OnAir-New`. Its 852 source files match the archive SHA-256 manifest. The reference release is 1.1.3, commit `a59d1d867094f63c1a087182bc361a7a5bd00c01`, on `safe-program-transitions-20261012`. GitHub's `main` branch has a different, older commit; it is not the sole requirements baseline. A newer installer branch also exists and needs review when inventorying packaging behavior.

Inspection confirmed an existing Python/FastAPI engine, HTML/JavaScript operator wall, Windows WebView2 shell, microphone and show components, scheduling, campaigns, recovery, and a hash-chained music ledger. The current HTML exposes 19 navigation destinations. The new product needs a simpler information architecture and independent implementation.

The current reporting implementation has specific gaps to avoid:

- `record_completed_play` uses finish time for `broadcast_at`; a start time must not be inferred from that field as if directly observed.
- The GitHub publishing script aggregates plays and uses a fixed six-station mount map. It is insufficient for a complete chronological report across the actual roster.
- Existing daily export paths use UTC dates; the new reporting calendar must use Europe/Istanbul.
- Combined OnAir/Juke exports apply 10,000-record limits in several paths. Complete annual exports must stream all matching records without silent truncation.
- Completion-based records require a separate design for interrupted plays, crashes, skips, and live contributions.

The supplied October 13 and October 15 results establish useful short-term evidence, not 24-hour continuity or a permanent repair of the remote server. Historical runbooks contain different output counts. The actual persisted roster on the broadcast PC remains authoritative.

## Product scope and operating rules

The first production release must cover library imports, playlists, queues, rotation, live microphones, programs, special events, advertisements, sweepers, output configuration, daily and annual reports, account permissions, backups, recovery, installation, and updates through the UI.

Daily work must use plain language, native file and folder pickers, clear progress, and reversible actions. Administrator workflows may need Windows elevation, but must not ask users to edit configuration, run commands, or repair database rows. A feature is complete only when its UI persists the change, reads it back, reports failure clearly, and provides recovery.

Preserve the actual enabled stations, mount names, codecs, bitrates, audio quality, library assignments, scheduling semantics, and explicit startup authorization. Do not synthesize `/radiotedu`, `/radiotedu1`, `/en`, `/fr`, or their variants. EN/FR broadcasts remain owned by their independent system. Dormant outputs stay dormant. An unavailable listener endpoint must never automatically disable a healthy source.

Maintain a capability inventory with an old workflow, new screen, service command, persisted value, permission, acceptance scenario, and migration rule for every supported task. Optional Voting, Juke, remote contribution, and AI integrations receive explicit entries so no operational dependency disappears during the rewrite. Local AI inference is optional and outside the audio path.

## Proposed implementation

Use C# with a current .NET LTS runtime and a custom WPF desktop interface. .NET 10 is the preferred starting candidate. WPF provides Windows controls, accessibility, scalable typography, and native integration without an embedded browser. This is an architectural proposal; its resource advantage must be measured.

The runtime decision has an early compatibility gate: identify the actual Windows 10 Pro version and test installation, audio drivers, microphone permissions, startup, and rendering on that build. Microsoft's current .NET 10 supported OS table does not explicitly list ordinary Windows 10 Pro 22H2. Running successfully and having vendor-supported OS coverage are separate questions. Do not select an almost-expired runtime simply because the old shell uses it; document a working deployment and maintenance path before locking the stack.

Keep one solution with a small number of clear components:

| Component | Responsibility | Failure boundary |
|---|---|---|
| Engine host | Continuous program clock, station state, mixing, scheduling, encoder supervision | Independent of the desktop window and imports |
| Desktop app | Operator workflows, forms, queue editing, search, monitoring | Closing or crashing the window does not stop automation |
| User audio agent | WASAPI microphone capture and headphone monitoring in the signed-in Windows session | Loss of capture initiates a defined return to automation |
| Background jobs | Import analysis, reports, GitHub publishing, backups | Bounded queues and resource budgets outside audio callbacks |
| Codec workers | Proven FFmpeg decoding and persistent encoding | A failed codec/output process is reaped and recovered independently |

Use a native, typed command boundary over Windows named pipes with access controls. The engine validates permissions and commands; the UI never owns the broadcast state. Keep domain logic testable independently from WPF and from live audio hardware. Avoid a dependency on the old Python runtime, a browser server, a cloud database, or a distributed deployment.

Use SQLite through Microsoft.Data.Sqlite for the new schema. Maintain a schema version, tested migrations, indexes, short transactions, and verified online backups. Keep configuration separate from immutable play history. Never use the old live database as the development database.

## Continuous audio and recovery

Build the audio proof before the complete application. The fundamental unit is a continuously advancing station timeline, not a succession of encoder processes tied to songs.

1. Decode each station's current program source once into bounded, pooled PCM blocks. Prepare a bounded portion of the next source early. Do not load entire tracks or the library into RAM.
2. Mix music, microphone, advertisements, and sweepers into one timed station program. Use sample positions and monotonic elapsed time for continuity; calendar time is for scheduling and reporting.
3. Keep encoders and Icecast source sessions alive across track boundaries. A track decoder ending is not a signal to restart an output.
4. Track decoded, mixed, accepted, buffered, and drained audio separately. Advance content ownership exactly once using the program timeline and boundary acknowledgements. Never replay a completed item because an output still holds buffered audio.
5. Give each output its own bounded queue, timeout, and recovery state. A slow output cannot hold a common buffer indefinitely or stop its sibling outputs. Define the affected branch's latency and resynchronization policy explicitly; finite buffers cannot preserve unlimited outage history.
6. Bound allocations and work in the audio loop. Metadata updates, report generation, file scans, database maintenance, and GitHub calls never execute in an audio callback.
7. Use one recovery owner for each output/session. Identify owned child processes, close and reap them, verify cleanup, and reconnect with capped backoff. Never kill unrelated server sessions or create overlapping source owners.
8. Persist a playback occurrence ID, queue state, and checkpoint. Distinguish an intentional stop from a crash. An intentional stop preserves queue order and follows the documented resume-from-beginning behavior; recovery must not silently use that policy to repeat completed tracks.
9. If the next file cannot be decoded, move to a prepared, approved fallback with an explicit incident record. A failed optional integration must not stall the station.

Show source connection state separately from decoded listener delivery. Label unknown or stale observations. Silence detection alone must not trigger whole-station restarts: legitimate quiet passages, planned pauses, network loss, and stalled audio require different evidence and responses.

A single engine host is the starting design for low overhead. Its restart remains a shared failure boundary. Test that boundary explicitly and reconsider station host isolation only if measurements justify the additional memory and operational complexity.

## Operator interface

Create a recognizable RadioTEDU broadcast desk with restrained branding, neutral surfaces, readable type, strong alignment, and clear state colors. Build a consistent native control system. Avoid decorative gradients, glass effects, excessive panels, animated backgrounds, and technical status dumps in daily workflows.

Use seven primary destinations:

| Destination | Main workflows |
|---|---|
| On Air | Station overview, now playing, remaining time, next items, microphone, cue, live controls |
| Library | Batch import, search, metadata, collections, playlists, retirement, restore |
| Schedule | Weekly clock, shows, special events, exceptions, conflict preview |
| Ads and Sweepers | Assets, campaigns, break placement, cadence, dates and limits |
| Reports | Hourly play history, daily archive, annual export, missing metadata, upload status |
| System | Output settings, audio devices, startup, users, storage, integrations, updates |
| Recovery | Health evidence, incident history, backups, restore, diagnostic export |

The On Air screen keeps the active station and broadcast status visible. Its main area shows the playing item and ordered queue; a compact live strip contains microphone level, live state, music ducking, headphone cue, and return-to-automation. Multi-station overview remains one action away. Stopping all broadcasts is visually separated from routine queue controls.

Support a minimum 1366 by 768 display, 100 to 150 percent scaling, keyboard navigation, visible focus, non-color status cues, large live controls, and readable countdowns. All application-owned interface text, setup, help and errors must be English. Preserve original content metadata, including Turkish artist names and titles.

Every content operation must have an ordinary form or direct manipulation alternative. Queue ordering supports both drag-and-drop and Move Up/Down. Preview always routes to a selected cue device; prevent accidental preview on the program bus. Show exactly which station a command affects. Preserve unsaved edits when health updates arrive.

Routine actions need immediate feedback and Undo where feasible. Stopping a station, immediate live takeover, restoring a database, or changing an active destination needs a clear consequence and deliberate confirmation. Safely retry idempotent commands; use unique command IDs for operations that must not repeat after a lost response.

## Content import and management

The import workflow is: select files or folder, choose station and content type, review metadata and duplicates, import, then view the result. Support large batches, progress per file, partial success, cancel/resume, and retry of failed items without importing successful files again.

Copy through temporary files and publish each asset atomically after validation. Validate actual audio decoding rather than only the extension. Extract artist, title, duration, format, and available rights metadata. Preserve original audio quality; analyze loudness or waveforms only when needed and in the background. Do not transcode the entire library as a prerequisite for use.

Detect byte-identical duplicates by hash, separately identify potential alternate editions, and allow explicit Keep Both. Use folder/file names as visible suggestions when tags are absent. Never invent artist or track metadata. Provide an inbox for unresolved fields and bulk editing with a preview.

Assign stable asset IDs independent of file paths. Removing an asset retires it from future selection and moves it to reversible trash when appropriate. Protect the playing asset and explain future schedule references before removal. Preserve all historical metadata snapshots. Provide UI restore and an administrator retention policy.

Special events include a reusable playlist, dates, applicable stations, optional metadata override, conflict rules, and a preview of the return to the normal schedule. Advertisements require an explicit campaign and schedule; importing an ad never makes it eligible automatically. Sweepers support per-station frequency, position, and repeat rules. Validate collisions between live shows, fixed breaks, and special events before publishing the schedule.

## Live broadcasting

Provide microphone device selection, a private test, input meters, gain, mute, ducking, a limiter, and a prominent live indicator. Make soft takeover at the next boundary and immediate takeover distinct. End Live returns to a visible automation target.

The user audio agent handles Windows interactive-session audio; the unattended engine must not depend on a service being able to open a user's microphone. Closing the main window can leave the audio agent active. Device removal, agent failure, or user logoff must follow a tested fallback policy and produce an incident record.

Support recorded programs and configured external live contributions through the same scheduling model. Inventory existing guest/show capabilities before specifying an isolated remote-input adapter. Remote input failures cannot own the automation timeline indefinitely. The UI must show whether the contribution is armed, previewing, or actually on air.

## Daily and annual reporting

Record what entered the program, with delivery evidence recorded separately. A queued song is not a broadcast play, and a connected source is not proof that every listener heard it.

Create an immutable occurrence ID at actual program start. Persist an event sequence for start, end, skip, interruption, recovery, and correction. Snapshot artist/title and rights metadata at the occurrence, so later catalog edits cannot rewrite history. Persist events through a durable local journal/outbox with replay and deduplication. Crash recovery must surface uncertain intervals rather than silently claiming complete delivery.

Each detailed play record includes occurrence ID, station ID/name, content type, artist, title, optional album/version/ISRC, UTC start/end, local start/end with offset, local hour, actual duration, completion status, program/presenter when known, and delivery evidence. Quality variants refer to the same station occurrence; 16 outputs must not turn one song into 16 plays. Keep ads and sweepers in operational history and filter them explicitly from song totals.

Use Europe/Istanbul for daily boundaries and hourly display. For example, local 15 October starts at 21:00 UTC on 14 October. Preserve exact timestamps, not only an hour label. A cross-midnight play belongs to its start-day occurrence list; time-based summaries apportion duration across the applicable periods. Repeated broadcasts remain separate occurrences.

At a configurable time, initially 00:10 Istanbul time, generate the previous local day's detailed CSV, hourly summary, and checksum manifest atomically. A still-running occurrence can delay finalization or receive a linked later revision. Mark provisional and final reports visibly. On startup and after outages, enumerate and repair every missing reporting date, including empty days with an explicit zero-record manifest.

Provide a Reports screen with date/station filters, all 24 hours, unresolved metadata, report generation, download, Retry Upload, and annual export. Stream complete exports from the database with cancellation and progress. Never apply a UI page limit to a report. Preserve Turkish characters and make CSV fields safe for spreadsheet use. Test with more than 10,000 events and more than a year's history.

For live music that is not a catalog asset, provide an operator set list and start/end cue or import workflow. Distinguish operator-declared metadata from automatically observed metadata. Arbitrary external audio cannot be promised automatic song identification without a separate recognition system.

Annual reporting can consolidate daily events and totals through the UI. Keep a mapping layer for the broadcaster's required submission template. The annual format is a product configuration requirement to confirm from the actual reporting template; do not infer legal submission rules from the old CSV.

## GitHub archive

The active authenticated account is `akgularda`. It remains the account used for repository work. `radiotedu` is a personal user account, not an organization. The inspected OnAir source repository grants `akgularda` read-only access, while several other RadioTEDU repositories grant write access. No dedicated play-report repository was visible in the accessible inventory. The old script's default checkout name does not prove that a corresponding accessible repository exists.

The dedicated private `akgularda/radiotedu-broadcast-reports` repository and separate private application repository `akgularda/radiotedu-onair-next` now exist. The report repository currently contains labeled synthetic test archives. Do not repurpose unrelated RadioTEDU repositories and do not switch the active account to create repositories under `radiotedu`.

Archive reports under `reports/YYYY/MM/YYYY-MM-DD/`, with detailed plays, hourly summary, and a manifest containing schema version, timezone, event count, checksum, completeness, and revision. Upload only reports and manifests; omit audio, credentials, database backups, absolute media paths, and unnecessary personal information.

The application publishes in the background using an idempotent outbox. The daily report identity plus revision/checksum prevents duplicate uploads. Record the confirmed commit SHA and verify the uploaded manifest before showing success. Network loss, authentication failure, conflicting remote changes, or rate limits leave the local report intact and queued. Retry with backoff, show the reason, and never force-push or restart broadcasting.

Development uses the existing `akgularda` login without exposing its token. The broadcast installation needs its own stored repository credential appropriate to the service identity; developer keyring access is not automatically available to a Windows service on another PC. Provide a UI connection/test/credential renewal workflow, protected Windows storage, least necessary repository permissions, and an expiry warning. Browser sign-in depends on a supported GitHub authentication registration; a protected token-entry form is the fallback, not a configuration-file edit.

## Resource budgets and storage

The HP Z220's CPU and 8 GB RAM are the commissioning target. The GT 710 is not required for audio processing. A newer development PC cannot establish that the complete workload fits the broadcast PC.

Initial engineering targets, to validate rather than advertise as achieved:

| Measurement | Initial target |
|---|---|
| Engine host, excluding codec children | At most 300 MiB steady working set |
| Desktop UI | At most 250 MiB with ordinary library and queue views |
| Entire application including all codecs and audio agent | Prefer at most 2 GiB; revise only with measured evidence |
| Typical total CPU at the full commissioned roster | Below 60 percent, retaining measured headroom for live input and transitions |
| Ordinary command acknowledgement | 95th percentile below 200 ms locally |
| Library and report display | Paged/virtualized, no all-library rendering |
| Long-run behavior | No unbounded buffers, process accumulation, or sustained memory growth after warm-up |

Compare the new engine to a safely isolated run of the reference using the same media, roster, codecs, and measurement method. Include encoder child memory and import/report load. If HE-AAC/FLAC cost prevents the budget, optimize implementation and scheduling before changing quality. Preserve the existing approved encoder capabilities and verify their packaging requirements.

Start import analysis with one background worker and throttle it while broadcasting. Do not create permanent full-length PCM copies of all media. Use bounded caches, modest meter refresh, virtualized lists, incremental library scans, and incremental reporting. Avoid repeated full-history hashing or cumulative report regeneration in normal playback.

The source and planning documents may remain in this Google Drive workspace. Put active databases, locks, credential stores, PCM buffers, and temporary work under a dedicated local SSD data root such as `%LOCALAPPDATA%/RadioTEDU/OnAirNext-Dev` for tests and `%PROGRAMDATA%/RadioTEDU/OnAirNext` for installation. SQLite WAL expects local cooperating processes; a synchronized working directory adds avoidable contention and conflict risks. Sync completed exports/backups after atomic creation.

Choose media roots through the UI. Prefer the SSD for active random access where capacity permits and HDDs for managed media/archive after measurement. Treat the reported 13.5 GB free on H: as limited capacity: estimate imports and recording growth, expose retention and low-space alerts, and do not silently select that disk for expanding caches. Verify actual space on the target PC during commissioning.

## Local test origin and continuity evidence

Install a dedicated Icecast instance on this development machine during the engine proof. Bind it to loopback, select unused ports, use generated test credentials, allow enough source slots for the actual mirrored roster, and disable public directory advertising. Keep distinct application data, Windows service names, user configuration, output destinations, and media fixtures. Launch tests only after checking these boundaries.

Use synthetic audio with identifiable transitions and counters alongside representative music, short advertisements, sweepers, long programs, and known quiet sections. Decode listener streams continuously and compare timing against expected boundaries. Keep summaries and short incident windows rather than unlimited recordings on a small disk. Concurrent decoding itself consumes CPU, so profile the measurement overhead separately.

Exercise normal transitions, crossfades, skips, empty/corrupt media, file removal, microphone takeover and loss, import during playout, report generation, failed GitHub upload, blocked output, delayed output, encoder crash, engine restart, offline boot, network loss/recovery, Icecast restart, duplicate commands, midnight, and year rollover.

Local Icecast validates the client and recovery design. It cannot establish that the production TinyIce server's historical stuck-session behavior is repaired. Test or reproduce that server condition separately before claiming that incident resolved. Icecast documents source timeouts and fallback behavior; a fallback is not evidence that the intended main program remained uninterrupted.

Run an initial 24-hour full-roster soak followed by a 72-hour release-candidate soak, including scheduled live transitions, batch imports, report rollover, and failure injection. Record exact versions, roster, codecs, duration, PCM underruns/drops, decoded gaps, repeats, reconnects, report completeness, and resource peaks. Separate expected outage windows from unexplained failures. Acceptance requires no unexplained restart, repeat, program gap, or missing event; passing a finite run is evidence for that run, not a zero-downtime guarantee.

## Development phases and completion gates

| Phase | Deliverable | Completion gate |
|---|---|---|
| 1. Requirements and compatibility | Capability inventory, Windows/runtime check, new solution layout, performance measurement method | Every current operational need mapped; installation path works on target Windows build |
| 2. Engine proof | New headless station engine, local Icecast, persistent output, bounded queues, next-item preparation, event journal | Controlled transition tests and isolated output failures pass at one station and then the full actual roster |
| 3. Operator design | Native interactive On Air, Library import, Schedule, and Reports flows using clearly labeled fixture data | Representative nontechnical operators complete scripted tasks; 1366 by 768 layout and keyboard operation verified |
| 4. Working daily automation | Connected UI, library/playlist management, schedules, metadata, safe retirement, persisted settings | Complete tasks through the UI with restart/read-back checks; no placeholder controls |
| 5. Live and broadcast content | Microphone/audio agent, shows, special events, campaigns, sweepers, required integration adapters | Ownership, cue isolation, takeover/return, ad timing, and input-failure scenarios pass |
| 6. Reporting and maintenance | Complete event reports, private GitHub archive, UI setup, backups, diagnostics, installer/update rollback | Offline/catch-up, partial play, duplicate, midnight, annual volume, upload verification, and restore tests pass |
| 7. Commissioning | Full-roster resource comparison, 24/72-hour evidence, migration rehearsal, operator trial | Quality preserved, agreed capacity budget met, complete workflows and reports verified, rollback rehearsed |

The first vertical slice must play several tracks, transition continuously, accept a microphone segment, insert an ad/sweeper, and produce its timestamped report against local Icecast. Build this before expanding the remaining UI. No schedule estimate is credible until this slice establishes audio and codec feasibility on the target hardware.

## Installation and migration

Ship a Windows installer and a clearly labeled test build. Include dependency detection, service registration, startup policy, file permissions, audio-device setup, encoder capability checks, and repair through guided UI workflows. Use distinct service and data names so the old program remains installed and usable. Package updates in versioned directories and avoid patching files beneath a running engine.

Build a read-only migration importer for a verified snapshot of the old database/configuration. The operator sees a preview of stations, output profiles, paths, library counts, schedules, users, and reporting history. Preserve old occurrence provenance and hash chains; import historical records as historical evidence rather than rewriting their times or silently combining incompatible ledgers. Never guess missing timestamps or copy live source credentials into logs.

During testing, imported outputs are stopped and explicitly remapped to loopback test destinations. Production source credentials and startup authorization are enabled only in the controlled commissioning workflow. Two applications must never compete for the same live mount.

Rehearse a cutover and rollback on the test origin, then plan the production handover around an agreed window. True uninterrupted migration requires a working relay/fallback or server-assisted handover; without one, measure and disclose the expected cutover gap. Keep the old installation and database available, but do not run both against the same production mounts. Test restoring the original source ownership and producing a coherent history across the handover.

## Sustainable maintenance

Keep a documented module map, state transitions, database schema, media ownership policy, and reporting schema beside the source. Use locked dependency versions, reproducible builds, dependency/license inventory, focused domain tests, desktop workflow tests, and real codec/stream integration checks. Test the failures that originally interrupted broadcasts rather than relying on screenshot or mocked-health tests.

Every release must document changes, compatibility, backup/migration behavior, rollback, test evidence, and resource results. Give operators a built-in quick guide and an actionable diagnostic export that redacts credentials. Schedule runtime/dependency review as a maintenance practice; no unattended runtime or application update may interrupt a broadcast.

An isolated engine milestone and local Icecast laboratory now exist, with private source and synthetic-report repositories under akgularda. Durable recovery/application storage remain unfinished working changes; native UI, service installation, full HE-AAC testing and production cutover are not complete. See README.md for the committed milestone and docs/OPERATING_PLAN.md for the next delivery gates. The current installation remains untouched.

## Technical references

- [Microsoft WPF overview](https://learn.microsoft.com/en-us/dotnet/desktop/wpf/overview/) describes the Windows desktop framework.
- [Microsoft .NET support policy](https://dotnet.microsoft.com/en-us/platform/support/policy/dotnet-core) identifies current LTS servicing periods.
- [Official .NET 10 supported operating systems](https://github.com/dotnet/core/blob/main/release-notes/10.0/supported-os.md) supplies the deployment support matrix.
- [Microsoft.Data.Sqlite overview](https://learn.microsoft.com/en-us/dotnet/standard/data/sqlite/) documents the proposed database provider.
- [SQLite write-ahead logging](https://www.sqlite.org/wal.html) describes its local shared-memory requirements and checkpoint behavior.
- [Icecast configuration](https://www.icecast.org/docs/icecast-latest/config_file/) documents listener/source limits, binding, source timeouts, and fallback mounts.
- [GitHub repository token permissions](https://docs.github.com/en/rest/authentication/permissions-required-for-fine-grained-personal-access-tokens) documents the permissions for report publishing.
