# RadioTEDU OnAir Next

Independent radio broadcasting engine and local browser console under development. The current RadioTEDU installation continues broadcasting. **No production migration has occurred.**

The user rejected the WPF prototype and requested a browser app. [The browser interface decision](docs/BROWSER_UI_DECISION.md) supersedes earlier native-only UI clauses. The rejected prototype remains historical source and is no longer part of the active solution.

The current development branch also adds [non-destructive source gain](docs/SOURCE_NORMALIZATION.md): complete-file loudness analysis, bounded fixed gain, revision-bound persistence and Library reanalysis controls. Exact source cf39fd9c passed 179 unit cases and 148 real audio checks, including two independently decoded normalized synthetic AAC-LC/FLAC branches in run 38842202973. See [qualified source scope](artifacts/evidence/source-normalization-179-20261016.json). Final-mix limiting, full codec headroom and all-stream acceptance remain unfinished.

The [final-mix limiter and audible output timeline](docs/FINAL_MIX_LIMITER.md) passed exact-source qualification at 28638a57: 194 unit cases, 9 browser-model cases, 168 real audio checks, 35 SCM checks, 20 setup checks and 15 independent synthetic signals. See [scoped evidence](artifacts/evidence/final-limiter-194-20261016.json). Exact finite EOF/Stop/Skip/queue ownership, nonblocking journal writes and unchanged low-level 24-bit FLAC precision were verified. Full encoded headroom/all18, target-PC and connected operational acceptance remain unfinished.

## Current interface

The new dark browser console has a channel strip, broadcast desk, searchable/paged Library, folder picker, file-upload controls and System connection screen. It uses local HTML/CSS/JavaScript, without a frontend framework, CDN, external fonts or AI calls. Browser navigation and channel selection have been exercised with actual mouse input in the separate unconnected visual preview.

- Unconnected visual preview: loopback port 18120, clearly labelled UI PREVIEW.
- ASP.NET Core console under qualification: literal loopback port 18121. It bridges only isolated onair-next-dev-* engine pipes.
- Local mutations require a same-origin session cookie and request token. Production engine identities are not accepted.
- The broadcasting engine owns playback; closing or disconnecting the browser does not stop it. The guided Windows installer and self-contained package passed disposable-runner software qualification; actual Windows 10 installation, before-logon media/device access and connected mouse acceptance remain incomplete.

The active solution now builds OnAirNext.Web. On Air, Library and System are the initial screens; unfinished screens and unavailable controls are explicitly disabled. A visible screen is not proof of connected operation. Real browser upload/folder readback, remaining workflows, installation and Codex-free operator acceptance are still open.

## Windows setup under qualification

Exact source e852c2e1adddd72de6223de26f4c21f7a49cb3b3 compiled and passed **134/134 unit cases, 35 real service checks and 20 installer/console checks** in Windows run 38825230914. The actual self-contained package was installed on a disposable runner, with eight stopped channels and zero outputs. Packaged runtime loading, immutable-file hard links, repeated install/shortcut commands and console close without stopping the engine were verified. See [scoped setup evidence](artifacts/evidence/windows-setup-134-20261016.json) and [operator setup instructions](docs/WINDOWS_GUIDED_SETUP.md). The runtime encoder listing in this fixture is a test double: no real codec/audio/loudness acceptance is implied.

The launcher opens an operator-private console session. Anonymous session creation and API reads/writes are rejected; the startup capability stays in the operator profile and is removed from the address bar by the UI. This does not store a Windows password. Connected mouse/UAC acceptance on the broadcasting PC remains incomplete.

The measured package payload is 274,556,343 bytes logically extracted, with 112,451,385 unique file bytes; .NET is included, FFmpeg/music/credentials are excluded. These are package content measurements, not download size or target-PC RAM usage. Its [Windows runtime dependency graph is pinned](src/OnAirNext.Setup/packages.lock.json); the fully locked package passed the same complete qualification. No service has been installed on the broadcasting PC and no station has migrated.

## Loudness instrument foundation

Newer source adds a [float programme pipeline and bounded measurement worker](docs/PCM_AND_METER_WORKER.md), with final-mix browser readouts, immutable prefix history and scoped real Windows loopback qualification. Exact source `e3b19c5b` passed 171 unit cases, 7 browser-model cases, 95 real audio checks, 35 SCM checks, 20 setup checks and nine independent synthetic signals in run 38835930561. The decoded 24-bit stereo FLAC precision case compared 135,808 frames with zero error. These are not all-18-output, HE-AAC, normalization/limiter, actual-PC or connected mouse acceptance. Earlier foundation evidence below is scoped to its stated source.

A bounded stereo 48 kHz BS.1770-5/EBU-mode measurement core has scoped synthetic qualification: exact source `6488c439` passed 160 unit cases, 35 service checks, 20 installer/console checks and nine independent FFmpeg signal comparisons in run 38830016334. It provides K-weighted gated I, rectangular M/S, LRA and four-times oversampled true peak. That foundation did not alter audio or attach to the programme loop. See [measurement scope and evidence](docs/LOUDNESS_MEASUREMENT.md); normalization, limiting, full UI and all 18 real decoded-output measurements remain unfinished.

## Engine and library foundation

- Continuous 48 kHz stereo programme timeline, pooled 20 ms blocks and bounded decoders.
- Independent persistent outputs and owned-child cleanup. A failed output must not rewind healthy siblings.
- Explicit HE-AACv2 configuration requires libfdk_aac; no quality substitution.
- Dedicated FULL-WAL SQLite writer, exclusive database ownership, immutable attempt metadata and hash-chain verification.
- Unfinished items restart from the beginning as linked attempts; completed items remain consumed.
- Durable enabled/run/stop intent, per-station lifecycle workers and ID/payload deduplication.
- Distinct durable jingle/sweeper counters; actual legacy behavior import/planner integration remain incomplete.
- Per-station live-root revisions, stable recursive discovery, complete decode validation outside the programme loop, bounded import jobs and paged catalog reads.
- Interrupted copy jobs resume without overwriting originals. Missing-drive history is retained. External folders are watched read-only in the isolated build; copying is confined to private managed storage.
- Published in-memory library rotation uses bounded selection work. Versioned metadata editing, reset-to-file-tags and reversible removal/restore are implemented, with catalog/worker tests. Connected mouse acceptance remains pending; playlists, queue editing and resource-budgeted analysis remain unfinished.

## Evidence and release limits

Exact source 10862c903e4154596878fb03fa638cd995073500 compiled in Windows CI and passed **123/123 unit cases**, plus all 35 real Windows-service lifecycle/source/file-transfer checks (run 38814727175). Digest-verified [file-transfer evidence](artifacts/evidence/file-transfer-123-20261016.json) covers cross-account byte integrity, retained-file retry, a stalled transfer alongside responsive station controls, anonymous-client rejection, unchanged source ACLs and cleanup preserving original files. See the [file-transfer workflow](docs/LIBRARY_FILE_TRANSFERS.md). These are disposable-runner software checks; target-PC setup, connected mouse workflows and audio commissioning remain incomplete. The earlier 107-case source-access evidence is retained in the [delivery ledger](docs/BROADCAST_PC_DELIVERY_LEDGER.md).

A protected local build was stopped after an existing Classic transport-health flag changed. No legacy PID/generation changed, the guardian verified zero remaining test processes, and later snapshots recovered. That observation is not proof of a listener outage or its cause. Remote builds reduce load on the broadcasting PC.

Read the [delivery ledger](docs/BROADCAST_PC_DELIVERY_LEDGER.md), [operating plan](docs/OPERATING_PLAN.md) and [broadcast-PC handoff](docs/BROADCAST_PC_HANDOFF.md). The following remain mandatory before release:

- All eight AAC-LC192 + eight libfdk_aac HE-AACv2 64 + two Ogg FLAC outputs: eighteen total.
- Genuine BS.1770-5 / EBU R128 loudness and oversampled true peak, with independently decoded output measurements. Sample limiting is not compliance.
- Complete browser workflows, installed service, microphone/cue, mic-only recording, soft-boundary events, branding, reports and migration/rollback.
- Target-PC resource/device verification and real full-roster 24-hour and 72-hour qualification.
- UI-driven migration of one station codec group at a time. Retire the old service only after every required group and gate passes; retain rollback files and media.

No production readiness, completed installer or full operator application is claimed. All AI functionality is deferred.

## Development

Use the pinned private SDK and local SSD build root from scripts/Setup-Dev.ps1. Dependencies are locked. Check broadcast health and resource headroom before any target-PC build or audio test; do not launch a duplicate full fleet on this PC. GitHub Windows CI builds the solution and retains test results/binaries without using production credentials or media.

The developer verification entry point is scripts/Test-Engine.ps1. Loopback scenarios include baseline, recovery, durable, lifecycle, finite, offline, live and controlled failure cases. They do not substitute for browser mouse acceptance or the final eighteen-output/long-duration gates.

| Component | Purpose |
|---|---|
| src/OnAirNext.Engine | Programme audio, output recovery, durable journal, library and reports |
| src/OnAirNext.Host | Isolated developer engine and bounded local command pipe |
| src/OnAirNext.Web | Local browser console and protected HTTP-to-engine bridge |
| tests/OnAirNext.Tests | Focused domain/ownership/recovery/library tests |
| tests/OnAirNext.Lab | Owned loopback fixtures and independent listener analysis |
| artifacts/evidence | Sanitized summaries; no credentials, database or media |

Public source: [akgularda/radiotedu-onair-next](https://github.com/akgularda/radiotedu-onair-next). Report archive remains private. Never commit credentials, real databases, recordings or music.

The source now includes an isolated, pinned-source Windows codec dependency build and real AAC-LC/FDK HE-AACv2/24-bit FLAC smoke qualification. Its result is pending; see [codec runtime scope](docs/CODEC_RUNTIME.md). The qualified engine runtime and production ownership are unchanged.
