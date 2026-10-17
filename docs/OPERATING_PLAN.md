> **Latest user decision — browser UI:** The native prototype was rejected. Build and test the browser console; earlier native-only UI clauses are superseded. See [browser interface decision](BROWSER_UI_DECISION.md). All broadcast safety and commissioning gates still apply.

# RadioTEDU OnAir Next operating and delivery plan

Updated 15 October 2026 from the approved feature scope and the operator's answers. This is the controlling product plan where it refines earlier planning defaults. Implementation proceeds with durable recovery first, followed by the native interface and connected workflows. The present request produces the plan; it does not perform a production installation or cutover.

Subsequent user authorization: the broadcasting-PC agent is to finish the build, learn the actual legacy media/branding configuration, configure and migrate through UI workflows **one station group at a time**, and retire the old service only after all required outputs are verified. Follow [the broadcasting-PC handoff](BROADCAST_PC_HANDOFF.md) for that later deployment. No production change is authorized on this development host.

## Confirmed operating requirements

| Requirement | Decision |
|---|---|
| Stations | All eight existing RadioTEDU stations: Classic, Lofi, Radio, Cazz, Rock, Energize, Situation and Main Character; retain stable identities and existing destination names from the verified migration snapshot. |
| Outputs | Eight AAC-LC primary outputs, **HE-AAC for all eight stations**, and the two existing FLAC outputs: **18 outputs**. Adding the two HE-AAC branches is an approved expansion for the new program. Do not mutate the legacy program's roster. |
| Quality | Preserve AAC-LC 192 kbps, HE-AACv2 64 kbps and the existing Ogg FLAC settings unless the operator changes them through the new UI. **libfdk_aac is required for HE-AAC**. No codec downgrade to pass a test. |
| Music source | One live music folder per station. Its files are the operator's authoritative catalog. Any subfolders belong to that same station; folder structure does not create stations or categories. |
| Normal programming | Automatic shuffled music without daily curation. Manual queue/playlists remain available. |
| Night programming | Prefer higher-BPM music at night; do not impose a hard eligibility filter. |
| Branding | A jingle after every two songs; a sweeper after every three songs. They are distinct content types and counters. |
| Special days | Optional event programming for dates such as October 29. Normal operation requires no fixed show schedule. |
| Live | Usually microphone over music; occasionally prerecorded programs. |
| Power/process recovery | Start without a signed-in user. Restart an unfinished item from its beginning; do not deliberately replay a completed item. |
| Network recovery | Keep retrying independently for every configured output, with bounded backoff and resource use. Network return must not require an operator or reset the station playlist. |
| History | Record every song occurrence automatically. CSV is the required export format; retain exact timestamps and daily private GitHub publication. |
| Audio recording | **Only while the microphone is on air.** Record the station's program mix, including music under speech. Do not record ordinary music-only automation. |
| Integrations | Preserve the previously approved optional capabilities, although none is used today. Disable them by default and keep them outside the normal workflow. |
| Language | All application-owned UI, setup, errors, help, reports' column names and recovery messages are English. Preserve original artist/title and other content text. |
| Isolation/account | Separate project, service identity, data and test mounts; current installation remains untouched. Continue using **akgularda**. |

## Service and recovery behavior

The independent Windows engine service owns all station automation, event history, schedules, output supervision and background work. Automatic delayed startup and Windows service recovery are configured by the new installer. Startup never waits for the desktop window, user login, a microphone, GitHub, AI, a network connection or a complete media analysis scan. A local persisted enabled/stopped state determines which stations should start; an explicitly stopped station stays stopped across boot. Newly imported stations remain disarmed until the commissioning workflow authorizes them.

Commissioning also verifies the PC's existing power-on-after-AC-restoration behavior: a Windows service can start after Windows boots, but cannot itself turn on a powered-off computer. Preserve the working legacy power-return setup. Forced-process termination is an early recovery test; a controlled cold-boot/power-loss rehearsal on an isolated target is a separate release gate.

The native UI is a controller. Closing/crashing it must not stop automation. Microphone capture and private headphone cue run in an interactive-user audio agent; unattended service playback cannot depend on a user's microphone session. Losing that agent removes the live contribution, closes the recording segment and returns the station to automation. The engine remains authoritative after the UI reconnects.

Use a local named-pipe boundary with explicit service/client ACLs, caller authorization and role checks; the prototype's current-user-only pipe is not sufficient for a service running under another identity. Protect credentials for the actual service identity. Validate media/recording access under that identity during setup. Unattended playback cannot depend on a drive mapping or cloud client that appears only after an interactive login; use accessible local volumes or explicitly tested service-accessible network locations.

Recovery policy is **restart the unfinished item at its beginning**, not sample-position resume. Preserve checkpoints for ownership, observed progress, troubleshooting and report duration, but do not use the saved offset as the restart position. An unfinished song, jingle, sweeper, ad or prerecorded program receives an interrupted attempt and a linked recovery attempt. Repeated actual audio must remain visible in history; an intentional recovery restart must not be concealed by deduplication. A committed completed occurrence is never selected again merely because of a restart. Ordinary future rotation may select that song later as a distinct occurrence.

Persist station desired state, current queue owner, pending order, rotation state, jingle/sweeper counts, active event position and command receipts. A transition transaction coordinates occurrence state, queue completion and next ownership. An accepted command must be durably recoverable, and a retried command ID must not perform a second Skip, append or scheduling action. Validate command IDs against their original station and payload. Fix the unfinished implementation's schema, ownership and acknowledgement behavior before connecting a UI to it.

Keep disk commits, scans, hashing and report work outside the audio callback. Use bounded asynchronous persistence and prepared transition intents, distinguish intended/started/completed events, and acknowledge durable state only after commit. Audio leaving a process and a disk commit cannot be one physical atomic operation: a crash in that interval must yield an explicit uncertain boundary, never a fabricated successful play or a claim that nobody could hear a repeated sample.

An output outage does not restart the current track. Its bounded queue discards stale transport backlog when necessary and rejoins the current program. Healthy siblings continue. Retries remain enabled indefinitely with capped backoff; there are no endless tight loops or unbounded queues. A missing drive is retried without replacing the station catalog with an empty scan. An unavailable item tries another eligible song or approved station fallback; the UI explains the issue when opened.

Do not kill arbitrary remote sources to work around a busy mount. Enforce one local source owner per destination and test old-session release with the owned local Icecast. Remote-server stuck sessions require their own authorized server-side resolution; client retries do not prove that defect is fixed.

## Live folder behavior

The music folder is the operator's catalog; the internal SQLite index is an automatically maintained cache plus settings/history, never something the operator edits. Folder assignment uses a native picker in Library. External copy/paste into the folder and UI batch import feed the same index. UI upload copies into the selected station folder with temporary-file staging and atomic publication; there is no hidden second compulsory media library.

Watch file changes and reconcile periodically, including after watcher overflow, restart, drive return and incomplete writes. Wait for a file to become stable and decodable before admitting it. Renames retain identity when confidently matched; replacement content is revalidated. Hash/metadata analysis is incremental and throttled, not a full-library startup prerequisite. Preserve manual queue order and do not play every newly detected song immediately.

Removing a file excludes it from future selection. Protect already-open playback where possible and validate the prepared next asset before taking ownership. A failed or offline folder scan does not retire all files. UI Remove uses reversible trash with restore, and warns about schedule/playlist references. Exclude trash, staging, recording and application-data directories from discovery. Do not follow reparse-point loops. Overlapping/shared roots must be explicit and visible.

Support batch progress, partial successes, cancel/resume, retry of failed imports, exact duplicate detection, metadata suggestions, bulk preview/apply/undo and unresolved metadata. Extract existing tags and offer filename suggestions without inventing artist/title. Changes to catalog metadata do not rewrite prior play snapshots.

Jingles, sweepers, advertisements and prerecorded programs use separate UI-managed collections/folder assignments. A jingle must not silently count as a song just because it sits in the music root; the operator can classify it and autoplay respects that type. The ordinary station music folder remains a single folder-based catalog.

## Music selection and cadence

Use a shuffled rotation with durable state and repeat avoidance rather than repeatedly selecting a song with no history. Keep manual queue choices first within the applicable programming policy. Show why a song is eligible/excluded, and allow approved artist/title separation rules without requiring operators to prepare playlists every day.

Night energy is an optional soft weight among eligible songs, relative to the station's own BPM distribution. Unknown BPM and quieter songs remain playable. Analyze BPM in the background; an unanalysed library is immediately usable. Suggested initial night hours are **22:00–06:00 Europe/Istanbul**, editable in the UI; these hours are a proposed default, not a user-specified broadcast commitment.

Count completed music items only. Jingles, sweepers, advertisements and recorded programs do not increment song counters. A skipped/failed song does not advance the default cadence. Every two completed songs makes a jingle due; every three makes a sweeper due. Each counter resets only when its own insertion is committed. Persist due state so restarting cannot lose or double-insert an item. If that collection is empty or broken, continue music and expose an actionable notice without repeatedly blocking.

Example using independent counters, before optional ads/manual intervention:

```text
Song 1 → Song 2 → Jingle
Song 3 → Sweeper
Song 4 → Jingle
Song 5 → Song 6 → Jingle → Sweeper
```

Both become due after song 6. The proposed default is **jingle, then sweeper**, with the collision policy editable in Automation. The UI preview shows the resulting sequence rather than hiding the collision. During microphone-on-air segments, defer automatic branding insertions by default until a safe music boundary after the microphone closes; preserve due counts. The operator may explicitly trigger a soundboard item live.

## Special days and prerecorded programs

Provide a Special Day wizard within Schedule: name, date(s), annual recurrence, target stations, chosen media/playlist, ordering/shuffle, opening jingle, jingle/sweeper cadence and start/end behavior. No event becomes active merely because it was imported or because its name resembles a holiday.

The default event presentation is an opening jingle followed by event songs and recurring jingles. Sweepers are optional per event; the normal station defaults remain two-song jingles/three-song sweepers. Offer Finish Current Song and Exact Time as explicit start policies; the user did not require everyday hard timed starts. Preview which content wins on overlaps and how ordinary automation returns. Preserve the normal queue/counters and the active event's position separately. On recovery, evaluate whether the event is still in its calendar window before restarting its unfinished item.

Prerecorded programs are a first-class asset type: import, audition, Play Next/Play Now or schedule, clear return to automation at the end, and the same restart-from-beginning recovery rule. Their embedded songs need an optional supplied set list/cue sheet; the program must not claim automatic recognition of arbitrary mixed audio. Playing a prerecorded program with the microphone off does **not** trigger audio recording.

## Microphone and recording

Mic On Air is the recording trigger, not opening the application, selecting a device or private microphone testing. Start only when the microphone contribution is accepted into the chosen station program. Record that program mix so the spoken segment and music underneath are preserved together. Stop at the end of the microphone's on-air contribution; no continuous background music recording or automatic music pre/post-roll. Mic mute/close or input loss ends the contribution and recording segment. A reopened microphone starts a new linked segment. For multiple on-air microphones/guests, the policy applies while at least one enabled live microphone contribution is on air.

The trigger is the on-air control state and valid input session, not voice-activity detection: pauses between words must not fragment a recording. Remain recording through quiet passages while the microphone remains on air.

Use one recording per station, not per quality output. Persist station, start/end timestamps, segment ID, format, duration, cause of ending and linked play occurrences. Suggest **AAC-LC 192 kbps**, segmented at microphone-session boundaries and at the hour for long sessions. Reuse suitable encoded program packets before network transport when this can be isolated cleanly; otherwise measure the cost of a dedicated recording encoder. Neither disk writes nor a stalled recorder may backpressure streaming.

Use crash-tolerant segments and startup repair/finalization. After a crash, finalize/recover the old segment where possible, label damage/missing intervals and leave the microphone off until the user/audio agent reestablishes live input. Automation restarts unattended without reopening a physical microphone unexpectedly. Source-network loss does not stop a local live recording if the microphone contribution continues.

Recording duration now depends on actual live use, not 24-hour station uptime. At 192 kbps, one hour of microphone-on-air program audio is approximately **86.4 MB** plus container overhead; eight stations each live for one hour produce about **691 MB**. The prior 116 GB/seven-day continuous-recording estimate no longer describes this requirement.

Recording folder, maximum size, free-space reserve, retention and deletion policy are UI settings. Seven days is a proposed starting retention value, not an approved continuous-recording policy. Choose storage/retention in setup; show the consequences before enabling age-based deletion. Protect recordings from ordinary library scans. On disk-full or recording failure, continue streaming, retain valid segments and show a prominent incident. Never upload audio recordings to the GitHub report repository.

## Every-song CSV and private archive

Capture each song's actual program start, end, artist, title, station, occurrence/attempt identity, content type, UTC and Istanbul timestamps, actual duration and complete/skipped/interrupted/recovered/uncertain status. Keep a per-hour view and original metadata snapshots. Count the station's program once: three quality outputs do not create three song plays. If recovery genuinely restarts an unfinished song, retain both attempts and their actual durations.

At 00:10 Europe/Istanbul, generate the previous local day's detailed CSV, hourly summary and checksum manifest, then publish in the background to the existing private **akgularda/radiotedu-broadcast-reports** repository. Keep local files first, catch up all missing dates after offline periods, verify the remote commit/manifest and expose Retry Upload/connection renewal through Reports/System. No row cap, fixed six-station mapping or network requirement for local history. Annual date-range export remains CSV; no additional submission format is required by the current answers.

Keep separate source/decoded-listener evidence. These CSVs report the station program, not a guarantee that every remote listener received each sample. A missing tag appears as missing metadata for review, never a guessed artist/title. Ads, branding and prerecorded programs remain in operational history and can be excluded from the songs CSV.

## Native English interface

Use C#/WPF with an independent local service boundary and seven clear destinations. Confirm the actual Windows 10 Pro build, runtime support/deployment path, service account permissions and audio-driver behavior before locking the installer. The current development PC cannot establish HP Z220 compatibility or capacity.

| Screen | Default workflow |
|---|---|
| On Air | All-station overview, selected station's Now Playing/Next Up, remaining time, source/delivery state, Mic On Air, private cue, manual insert/skip, Return to Automation. |
| Library | Station folder assignment/status, batch imports, automatic discovery, search, metadata/bulk edits, playlists, retire/restore, jingles/sweepers/programs. |
| Schedule | Special days first; optional weekly rules, prerecorded programs, future playback preview, drafts/history and conflict resolution. |
| Ads and Sweepers | Two-song jingle/three-song sweeper rules, sequence preview, collections, optional ad campaigns and missed/partial delivery reports. |
| Reports | Every-song history, daily CSV/upload status, annual export, missing metadata and microphone-session recording list/play/export. |
| System | Stations/18-output plan, devices, service startup, credentials/GitHub, storage/retention, users, optional integrations, updates. |
| Recovery | Incidents, restart reason, output evidence, backups/verified restore, diagnostics and safe repair. |

Use a restrained RadioTEDU broadcast desk with clear alignment and readable typography, accessible text-plus-color states, persistent station identity, 24-hour time and English product text. Keep technical diagnostics behind System/Recovery. Preserve Turkish titles/artist names accurately. Support 1366×768, 100–150% scaling, keyboard focus and Move Up/Down alternatives to dragging.

Common tasks must finish entirely in the interface: choose a folder, upload a batch, correct tags, change night preference/cadence, create a special day, queue a program, test/open/close the mic, find/export recordings, configure outputs, inspect/retry report upload and restore a backup. Save requires persisted read-back; errors explain the next action. No placeholder buttons or manual database/config steps qualify as completion.

## Scope of optional capabilities

The broader approved inventory remains the feature contract: guest rooms, show permissions/rundowns, soundboard, advertising improvements and integration adapters are retained in the roadmap. Their default state is disabled/unconfigured, since the operator currently uses none of the external integrations. Build the unattended music/microphone workflow first, then add these through the same native settings/permissions model. Do not start models, HLS transcoders, Voting/Juke services, EN/FR sources or remote control automatically. External EN/FR source ownership stays external.

## Delivery order and acceptance

| Stage | Deliverable | Required evidence before moving on |
|---|---|---|
| 1. Durable recovery | Finish atomic state/command handling; restart unfinished content from beginning; preserve cadence/event ownership and mark interrupted attempts. | Kill the engine before/after start, at EOF, during queue ownership/Skip/ad/event transitions, and immediately after acknowledgements. Restart repeatedly from the same local DB. Completed items stay consumed; unfinished attempts follow policy; reports expose uncertainty; no duplicate command effects. Include disk-write failures and corrupt/missing checkpoint handling. |
| 2. Windows service and HE-AAC proof | Separate service-capable host, unattended startup, persistent desired state, capable libfdk_aac toolchain and eight AAC/eight HE-AAC/two FLAC loopback outputs. | Start without UI/login, begin with origin offline, restore it, restart origin/encoder/service, stall one branch and verify healthy siblings. Independently decode all 18 outputs and inspect actual HE-AACv2/stereo/bitrate behavior. Service prototype uses its own test identity; no legacy service changes. |
| 3. Live folders and automation | Automatic folder index, shuffled selection, optional night bias, distinct two/three-song branding counters, basic event/program ownership. | Add/rename/replace/remove during playout, incomplete copies, nested files, watcher overflow, drive loss/return, empty branding collection, due-at-six collision and recovery at insertion boundaries. No daily queue preparation. |
| 4. Native connected UI | Seven-screen shell, complete station/library/queue/settings workflows, English-only product text. | Nontechnical user completes batch import, retirement/restore, cadence change, special-day setup and program queueing without code/config edits. Restart/read-back succeeds; UI closure leaves service streaming. |
| 5. Microphone and conditional recordings | Native audio agent, private cue, music ducking, automatic recording only while Mic On Air. | Private test creates no recording; live audio does; close/loss ends it; music-only sessions create none. Test codec/disk failure, long-session splitting and crash repair without stopping outputs. Verify recording against the actual mixed program. |
| 6. Reports and full operational tools | Daily/annual CSV, private GitHub outbox, backups/restore, installer/repair, remaining approved guest/show/ad/integration workflows. | More than 10,000 events, midnight/year rollover, interrupted/recovered attempts, all eight stations, offline catch-up, duplicate upload retries, credential renewal, full UI task scripts and restore drill. |
| 7. Target-PC commissioning | Exact destination migration preview, Windows 10/driver validation, resource measurements, 24-hour then 72-hour full-roster qualification and controlled one-station-at-a-time cutover/rollback. | All 18 requested outputs at approved quality, meaningful listening QA, folder/report/import/mic/recording load, no unexplained repeat/restart/gap or missing history in the measured run. Preserve untouched station groups, verify each transferred group, and retire the former service only after the complete migrated roster passes observation gates. Retain failures and disclose server-side limitations. |

HE-AAC and service recovery are early release gates, not late optional enhancements. Native UI follows durable recovery and the service/codec proof; it must connect to real persisted commands rather than become a static mockup. The existing engine milestone remains useful evidence but its earlier 16-output AAC/FLAC fixture is not acceptance for this 18-output roster.

## Resources and isolation

Keep the existing 8 GB HP Z220 as the capacity target: preferably no more than 2 GiB for the complete application/codec/audio-agent workload and under 60% typical CPU at the requested roster, subject to measurement. Virtualize library/report views, bound PCM/packet queues, predecode only a small successor window and throttle analysis/import jobs. Do not reduce configured quality to meet the target. If limits are missed, investigate allocation, I/O and encoder architecture before proposing a changed budget.

Use local SSD for active database/WAL/state, not the Google Drive source checkout. Allow UI-selected media and recording roots; account for H:'s limited free space. Recording is conditional, but capacity reserves still apply. Test builds keep literal-loopback destinations and `/next-` mounts. Production destination support belongs to the explicit installer/commissioning mode, not an accidental relaxation of laboratory validation.

The public source repository is **akgularda/radiotedu-onair-next**. Keep the current program, source archives, legacy reference, service names, databases, folders, mounts and credentials untouched. A later cutover requires its own controlled handover; planning and isolated implementation do not switch any live broadcast.

## Technical references

- [FFmpeg libfdk_aac encoder documentation](https://ffmpeg.org/ffmpeg-codecs.html#libfdk_005faac) documents HE-AAC/HE-AACv2 profiles and required build support. Pin and verify the chosen toolchain; the currently tested generic FFmpeg does not provide this encoder.
- [FFmpeg licensing guidance](https://ffmpeg.org/legal.html) and the encoder build notes inform packaging. Determine a reproducible FDK-capable build/integration and its redistribution path before including binaries in an installer; do not assume an arbitrary GPL/FDK binary can be bundled.
- [Microsoft .NET 10 OS support matrix](https://github.com/dotnet/core/blob/main/release-notes/10.0/supported-os.md) is part of the target Windows compatibility gate.
- [Full legacy inventory and approved additions](LEGACY_FEATURES_AND_APPROVAL.md) retains individual capability IDs and source references; the present operating decisions override earlier language, recording and output-count assumptions.
