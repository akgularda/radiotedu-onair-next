# OnAir: legacy inventory and proposed scope for approval

Prepared 15 October 2026. Approval status: **recommended scope approved** by the user. The [current operating and delivery plan](OPERATING_PLAN.md) records the subsequent answers and takes precedence for the 18-output roster, English-only interface, restart-from-beginning recovery and microphone-on-air recording trigger. Durable recovery is the first implementation stage.

## What this inventory establishes

This audits the supplied OnAir **1.1.3** source, commit `a59d1d867094f63c1a087182bc361a7a5bd00c01`, branch `safe-program-transitions-20261012`. It does not assume GitHub's older `main` is the installed version. The extracted reference contains 852 source files verified against the archive manifest. No live installation, production database, audio library, credentials, service or mount was changed or queried for this inventory.

“Present” below means an implementation is present in this source. It does not mean that every feature is enabled, configured, exposed equally well in the UI, or verified on the broadcast PC. Optional dependencies and known incomplete implementations are called out. Source links are relative to this document in the separate development directory.

The accompanying [API inventory](LEGACY_API_INVENTORY.json) enumerates **324 source-declared registered HTTP/WebSocket routes** and **127 declared API models** across 44 inspected Python files, including static-page routes. Models include request and response structures. It includes handler names, source lines, argument types and source hashes. Endpoint count is not a feature count. Static declaration does not establish correct behavior; the substantive limitations below come from reading the handlers and their services.

## 1. Station management and ordinary playout

- **L01.1** Create, rename, describe, select and delete stations, subject to runtime ownership checks.
- **L01.2** Start a station, start a selected track, stop, resume and skip the current item.
- **L01.3** Preserve the current queue item and queue order on intentional Stop. The documented restart policy restarts that item from the beginning.
- **L01.4** Persist explicit automatic-start authorization and recover authorized stations after backend/service startup.
- **L01.5** Show current track, elapsed/remaining state, next source, station runtime and transition history.
- **L01.6** Automatically refill from the station library when explicit programming is exhausted.
- **L01.7** Deterministic rotation based on play count, last-played time and a saved per-station rotation key; identical persisted state produces the same next selection.
- **L01.8** Track play count and last-played information, and exclude selected assets from autoplay.
- **L01.9** Configure per-station music crossfade from 0 to 10 seconds.
- **L01.10** Local speaker/monitor configuration, separate from source-output configuration.

Sources: [canonical function map](../../RadioTEDU-OnAir-UX-Dev/RadioTEDU-OnAir-New/docs/AI_MAINTAINER_FUNCTION_MAP.md), [runtime API](../../RadioTEDU-OnAir-UX-Dev/RadioTEDU-OnAir-New/app/api/runtime.py), [legacy API](../../RadioTEDU-OnAir-UX-Dev/RadioTEDU-OnAir-New/app/api/legacy.py), [audio API](../../RadioTEDU-OnAir-UX-Dev/RadioTEDU-OnAir-New/app/api/audio.py).

## 2. Queue and program workspace

- **L02.1** View pending queue entries and current playout state.
- **L02.2** Add tracks, refresh/autofill, remove future entries and move items explicitly.
- **L02.3** Queue mutation acknowledgements, deduplication and expected-revision checks for conflicting edits.
- **L02.4** Distinguish manual queue, due advertisement, scheduled item and library fallback selection.
- **L02.5** A separate show/program queue with add, reorder, remove and clear actions.
- **L02.6** Claim/release the program workspace, with controlled ownership and administrative override.
- **L02.7** Switch between ordinary automation and the program source.
- **L02.8** Program music normal/duck/mute control and legacy Liquidsoap-named push/skip/cart/played compatibility endpoints. These names do not prove a separate Liquidsoap installation is required.
- **L02.9** Database command outbox: list, claim, mark done/failed, retain attempt/status information. This is a backend capability, not a finished daily-report publishing UI.

Sources: [queue](../../RadioTEDU-OnAir-UX-Dev/RadioTEDU-OnAir-New/app/api/queue.py), [legacy](../../RadioTEDU-OnAir-UX-Dev/RadioTEDU-OnAir-New/app/api/legacy.py), [outbox](../../RadioTEDU-OnAir-UX-Dev/RadioTEDU-OnAir-New/app/api/outbox.py).

## 3. Music/media library

- **L03.1** Batch audio upload and station assignment; content types include music, advertisement, jingle and startup audio.
- **L03.2** Native operator file/folder selection, folder scans and recursive import.
- **L03.3** Managed station folders with saved recursive, skip and merge/replace policies.
- **L03.4** Background watching and stable-file incremental import; rescan and watcher status.
- **L03.5** Treat transient missing drives/incomplete scans differently from confirmed removals; avoid wiping inventory during a failed scan.
- **L03.6** Add newly imported/reactivated material to future selection while preserving operator queue ownership; let a playing item finish when its future eligibility changes.
- **L03.7** Search/filter/paginate tracks, list available filter values and show library statistics.
- **L03.8** Store artist, title, album, genre, language, duration, BPM, content type, file path and MusicBrainz recording ID.
- **L03.9** Edit ordinary metadata, set autoplay exclusion and deactivate tracks. Rights metadata has its own editor/API.
- **L03.10** Soft cleanup of missing local files; URL-based media are not treated as missing disk files.
- **L03.11** Product catalog discovery/rescan and unified-media inventory/refresh, including configured hard-link based arrangements.
- **L03.12** Serve authorized application media for preview/use.

Sources: [tracks](../../RadioTEDU-OnAir-UX-Dev/RadioTEDU-OnAir-New/app/api/tracks.py), [library automation](../../RadioTEDU-OnAir-UX-Dev/RadioTEDU-OnAir-New/app/api/library_automation.py), [legacy](../../RadioTEDU-OnAir-UX-Dev/RadioTEDU-OnAir-New/app/api/legacy.py).

## 4. Import processing, metadata and downloads

- **L04.1** YouTube/playlist URL imports through yt-dlp, with target station, queued jobs, status and import settings.
- **L04.2** Format/quality and music-oriented import options; optional import processing.
- **L04.3** Real audio/tag BPM analysis in batches, confidence filtering, saved results and detailed skipped/error/progress counts.
- **L04.4** Manual beginning/end trim, automatic silence trim, batch silence trim and optional intro cleanup. These are audio-processing operations; a full non-destructive waveform editor is not established.
- **L04.5** Basic tag/string normalization and preservation of existing MusicBrainz IDs.
- **L04.6** Metadata-rule CRUD exists, but broad autofix/normalization execution is incomplete; see limitations.
- **L04.7** “iTunes verification” and “censor” routes exist but do not implement the promised lookup/audio modification; see limitations.

Sources: [legacy import/metadata handlers](../../RadioTEDU-OnAir-UX-Dev/RadioTEDU-OnAir-New/app/api/legacy.py), [track processing handlers](../../RadioTEDU-OnAir-UX-Dev/RadioTEDU-OnAir-New/app/api/tracks.py), [basic normalization](../../RadioTEDU-OnAir-UX-Dev/RadioTEDU-OnAir-New/app/metadata/picard.py).

## 5. Playlists

- **L05.1** Create, describe, list, inspect and delete playlists.
- **L05.2** Add individual/bulk tracks, remove entries and reorder the playlist.
- **L05.3** Generate playlists using artist, genre, type, BPM range, item limit and sorting options.
- **L05.4** Use playlists as scheduled or special-event content.

Source: [legacy playlist and schedule handlers](../../RadioTEDU-OnAir-UX-Dev/RadioTEDU-OnAir-New/app/api/legacy.py).

## 6. Scheduling and broadcast planner

- **L06.1** Schedule a track or playlist with a start time and optional end/window.
- **L06.2** Named repeating schedule entries, weekday rules and timeline display.
- **L06.3** Create/edit/delete/enable broadcast plans for ads, sweepers and recorded programs.
- **L06.4** Target multiple stations from one source asset, with source-station ownership.
- **L06.5** Start/end dates, weekdays, local time windows and timezone, defaulting to Europe/Istanbul.
- **L06.6** Repetition by time interval or song count; sweeper frequency by number of songs.
- **L06.7** Priority and allowed playback window, including ad due/upcoming status and countdown information.

Sources: [schedule](../../RadioTEDU-OnAir-UX-Dev/RadioTEDU-OnAir-New/app/api/schedule.py), [planner](../../RadioTEDU-OnAir-UX-Dev/RadioTEDU-OnAir-New/app/api/broadcast_planner.py), [legacy](../../RadioTEDU-OnAir-UX-Dev/RadioTEDU-OnAir-New/app/api/legacy.py).

## 7. Dayparting

- **L07.1** Per-station weekly music clocks covering all seven days.
- **L07.2** Time ranges with BPM minimum/maximum and enabled/timezone settings.
- **L07.3** Validate continuous coverage, reject overlaps/gaps and invalid BPM bounds.
- **L07.4** Reset to station profiles; display active period and BPM coverage information.

Source: [dayparts](../../RadioTEDU-OnAir-UX-Dev/RadioTEDU-OnAir-New/app/api/dayparts.py).

## 8. Special days and event broadcasts

- **L08.1** Per-station named events with inclusive dates, daily time windows, timezone, priority and enable/disable.
- **L08.2** Annual recurrence including periods crossing a year boundary; overnight and full-day windows.
- **L08.3** Select a playlist/folder or upload event media while preserving original bytes.
- **L08.4** Preview, select and reorder tracks; retain selections while refreshing and reject incomplete selections.
- **L08.5** Stable ordered or seeded shuffled playback, repeat and event crossfade settings.
- **L08.6** Begin after the current item or use explicit immediate interruption; restore ordinary queue behavior afterward.
- **L08.7** Audio selection and station metadata override can operate independently, including metadata-only events.
- **L08.8** Restore the latest ordinary artist/title when an override ends without restarting audio.
- **L08.9** Persist active event position, avoid duplicate creation on retry, and fall back/retry when event media is missing.

Sources: [special events API](../../RadioTEDU-OnAir-UX-Dev/RadioTEDU-OnAir-New/app/api/special_events.py), [special-day behavior](../../RadioTEDU-OnAir-UX-Dev/RadioTEDU-OnAir-New/docs/SPECIAL_DAY_BROADCASTS.md).

## 9. Advertisements

- **L09.1** Approved ad catalog, folder synchronization and station targets.
- **L09.2** Importing an advertisement does not automatically authorize/schedule its broadcast.
- **L09.3** Timed ad items and slot identifiers.
- **L09.4** Break sets with intro/outro jingles and ordered slots; create/edit/delete/enable.
- **L09.5** Campaigns with start/end dates, day interval, daily repeat limit, priority, notes, tracks and slots.
- **L09.6** Ad console, runtime state and upcoming-break information.
- **L09.7** Planner cadence by elapsed time or song count, allowed windows and multiple target stations.
- **L09.8** Playback ownership and durable retry intended to prevent premature advancement while an ad still drains.
- **L09.9** Remote server midroll request, conditional on a compatible server and enabled capability; separate from local scheduled ad playout.

Sources: [ads](../../RadioTEDU-OnAir-UX-Dev/RadioTEDU-OnAir-New/app/api/ads.py), [legacy campaigns](../../RadioTEDU-OnAir-UX-Dev/RadioTEDU-OnAir-New/app/api/legacy.py), [planner](../../RadioTEDU-OnAir-UX-Dev/RadioTEDU-OnAir-New/app/api/broadcast_planner.py), [streaming](../../RadioTEDU-OnAir-UX-Dev/RadioTEDU-OnAir-New/app/api/streaming.py).

## 10. Sweepers, startup audio and soundboard

- **L10.1** Upload/select jingle folders and save sweeper settings.
- **L10.2** Jingle selection by least-played/last-played/ID; legacy “random” means a stable shuffled policy.
- **L10.3** Sweeper cadence and station-specific placement through automation/plans.
- **L10.4** Startup audio upload, fixed-track or approved-jingle policy and configuration.
- **L10.5** Soundboard item creation/upload/edit/delete, immediate play and stop.
- **L10.6** Soundboard color, category, hotkey, gain, order and duration information, controlled by permissions.

Sources: [legacy automation](../../RadioTEDU-OnAir-UX-Dev/RadioTEDU-OnAir-New/app/api/legacy.py), [soundboard](../../RadioTEDU-OnAir-UX-Dev/RadioTEDU-OnAir-New/app/api/soundboard.py).

## 11. Live microphone and mixing

- **L11.1** Device enumeration and selection of a physical microphone in the browser-based interface.
- **L11.2** Low-latency PCM capture with bounded buffering, live status and signal indication.
- **L11.3** Live activation requires input evidence; explicit stop restores the normal mix.
- **L11.4** Microphone gain, music gain, duck level and normal/duck/mute program music modes.
- **L11.5** Smooth ducking held between spoken words and linked stereo sample-peak limiting.
- **L11.6** Stale/lost microphone recovery rather than leaving music permanently ducked/muted; release capture when leaving the applicable UI/session.
- **L11.7** Program PCM render start/chunk/stop boundary for generated or external contributions.
- **L11.8** Optional WebRTC offer/answer and ICE/STUN/TURN configuration, dependent on enabled settings and aiortc.

The limiter is not proof of true-peak or formal loudness compliance. Existing browser capture is different from the proposed native WASAPI audio agent.

Sources: [audio](../../RadioTEDU-OnAir-UX-Dev/RadioTEDU-OnAir-New/app/api/audio.py), [WebRTC](../../RadioTEDU-OnAir-UX-Dev/RadioTEDU-OnAir-New/app/api/webrtc.py), [release behavior](../../RadioTEDU-OnAir-UX-Dev/RadioTEDU-OnAir-New/docs/RELEASE_1_1_0.md).

## 12. Emergency external audio

- **L12.1** TRT presets and a custom approved-source page URL.
- **L12.2** Open/preview a source before transmission.
- **L12.3** Browser tab-audio sharing with explicit armed/confirmed takeover.
- **L12.4** Show program/source/signal/buffer state.
- **L12.5** Stop emergency audio and restore playlist operation.

This workflow depends on browser capture and operator permissions; it is not proof that arbitrary page URLs are directly decodable stream URLs.

Source: [operator UI](../../RadioTEDU-OnAir-UX-Dev/RadioTEDU-OnAir-New/app/static/onair/index.html).

## 13. Shows and studios

- **L13.1** Create/edit/deactivate shows with station, description and color.
- **L13.2** Assign/unassign presenters and producers; show-specific permission selection and candidate lists.
- **L13.3** Show session/current-session state, Go Live, Go Break and End Show.
- **L13.4** Four show audio roles: intro, outro, break-out and break-return.
- **L13.5** Create/edit studios, join/leave and hand over ownership.
- **L13.6** Studio chat and a shared claimed program workspace/queue.
- **L13.7** Broadcast, queue, jingle, break, end, guest and recording permissions scoped to a show.

Sources: [shows](../../RadioTEDU-OnAir-UX-Dev/RadioTEDU-OnAir-New/app/api/shows.py), [studios](../../RadioTEDU-OnAir-UX-Dev/RadioTEDU-OnAir-New/app/api/studios.py).

## 14. Remote guests and recording

- **L14.1** Issue/revoke invite links, redeem with display name and create guest sessions.
- **L14.2** Waiting room, admit, reject and remove guests.
- **L14.3** Guest self-mute, host mute, on-air switch, gain and all-guests-off-air.
- **L14.4** Host talkback while the host microphone is off-air.
- **L14.5** Guest WebRTC signaling/audio when the optional dependency is installed/configured.
- **L14.6** Recording consent request for all guests with a timeout/cancel path.
- **L14.7** Consented program recording to FLAC with bounded writer queues, segment manifests and optional mirror paths.
- **L14.8** List/download/delete recordings and retention cleanup, default 30 days with configurable bounds.
- **L14.9** Recording/failover continuation support in the configured replicated environment.

This records the consented program mix; it does not establish separate multitrack recordings for every guest or an always-on station recorder.

Sources: [guest room](../../RadioTEDU-OnAir-UX-Dev/RadioTEDU-OnAir-New/app/api/guest_room.py), [guest signaling](../../RadioTEDU-OnAir-UX-Dev/RadioTEDU-OnAir-New/app/ws/guest_router.py), [program recording](../../RadioTEDU-OnAir-UX-Dev/RadioTEDU-OnAir-New/app/services/program_recording.py).

## 15. Streaming outputs and server integration

- **L15.1** Configure station destination, mount, protocol, codec/profile and credentials.
- **L15.2** Multiple quality outputs, enable/public flags, persisted configuration and diagnostics.
- **L15.3** Draft, validate, apply, inspect operation status and roll back output changes.
- **L15.4** Preserve the actual commissioned station roster and codec quality; refuse silent quality downgrades.
- **L15.5** Capacity diagnostics and separate source/runtime/public delivery health information.
- **L15.6** Source metadata update and compatible-server move-listeners/kick-source actions.
- **L15.7** Server feature configuration for fallback, listener authentication, status page, access/playlist logs and ad insertion. The remote origin must actually implement/authorize the feature.
- **L15.8** Dedicated local HLS runtime start/stop/status: six configured radio inputs, two HE-AAC v1 96/192 kbps variants per input, six-second segments and rolling playlists.
- **L15.9** HLS source validation and required encoder checks. Requires `libfdk_aac`, output storage and a configured HTTP serving path; the legacy Rocket HLS toggle is rejected in favor of the dedicated manager.
- **L15.10** Public station discovery API, browser-facing state updates and public campaign endpoints.

The canonical source documents eight primary stations and eight enabled quality variants (16 outputs). Earlier runbooks disagree. Only the broadcast PC's persisted roster can confirm what is currently commissioned. EN/FR belong to their independent system and must not be synthesized here.

Sources: [station output](../../RadioTEDU-OnAir-UX-Dev/RadioTEDU-OnAir-New/app/api/stations.py), [configuration transactions](../../RadioTEDU-OnAir-UX-Dev/RadioTEDU-OnAir-New/app/api/stream_config.py), [streaming](../../RadioTEDU-OnAir-UX-Dev/RadioTEDU-OnAir-New/app/api/streaming.py), [HLS runtime](../../RadioTEDU-OnAir-UX-Dev/RadioTEDU-OnAir-New/app/services/hls_runtime.py), [public API](../../RadioTEDU-OnAir-UX-Dev/RadioTEDU-OnAir-New/app/api/public.py).

## 16. Music reporting and rights metadata

- **L16.1** Append-only hash-chained completed-play ledger, queue identity deduplication and metadata snapshots.
- **L16.2** Artist/title/version, composer, lyricist, producer, label, ISRC, source and rights references, program/presenter, duration/count information.
- **L16.3** Track rights-metadata read/edit and station/date filters.
- **L16.4** CSV/JSON export and optional inclusion of Juke music history.
- **L16.5** Monthly close records with checksums, close identity and ledger boundary information.
- **L16.6** Official-export tools for current, daily and month-close reports, atomic local writes and configured archive copies.
- **L16.7** A GitHub publication script exists for aggregated music usage.
- **L16.8** Operational logs, filtered log export and event list/create/delete tools, distinct from the immutable music ledger.

Important gaps: completion-based `broadcast_at` uses finish time; some exports use UTC days; combined exports contain 10,000-row caps; the GitHub script has a fixed six-station mapping and aggregates rather than preserving every chronological play. This is not the complete daily Istanbul-time automatic archive requested for the new product. Arbitrary live/external audio is not automatically identified.

Sources: [music usage API](../../RadioTEDU-OnAir-UX-Dev/RadioTEDU-OnAir-New/app/api/music_usage.py), [ledger](../../RadioTEDU-OnAir-UX-Dev/RadioTEDU-OnAir-New/app/services/music_usage.py), [official export](../../RadioTEDU-OnAir-UX-Dev/RadioTEDU-OnAir-New/tools/export_official_music_usage.py), [GitHub publisher](../../RadioTEDU-OnAir-UX-Dev/RadioTEDU-OnAir-New/tools/publish_music_usage_to_github.py).

## 17. RadioTEDU integrations and campaigns

- **L17.1** Configure RadioTEDU adapters and inspect their health/status.
- **L17.2** Allowlisted managed-service start/stop/restart and guarded update actions, with editable paths/configuration/health settings.
- **L17.3** JukeLocal media agent library browse/upload, safe retirement to trash and restore with configured-root protections.
- **L17.4** Voting round publication and resolution, voting-agent/backend integration.
- **L17.5** Named campaign dates, genres, AI eligibility, track-title cleanup, public campaign display and public votes.
- **L17.6** Study/mobile companion integration surfaces and external service links.
- **L17.7** Monitor/control a separately configured EN/FR service; do not create EN/FR sources in this engine.

A campaign name such as “NoCopyrightMonth” is not evidence of music rights clearance. External applications remain separate dependencies.

Sources: [integrations](../../RadioTEDU-OnAir-UX-Dev/RadioTEDU-OnAir-New/app/api/integrations.py), [campaigns](../../RadioTEDU-OnAir-UX-Dev/RadioTEDU-OnAir-New/app/api/campaigns.py), [service control](../../RadioTEDU-OnAir-UX-Dev/RadioTEDU-OnAir-New/app/services/radiotedu_service_control.py).

## 18. Optional AI host

- **L18.1** Per-station AI enablement, model path, voice, persona/prompt, duration and history settings.
- **L18.2** Track introductions, station IDs, custom TTS tests and announcement history.
- **L18.3** Warmup/readiness, cache clearing, look-ahead and batch pregeneration.
- **L18.4** Local/provider selection and template fallback; accepted speech selections include local Qwen TTS, Windows SAPI, Edge TTS and OmniVoice. Availability depends on installed providers and configuration.
- **L18.5** Model/hardware/package status and benchmark tools.
- **L18.6** Generated-audio contribution boundary and prefetch designed so optional AI failure does not block ordinary music.
- **L18.7** Setup-wizard AI configuration and voice test.

This is an optional subsystem with potentially substantial resource use. Provider labels are not proof that a model is installed or suitable for the 8 GB broadcast PC.

Sources: [AI host](../../RadioTEDU-OnAir-UX-Dev/RadioTEDU-OnAir-New/app/api/ai_host.py), [fast AI actions](../../RadioTEDU-OnAir-UX-Dev/RadioTEDU-OnAir-New/app/api/ai_host_fast.py), [diagnostics](../../RadioTEDU-OnAir-UX-Dev/RadioTEDU-OnAir-New/app/api/ai_diagnostics.py).

## 19. Accounts and permissions

- **L19.1** Sign in, refresh session, sign out, current-user lookup and change own password.
- **L19.2** Create/edit/deactivate users and administrator password reset.
- **L19.3** Administrator/DJ/producer/viewer roles and custom role-template CRUD.
- **L19.4** Permissions for station control, library, playlists, queue, scheduling, downloads, advertisements, soundboard, streaming, reports, users and roles.
- **L19.5** Show-specific presenter/producer assignments and granular live/guest/recording permissions.
- **L19.6** Protected credential storage, token/session controls, login-abuse controls and inactivity handling.

Sources: [authentication](../../RadioTEDU-OnAir-UX-Dev/RadioTEDU-OnAir-New/app/api/auth.py), [users](../../RadioTEDU-OnAir-UX-Dev/RadioTEDU-OnAir-New/app/api/users.py), [roles](../../RadioTEDU-OnAir-UX-Dev/RadioTEDU-OnAir-New/app/api/roles.py), [shows](../../RadioTEDU-OnAir-UX-Dev/RadioTEDU-OnAir-New/app/api/shows.py).

## 20. Continuity, diagnostics and recovery

- **L20.1** Per-station worker supervision, heartbeat/lease ownership and playback checkpoints.
- **L20.2** Next-item prewarming and transition/drain ownership safeguards.
- **L20.3** Output-specific reconnection, bounded buffer/backpressure handling and socket-write deadlines.
- **L20.4** Durable cold-start retries with capped backoff, avoiding automatic disabling of a healthy source because a listener probe fails.
- **L20.5** Backend readiness/liveness, health wall, station/stream health and watchdog status/manual repair/report.
- **L20.6** Diagnostic bundle creation, listing and download with credential redaction.
- **L20.7** Verified SQLite online backups and recovery points, including protected retention tiers.
- **L20.8** Verify and stage a restore; Supervisor applies it before backend startup, with pre-restore backup and rollback handling.
- **L20.9** Optional high availability: leader/follower status, heartbeats/voting/quorum, audit anchors, replicated state and checkpoints. Disabled unless configured and dependent on peers/witness infrastructure.
- **L20.10** Separate listener-decoding verification tools and network-recovery tests.

These are implemented mechanisms and safeguards, not a guarantee of zero downtime. The supplied short samples do not prove a 24-hour run, and this audit does not establish a permanent fix for the remote server's stuck-session defect.

Sources: [watchdog](../../RadioTEDU-OnAir-UX-Dev/RadioTEDU-OnAir-New/app/api/watchdog.py), [recovery](../../RadioTEDU-OnAir-UX-Dev/RadioTEDU-OnAir-New/app/api/recovery.py), [HA](../../RadioTEDU-OnAir-UX-Dev/RadioTEDU-OnAir-New/app/api/ha.py), [durable recovery](../../RadioTEDU-OnAir-UX-Dev/RadioTEDU-OnAir-New/docs/DURABLE_BROADCAST_RECOVERY.md).

## 21. Setup, packaging and maintenance

- **L21.1** Setup wizard state/preflight, dependency repair, configuration, verification, completion and reset.
- **L21.2** Windows Supervisor service ownership, startup/recovery, desktop/tray launcher and WebView2 shell.
- **L21.3** Browser operator wall with service-worker asset caching.
- **L21.4** Backend reload/maintenance and versioned installation/update tooling. Reload is not a guarantee of uninterrupted audio.
- **L21.5** Portable Windows/Linux/macOS launch/setup paths in the source; platform behavior still requires its own verification.
- **L21.6** Encrypted portable recovery bundle/import tools: verified DB snapshot, protected credentials and public output inventory, excluding audio media.
- **L21.7** Platform credential re-protection during recovery (Windows DPAPI / macOS keychain paths).
- **L21.8** RadioTEDU and rtAI-OnAir product profiles with different integration visibility.

Sources: [setup](../../RadioTEDU-OnAir-UX-Dev/RadioTEDU-OnAir-New/app/api/setup.py), [portable recovery](../../RadioTEDU-OnAir-UX-Dev/RadioTEDU-OnAir-New/docs/PORTABLE_RECOVERY.md), [main application](../../RadioTEDU-OnAir-UX-Dev/RadioTEDU-OnAir-New/app/main.py).

## 22. Existing UI organization

The old HTML operator wall exposes 19 destinations: On Air, Media, Playlists, Automation, Emergency, Services, Settings, Diagnostics, Stations, Queue, Scheduler, Special Days, Broadcast Planner, Dayparting, Shows, Compliance, Advertising, Streaming, and Backup/Recovery. Its Windows shell embeds that browser interface. It is not a native WPF control interface.

Many daily tasks already have UI entry points. Some capabilities above are service/API/tool-level, and some administrative/portable/reporting workflows still depend on scripts or configuration. “A route exists” must not become “the operator can finish this task independently.”

## Known incomplete or dependent features

| Item | Source finding | Treatment in the new program |
|---|---|---|
| Audio censorship | `censor_track` returns success/segment count without editing audio | Do not copy the false success. Offer actual non-destructive censoring only if approved. |
| iTunes verification | Handler reports analyzed counts but performs no catalog lookup/matching | Treat external matching as a new optional feature with review before changes. |
| Bulk metadata autofix/normalize | Broad routines largely count records and optionally invoke BPM analysis; metadata update/rule-hit counts remain zero | Implement honest preview/apply/undo if approved; do not promise automatic correctness. |
| MusicBrainz/Picard name | Basic normalization preserves IDs/strings; no demonstrated fingerprint/catalog lookup here | Preserve existing tags; recognition requires a separate feature. |
| Daily GitHub reporting | Tools exist, but time boundaries, fixed station mapping, aggregation and row caps leave gaps | Build a complete local-first scheduled archive and UI. |
| Guest/WebRTC | Real implementation, optional dependency and network configuration | Explicit capability test/setup; music continues if unavailable. |
| HLS | Real local writer; licensed/capable encoder build, HTTP serving and extra CPU/storage required | Optional adapter; do not enable extra transcoding on the old PC by default. |
| Server administration | API requests require compatible server features and credentials | Show supported/unsupported capabilities rather than fake success. |
| HA | Real optional multi-machine coordination code | Separate multi-machine acceptance work; no single-PC failover claim. |

## What exists in the new program today

The committed engine milestone provides independent continuous PCM playout, bounded next-item decoding, persistent AAC-LC/FLAC output sessions, independent output recovery, synthetic live mixing, local event history, Istanbul-time report export and isolated Icecast tests. Private source and synthetic-report repositories exist under **akgularda**.

The recorded baseline passed 24 unit tests and eight integration scenarios. A short eight-station/sixteen-output run was performed on this newer development PC. It did not reproduce the six HE-AACv2 production branches, establish HP Z220 capacity, or prove 24/72-hour continuity.

Durable checkpoints/command recovery and application/library/schedule storage are **unfinished source changes**. Existing focused tests passed after the engine edits, but crash-recovery acceptance is not complete. The new Application project now builds separately but is not integrated into the solution or a UI. **There is no finished native operator UI, installer, hardware microphone capture or scheduled production GitHub publisher yet.** No current broadcasts were moved to the new program. See HANDOFF_STATUS.md for snapshot verification; successful compilation does not certify these unfinished features.

## Proposed approval scope

Every accepted feature must have a usable screen, correct permissions, persisted read-back, useful errors and a recovery path. No routine content or administrative task should require Codex, SQL, a terminal, editing JSON or changing a config file. Engineering defects still require a software update; the product must not depend on an AI assistant for ordinary operation.

### A — Required production foundation and core carryover (recommended)

1. Finish and test durable crash recovery with atomic occurrence/queue/checkpoint ownership, idempotent commands and visible uncertain intervals. Intentional Stop and unexpected crash have distinct recovery rules.
2. Keep audio independent of the UI and background jobs; isolate slow/broken outputs without replaying finished material or restarting healthy outputs.
3. Preserve core L01–L13 and L15–L16/L19–L22 operational needs, including emergency input, startup audio, dayparts, special events, soundboard, shows, permissions and backups. Optional remote/AI/server capabilities use the separately approved packages below.
4. Native seven-screen UI: **On Air, Library, Schedule, Ads & Sweepers, Reports, System, Recovery**. Multi-station overview, clear active station, readable remaining time, ordered queue, microphone strip, private cue and explicit return to automation.
5. Complete import wizard: batch files/folders, real decode validation, progress, partial success, cancel/resume, failed-file retry, duplicate review, metadata suggestions and reversible retirement/restore.
6. Complete schedule editor: one-off and recurring tracks/playlists/programs, weekly dayparts, events, ads and sweepers, with visible station/timezone and conflict resolution.
7. Native microphone device setup/private test, gain/mute/ducking/limiting, device-loss handling and defined behavior when closing the UI or signing out.
8. Accurate local play events with artist/title snapshots, start/end/partial/recovered status and one occurrence per station regardless of quality variants.
9. Automatic previous-day Istanbul reports, all 24 hours, background private GitHub publication, retry/catch-up, confirmed commit/checksum, annual export without row truncation and UI credential renewal. Existing synthetic archives remain labeled as tests. Annual template mapping needs the broadcaster's actual template.
10. UI setup for outputs, storage, accounts, startup, integrations and credentials; verified backups, guided restore, diagnostics, repair/update and rollback.
11. Read-only migration preview from a verified legacy snapshot, local test remapping, acceptance on the HP Z220, full-roster 24/72-hour tests and rehearsed cutover/rollback. Retain current quality and authoritative persisted roster.

### B — Better music and schedule management (recommended additions)

| ID | Addition | Practical benefit |
|---|---|---|
| B1 | Bulk metadata preview/apply with undo and an unresolved-metadata inbox | Fix many songs without SQL or false “automatic correction” claims. |
| B2 | Exact hash duplicates plus separately labeled possible alternate versions | Avoid duplicate imports without deleting intentional edits/remasters. |
| B3 | Artist/title repeat separation and explainable rotation rules | Better variety; show why an item was selected or excluded. |
| B4 | Smart saved collections by genre/language/BPM/type and missing metadata | Reuse selections without rebuilding playlists manually. |
| B5 | Schedule preview/rehearsal: “What will play next Tuesday at 14:00?” | See rule precedence, overflows, missed items and return behavior before publication. |
| B6 | Save draft/publish/version/revert schedules, copy a day/week and reusable show templates | Reduce repetitive work and recover from edits. |
| B7 | Non-destructive waveform cue-in/out, intro/outro markers and audition | Polish transitions while preserving original audio. |
| B8 | Background loudness measurement and optional reversible playback gain, with clipping warnings | More consistent perceived level without mandatory library transcoding. |
| B9 | Expiry/unavailable-media checks and a “why can't this play?” explanation | Catch broken paths and eligibility problems early. |
| B10 | Optional real clean-edit/bleep regions, audition and export | Implement actual censorship rather than the old no-op endpoint. |

### C — Advanced live shows and remote guests (carryover plus improvements)

- **C1:** Preserve guest invitations, waiting room, individual on-air/mute/gain, talkback, consented recording and permissions in the new interface.
- **C2:** Reusable show rundown with timed segments, presenter notes, intro/outro assets and visible overrun countdown.
- **C3:** Configurable push-to-talk, soundboard keys and microphone-safe keyboard focus behavior.
- **C4:** Guest connection test/lobby meter before admission, reconnect guidance and clear fallback ownership.
- **C5:** Recorded-show library and a reviewed “schedule this recording” workflow, preserving consent/provenance and allowing a declared music set list.
- **C6:** Optional rolling station aircheck recorder with per-station limits, disk budget, retention and short incident clips. Distinct from existing consented show recordings; recordings stay local unless explicitly exported.

Recommendation: retain guest compatibility, but deliver the ordinary native microphone workflow before the remote room. Enable recording only with an explicit retention/storage choice.

### D — More capable advertising (improvements)

- **D1:** Campaign scheduled-versus-actually-played ledger, partial/missed status and CSV/PDF-style delivery summary. Describe program playout accurately; remote listener receipt is separate evidence.
- **D2:** Advertiser/category conflict rules, break-duration limits and expiry warnings.
- **D3:** Missed-break recovery preview with “skip / next safe break / reschedule”; do not interrupt a song or replay an ad silently.
- **D4:** Creative versions, approval state, replacement start date and rotation limits.
- **D5:** Sponsor/show association and reusable break templates. Billing/invoicing remains outside this proposed broadcast application.

### E — Optional integrations and editorial assistance

- **E1:** Preserve required Voting/Juke/companion connections behind documented adapters and UI setup/status/test actions. Keep EN/FR externally owned.
- **E2:** AI announcements generated/cached elsewhere or explicitly throttled, with script/audio review and approval before eligibility. No automatic station control.
- **E3:** Optional genuine external metadata/catalog matching with source/confidence, proposed changes and manual approval. Never invent missing artist/title or silently alter reporting history.
- **E4:** Optional live/external set-list entry/import and start/end markers. Automatic recognition is a separate evaluated integration, not a reporting guarantee.
- **E5:** Optional LAN control desk with restricted roles and a single command authority, after the native app is stable.

Recommendation: approve adapters actually used today; defer heavy local model inference. An 8 GB broadcast PC should not spend its reliability margin generating AI speech during live operation.

### F — Advanced distribution and redundancy (later, separately tested)

- **F1:** Preserve optional HLS via a dedicated/offloaded adapter with codec and origin capability tests.
- **F2:** Compatible-origin administration for metadata, source cleanup and listener movement, with verified effects and ownership safeguards.
- **F3:** Secondary-PC standby/failover with tested source fencing, replicated history and deterministic handback. This needs another machine and server cooperation.
- **F4:** Independent listener probe and incident evidence with sensible alarm suppression; distinguish intentional quiet content from source/network failure.

Recommendation: include F4's evidence in core diagnostics, but defer F1–F3 activation until their dependencies and resource costs are confirmed. Local Icecast testing cannot certify a repair in the remote production server.

## Additional ideas worth considering

| ID | Idea | Recommendation |
|---|---|---|
| G1 | “Prepare tomorrow” checklist: missing media, metadata, overlaps, expiring campaigns, disk space and unuploaded reports | High value, local and inexpensive. |
| G2 | Operator handover notes and an activity history showing who changed a queue/schedule/output | High value with multiple presenters. |
| G3 | Beginner workspace with only permitted daily controls; administrator mode for system settings | Recommended; permissions still enforced by the engine. |
| G4 | “Finish this item, then stop/go live/switch program” commands with a visible pending action and cancel | Recommended; reduces accidental abrupt cuts. |
| G5 | Holiday/event templates and copy-to-selected-stations with a before/after preview | Useful for high broadcast volume. |
| G6 | Fixed-time marker with configurable soft/hard timing and an overflow explanation | Useful after schedule precedence is agreed. |
| G7 | Recorded voice-track segments with private audition and automatic music ducking | Optional, useful when presenters cannot attend live. |
| G8 | Storage forecasts before batch imports/recording and automatic safe cache cleanup | Recommended, especially given H: free space. |
| G9 | Accessible English labels, keyboard operation, scaling and persistent built-in contextual help | Approved direction; subsequent user answers require English-only product text. |
| G10 | UI rehearsal mode clearly isolated from all real mounts | Recommended for training and schedule review. |
| G11 | Optional private end-of-day summary/incident notification | Later; choose destination/recipients explicitly before sending anything. |
| G12 | Exportable documented library/schedule/history formats and restore drills | Recommended for sustainability and avoiding dependence on a single maintainer. |

## UI direction for approval

Use a restrained RadioTEDU broadcast desk: neutral surfaces, clear typography, consistent controls, restrained station branding, accessible status colors plus text, no decorative dashboard clutter. Keep the active station, clock and program state visible. Put Now Playing and ordered Next Up centrally, live microphone/cue controls alongside, and connection/incident status in a compact persistent strip. Library and Schedule are full working screens, not small settings dialogs.

Every destructive or immediate on-air action states its station and consequence. Ordinary edits get progress/read-back and Undo where possible. Preview routes to a selected private cue device, never the program bus. Support 1366×768, keyboard navigation and Windows scaling. Performance targets must be measured on the HP Z220; these are design requirements, not achieved benchmarks.

## Approved recommendation

Recommended approval: **A + B1–B9 + C1–C4 + D1–D4 + required E1 adapters + F4 + G1–G6/G8–G10/G12**. Deliver A first, then these improvements in reviewable increments. Keep B10, C5–C6, D5, E2–E5, F1–F3, G7 and G11 optional/deferred unless specifically selected.

The user approved the recommended scope and supplied the operational refinements in OPERATING_PLAN.md. This authorizes development in the isolated new program. It is not production cutover approval and does not change ownership of the current installation. Continue using **akgularda**.
