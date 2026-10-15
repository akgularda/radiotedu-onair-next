# Broadcasting PC build and staged migration handoff

User-authorized handoff dated 15 October 2026. Use the **akgularda** GitHub account. New private source: https://github.com/akgularda/radiotedu-onair-next. Private report archive: https://github.com/akgularda/radiotedu-broadcast-reports.

## Objective and authorization

Finish RadioTEDU OnAir Next on the broadcasting PC, build the native Windows application, configure it through UI workflows, transfer broadcasts **one station at a time**, then retire the former program after all required outputs work and verification gates pass. The user explicitly authorized this staged transfer and final shutdown; do not repeatedly request permission for the same authorized steps. A verified rollback and sufficient evidence are prerequisites, not reasons to skip implementation.

Software development may use source edits, build commands and tests. **Operational configuration, content adjustments, import/migration, station/output settings, service setup/startup and cutover/rollback must be executable through the UI.** Do not substitute direct SQL updates, handwritten config files, hidden API calls or command-line operational workarounds for missing controls. Read-only technical inspection and verification are permitted. If the needed UI action is absent, implement and test it before using it. No operator should need Codex to run the finished application.

The development-PC rule to leave production untouched remains correct for that host. On the actual broadcasting PC, this user's newer instruction authorizes the specific UI-driven staged migration described here. Keep the old installation running and unchanged through discovery/development; only the currently selected station may change ownership during its handover. Do not relax loopback test validation globally. Implement a deliberate commissioning mode with UI validation, protected credentials and per-station source fencing.

## Read first

Read `AGENTS.md`, `README.md`, `DEVELOPMENT_PLAN.md`, `docs/OPERATING_PLAN.md`, `docs/LEGACY_FEATURES_AND_APPROVAL.md`, `docs/ENGINE_VERIFICATION.md` and this handoff. Operating-plan user decisions and this later migration instruction supersede earlier provisional defaults. `docs/LEGACY_API_INVENTORY.json` is a source map, not evidence that every endpoint works.

The legacy reference is version 1.1.3, commit `a59d1d867094f63c1a087182bc361a7a5bd00c01`, branch `safe-program-transitions-20261012`, repository `radiotedu/RadioTEDU-OnAir-New`. Older `main` differs. Source-only access is sufficient; do not modify that repository. Some inventory source links refer to the original sibling checkout; use the pinned source/reference or actual installed source to resolve them on this PC. The running program's verified settings and assets are authoritative for migration, not an old runbook.

## Honest starting state

The first engine milestone implemented continuous PCM, persistent AAC-LC/FLAC sessions, bounded output recovery, local history, reports and loopback Icecast tests. It was tested on a newer development PC, not the HP Z220. The old fleet fixture uses eight AAC and eight FLAC test outputs; it does not validate the requested 18-output production roster or HE-AAC.

The handoff also preserves unfinished recovery and `OnAirNext.Application` source. Do not deploy it as a finished application. Specifically:

- The working checkpoint code currently attempts sample-position resume; **the approved policy is restart the unfinished item from its beginning**, with a new linked attempt and interrupted history.
- Review synchronous journal/Skip writes and periodic awaited commits in the playout path, durable command validation, saved desired-stop state, restart title/state and transition ownership. They have not passed crash-recovery acceptance.
- Application storage is an initial single-studio sketch with global tables, limited list queries and simple rotation. It is not the multi-station live-folder domain and is not integrated into the solution. Complete or replace it rather than treating its contracts as the final architecture.
- There is no finished native UI, Windows service installer, hardware microphone agent, conditional recording workflow or scheduled production report publisher.
- The tested FFmpeg lacks libfdk_aac. Qualify a reproducible capable toolchain; never silently fall back to AAC-LC on a HE-AAC output.

See `docs/HANDOFF_STATUS.md` for the actual pre-push verification results. Short test success is not production readiness.

## Confirmed product behavior

1. Eight existing stations: Classic, Lofi, Radio, Cazz, Rock, Energize, Situation and Main Character. Preserve actual IDs and mount names. Final roster: **eight AAC-LC 192 kbps, eight HE-AACv2 64 kbps using libfdk_aac, and the two existing Classic/Cazz Ogg FLAC outputs: 18 total**. Learn the actual FLAC names from the old program. Add the two newly requested HE-AAC branches through the new UI. EN/FR remain externally owned; create no invented stations/mounts.
2. One live music folder per station, automatically indexed. Any subfolders belong to that station. Changes made in Explorer or through batch import become eligible automatically after stability/decoding checks. Preserve queue order, history and asset identity; do not empty a catalog because a drive temporarily disappears. No compulsory daily scheduling or manual database work.
3. Shuffled unattended music. Nighttime higher-BPM preference is soft and configurable; low/unknown BPM remains playable. Jingle every two completed songs and sweeper every three, with separate durable counters and explicit collision handling. Learn all actual jingle/sweeper files, folders, selection and gain settings from the old program instead of inventing replacements. Preserve approved station-specific variations with visible UI read-back.
4. Special-day UI for October 29 and other configured events: dates/annual recurrence, station targets, selected content, opening/cadence, start/end and return to automation. Prerecorded program import/play/schedule is available, without making daily show preparation mandatory.
5. Microphone over music with native device selection, private cue, gain, ducking and loss recovery. **Record only while a microphone is on air**, capturing the program mix. No music-only or private-test recording. Quiet pauses do not stop recording while the mic stays on air. Stop/segment on mic close or input loss. Storage/retention are UI settings; the user did not approve continuous recording.
6. Independent Windows service starts without user login. After power/process failure, restart an unfinished item at its beginning; committed completed items stay consumed. Keep retrying offline outputs with bounded backoff indefinitely. Network recovery must not restart the current item or healthy outputs. UI closure must not stop automation. Keep intentional stopped-state distinct from crashed state.
7. Every actual song attempt gets immutable artist/title and precise time/duration/status history. One station occurrence regardless of quality variants. Daily Europe/Istanbul CSV locally and private GitHub archive with retry/catch-up/verified commit; annual CSV export without silent row caps. Record interrupted and actually repeated recovery attempts honestly. Arbitrary prerecorded/live mixes require an entered/imported set list for embedded songs; do not invent recognition.
8. English-only product UI/setup/help/errors/report headers. Preserve original artist/title text. Retain all approved optional show/guest/soundboard/advertising/integration capabilities, disabled/unconfigured unless used. Heavy optional work must not consume the 8 GB PC's audio headroom.

## Learn the existing installation before importing

Use the current UI and read-only inspection to inventory all stations, outputs/codecs/bitrates, source ownership and service startup; music roots; jingles, sweepers, startup audio, gains/cadence; advertisements; special days/playlists/schedules; microphone settings; reporting paths; backups; and optional integrations. Determine which assets are referenced by the database versus discovered in folders. Inspect actual audio metadata where necessary.

Create a verified online snapshot/export through existing backup UI or implement a proper guided snapshot/import workflow. Never copy a live WAL database as a plain file or write to it to make migration convenient. Keep backups/credentials/media local and protected. Do not upload them to source or report repositories. The new import wizard must preview mappings/counts, missing assets, unsupported settings and credentials handling, import without changing the old DB, and read back saved settings.

Old music/jingle/sweeper roots remain readable for rollback. During parallel operation, avoid changing shared tags, renaming/deleting old assets or letting two folder watchers write the same files. Stage any required writable new media separately. Do not lose branding just because an old asset type was called a different name.

## UI research and implementation

Before designing, examine current official screenshots/manuals for established broadcast tools. Starting references:

- [PlayIt Live](https://docs.playitsoftware.com/PlayItLive/218/PlayItLive.html): running playout log, current/upcoming players, readable timers, track search, QuickCarts and private pre-fade listening.
- [mAirList Radio Automation](https://www.mairlist.com/en/products/radio-automation/) and its [cartwall manual](https://mairlist.docs.mairlist.com/configuration/cartwall/): automation/live-assist, cartwall, cue editing and distinction between on-air and audition routing.
- [RCS Zetta log module](https://www.rcsworks.com/blog/zettas-log-module-did-you-know/) and [Hot Keys](https://www.rcsworks.com/blog/zettas-redesigned-hot-keys/): visible current progress, editable logs and station-aware on-air/audition controls.

Record reference URLs, useful operator patterns and how they fit RadioTEDU in a short design note. Inspect actual layouts, not just marketing descriptions. Use familiar broadcast conventions with original RadioTEDU styling; do not copy proprietary assets or a vendor's identity. Avoid decorative dashboards, tiny technical controls and placeholder forms.

Implement native WPF screens: On Air, Library, Schedule, Ads and Sweepers, Reports, System and Recovery. Include an all-station overview, central Now Playing/Next Up with clear timing, searchable media, cartwall/live strip and compact health status. All settings—including migration, destination changes, service ownership and rollback—need working UI forms/wizards with persisted read-back, permissions, actionable errors and restart verification. Test at 1366×768 and Windows scaling with keyboard access.

## Build and validation order

First measure existing CPU/RAM/disk headroom without disturbing broadcasts. Keep local database/build/test state outside cloud-sync folders. The target is HP Z220, Xeon E3-1245 v2, 8 GB RAM, Windows 10 Pro; validate the runtime/OS/driver path. Bound and throttle builds, imports and listener probes so tests cannot starve live audio. Do not run a second full encoder/listener fleet on the broadcast PC without measured headroom. Use isolated test resources/another test host when necessary; do not bypass validation because the PC is busy.

Finish durable state/command recovery first, then service/FDK proof, live-folder automation, connected native UI, microphone/conditional recordings, reports/maintenance and remaining approved capabilities. Use synthetic/representative media and owned loopback Icecast before production. Exercise engine/encoder/origin failure, offline startup, one stalled output, EOF/Skip crashes, duplicate commands, missing drives, midnight and recording failures. Each broadcast variant requires actual decoded audio and codec validation, not just a source-connected badge or HTTP response. Retain successful and failed evidence, timestamps, version and roster.

Run the full-roster 24/72-hour qualification gates in the safe test arrangement described in the operating plan. Never label a short sample as proof of all-day continuity. Once stations are live on the new host, continue long-duration observation; final retirement requires the complete migrated roster to pass the full observation gates too.

## One-station-at-a-time cutover

Build a persistent migration checklist in the new UI with station groups, output ownership, tests, current stage and Roll Back. Process one station group at a time; a group contains its AAC-LC, HE-AAC and FLAC where applicable. Other groups remain untouched and broadcasting. Do not run two sources against the same live mount.

For each station:

1. Verify snapshot/rollback, mapped media/branding, real next-item transitions, correct codecs, service desired state and report/recording behavior against isolated test outputs. Record baseline health for all other live stations.
2. Import/configure destinations through UI while disarmed. Verify the complete group and its inverse rollback action before releasing any old source. Choose one pilot based on measured risk, not an invented priority.
3. Use supported server relay/fallback/handover when available to reduce interruption. Otherwise show and measure the expected handover gap; do not promise a gapless switch without an actual mechanism.
4. Release **only this station's** old outputs through its UI, persist its legacy autostart as disabled for transferred outputs, verify old ownership released, then activate this group from the new UI. Avoid changing unrelated station/service settings. If existing controls cannot do this granularly, implement the UI workflow first; do not stop the whole old engine.
5. Decode every destination externally, verify correct song/branding order, transitions, stereo/profile/bitrate, metadata and continuity across successive items. Confirm report occurrences and that other stations stayed healthy. Observe over a documented interval and inspect resource headroom before taking the next station.
6. On failure, stop/release only the new group, verify release, restore old group/source ownership and its desired startup state through UI, confirm listener audio, and mark the migration rolled back. Never leave an automatic old/new reconnect race.
7. Mark this station complete only with evidence and continue to the next group. Resume safely after UI/PC restart from the durable migration state; do not guess which program owns a mount.

During incremental migration, preserve provenance and reporting continuity across old/new histories. Do not double-count overlapping observation or treat a queued song as played. An explicitly desired source expansion must not be treated as an accidental legacy mismatch.

## Retiring the former program

Only after **all eight station groups / all 18 requested outputs**, including Classic and Cazz FLAC, are verified under the new service and the full observation gates pass:

- Use the UI's retirement workflow to stop the old broadcast service/supervisor and disable its automatic source startup/recovery so it cannot reclaim mounts after reboot. Closing the desktop window alone is insufficient.
- Confirm the new service is the sole authorized source owner, all outputs still decode, reports publish and no unintended former background keeper remains.
- Keep the old installation, verified snapshot and necessary media available for rollback; do not uninstall or delete them.
- Reboot/power-return verification must be controlled and supported by a tested fallback/rollback. Do not reboot healthy live broadcasts merely to claim the test happened; document any remaining gate until a safe rehearsal/handover arrangement exists.

Finish with repository commits/build version, per-station migration/verification evidence, measured limits, the former-service retirement state, and the exact UI rollback procedure. Do not claim permanent repair of the remote server's historical stuck sessions without server-side evidence.
