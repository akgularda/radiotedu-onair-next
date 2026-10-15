# RadioTEDU OnAir Next

Broadcast-PC continuation: see the [delivery ledger](docs/BROADCAST_PC_DELIVERY_LEDGER.md) for current 49-unit/14-check isolated recovery evidence and incomplete release gates. The native UI is being developed in the requested dark Palantir-like direction; production has not been migrated.

Independent Windows broadcast application under development. The first engine milestone implements continuous program audio, isolated Icecast outputs, an audio-agent input boundary, local play history, and reproducible loopback tests. The operator interface, installer, production migration, and full automation features follow the [development plan](DEVELOPMENT_PLAN.md).

**Broadcasting-PC handoff:** read [the handoff instructions](docs/BROADCAST_PC_HANDOFF.md), [approved operating plan](docs/OPERATING_PLAN.md) and [current handoff status](docs/HANDOFF_STATUS.md) before continuing. This snapshot includes unfinished recovery/application source, not a production-ready installer. Unfinished items now restart from the beginning as linked attempts; 49 unit cases and a 14-check real hard-kill/stop-intent scenario passed on the broadcasting PC. Full recovery and automation acceptance remains open. The new required roster is eight AAC-LC + eight libfdk_aac HE-AACv2 + two FLAC outputs. Configure/operate through the native UI and migrate one station group at a time; retire the old service only after the verified handover gates pass.

This repository does not launch, modify, import, or replace the current RadioTEDU installation. The development host rejects production destinations: only literal loopback IPs, unprivileged test ports, `/next-` mounts, a private development data root, and new named-pipe identities are accepted.

## Implemented engine

- C#/.NET 10 engine with a separate executable host and current-user command pipe.
- Continuous 48 kHz stereo program timeline, 20 ms PCM blocks, shared pooled buffers, and exact boundaries inside a block.
- Independent current and prebuffered successor decoders; no whole-track PCM cache.
- Persistent AAC-LC 192 kbps and Ogg FLAC encoders over asynchronous Windows named pipes.
- Independent bounded output queues, transport write deadlines, owned-process cleanup, and reconnection.
- Explicit HE-AACv2 configuration fails when `libfdk_aac` is unavailable; no quality substitution.
- Bounded live PCM input, smooth music ducking, sample limiting, and automatic gain recovery after input loss. Hardware microphone capture belongs to the later user audio agent.
- Append-only SQLite event journal, hash verification, immutable artist/title snapshots, occurrence IDs, start/end/skip/interruption events.
- Istanbul daily CSV, all 24 hourly count rows, checksums, incomplete-play flags, and streamed arbitrary-period export without a 10,000-row cap.
- Durable restart-from-beginning attempts, persistent enabled/running intent, atomic stop receipts and Skip command-ID/payload checks across process death.
- Distinct durable jingle/sweeper completion counters; legacy behavior import and planner integration remain pending.
- Native WPF On air/Library/System window in an original dark theme, isolated-engine discovery/status and selected-station Skip. Start/Stop and the station coordinator are being qualified; Windows CI has compiled the Library file/folder controls and passed 70 unit cases; connected mouse operations, real library audio validation and the complete operator interface are not qualified.

The default laboratory playlist is a developer-provided sequence. Durable queue primitives exist; complete operator queue editing, schedules, campaign ownership, migration and production credential workflows are not delivered. See the delivery ledger for the exact acceptance gaps.

The public source repository is [akgularda/radiotedu-onair-next](https://github.com/akgularda/radiotedu-onair-next). Synthetic report fixtures are archived separately in [akgularda/radiotedu-broadcast-reports](https://github.com/akgularda/radiotedu-broadcast-reports). The development publisher verifies remote commits and repeat publication; the scheduled application publisher remains a later milestone.

## Build and test

On a Windows development PC with FFmpeg and 7-Zip available:

```powershell
.\scripts\Test-Engine.ps1
```

The bootstrap downloads pinned official .NET and Icecast archives into `%LOCALAPPDATA%\RadioTEDU\OnAirNext-Dev\tools`, verifies the pinned hashes, and extracts them without running an installer. The SDK checksum is from Microsoft's release metadata; the Icecast checksum pins the official download used for this milestone. No machine PATH, production service, or existing program is changed. NuGet dependencies are locked.

Build outputs, databases, generated test credentials, fixtures, and test-origin logs remain on the local SSD under `%LOCALAPPDATA%\RadioTEDU\OnAirNext-Dev`. Source and public evidence summaries can live in this synchronized repository.

Integration scenarios:

| Scenario | Real behavior exercised |
|---|---|
| baseline | Songs, advertisement and sweeper through persistent AAC and FLAC sessions |
| recovery | Kill an owned encoder, then restart the owned Icecast origin |
| durable | Hard-kill/restart from the opening, linked attempts, durable Skip retry, persistent Stop and receipt-collision rejection |
| offline | Begin with no origin, then bring it online without resetting the program |
| fleet | Eight synthetic stations, sixteen AAC/FLAC outputs, sixteen continuously decoded listeners |
| live | Synthetic audio-agent input, ducking, loss and return |
| backpressure | A real loopback source socket accepts headers and stops reading audio |
| corrupt | Reject a malformed future media file and continue with its approved successor |
| host | Separate engine process, disconnected controller, idempotent Skip, explicit Stop |

Run one scenario or a longer bounded fleet test:

```powershell
.\scripts\Test-Engine.ps1 -Scenario fleet -DurationSeconds 60 -SkipBootstrap
.\scripts\Test-Engine.ps1 -Scenario soak -DurationSeconds 86400 -SkipBootstrap
```

The soak command supports up to 72 hours and streams analysis without retaining full audio recordings. A supported command is not evidence that a 24/72-hour run has already passed. Each run prints the path to `results.json`, preserving success and failure evidence. Every test-created process and socket is cleaned up when the run ends.

## Audio and reporting semantics

`SourceConnected` is an internal transport state. It is not independent proof of complete origin acceptance, listener delivery or on-air audio. Lab probes independently decode the listener streams and inspect synthetic tone order and 20 ms RMS windows. The first 200 ms of decoded audio is excluded from the continuing-stream RMS checks to allow codec priming. This analysis does not prove every listener's wall-clock playback continuity.

The PCM timeline advances independently of network recovery. When an output exhausts its bounded queue, it discards stale queued blocks and rejoins the current program; it does not hold other outputs or replay a completed item. Outage time and discarded blocks remain observable. No finite buffer can retain an unlimited outage.

Events refer to samples entering the program timeline, not an assertion that a remote listener heard them. Quality outputs share one occurrence per station play. The first reporting schema provides hourly occurrence counts; duration apportionment across hours/dates and authoritative annual submission templates are later work.

The journal has a bounded 4,096-event in-memory handoff followed by a dedicated SQLite writer using FULL synchronous WAL commits. Rejection and writer failure are visible in host status. A hard process/power failure can lose events still in the handoff; a committed start without an end is reported incomplete. Durable admission/checkpoint recovery and reporting completeness across hard failures are required before production commissioning. The hash chain detects accidental or unauthorized row modification; it is not an externally signed attestation.

## Current verification scope

See [engine verification](docs/ENGINE_VERIFICATION.md) and the checked-in machine-readable evidence. Tests use generated tone assets and loopback Icecast 2.5.0 on this Windows 11 development PC. The fleet represents eight stations and sixteen test outputs, with eight AAC-LC and eight Ogg FLAC outputs. It does not reproduce the commissioned HE-AAC branches or establish HP Z220 capacity.

Production release still requires Windows 10 Pro and device testing, the exact persisted station/output roster, approved HE-AAC capabilities, real audio listening QA, long-duration soaks, full UI workflows, durable recovery, and migration/rollback rehearsals. No production cutover or service installation has occurred.

## Project map

| Location | Responsibility |
|---|---|
| `src/OnAirNext.Engine` | Audio, output recovery, local event journal and reports |
| `src/OnAirNext.Host` | Independent headless host and local command boundary |
| `tests/OnAirNext.Tests` | Settings isolation, PCM ownership, live mixing, history and reporting tests |
| `tests/OnAirNext.Lab` | Owned Icecast fixtures, fault injection, listener decoding and resource evidence |
| `scripts` | Reproducible developer setup and checks |
| `artifacts/evidence` | Sanitized test summaries; no credentials, database or audio files |

Windows audio I/O and WPF are intentional platform choices. The runtime and decoder dependencies require a documented update path. This project does not change the existing application's licensing or publish its media.
