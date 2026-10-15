# RadioTEDU OnAir Next

Independent radio broadcasting engine and local browser console under development. The current RadioTEDU installation continues broadcasting. **No production migration has occurred.**

The user rejected the WPF prototype and requested a browser app. [The browser interface decision](docs/BROWSER_UI_DECISION.md) supersedes earlier native-only UI clauses. The rejected prototype remains historical source and is no longer part of the active solution.

## Current interface

The new dark browser console has a channel strip, broadcast desk, searchable/paged Library, folder picker, file-upload controls and System connection screen. It uses local HTML/CSS/JavaScript, without a frontend framework, CDN, external fonts or AI calls. Browser navigation and channel selection have been exercised with actual mouse input in the separate unconnected visual preview.

- Unconnected visual preview: loopback port 18120, clearly labelled UI PREVIEW.
- ASP.NET Core console under qualification: literal loopback port 18121. It bridges only isolated onair-next-dev-* engine pipes.
- Local mutations require a same-origin session cookie and request token. Production engine identities are not accepted.
- The broadcasting engine owns playback; closing or disconnecting the browser does not stop it. An isolated Windows-service lifecycle fixture has passed on a disposable CI runner; guided installation, real before-logon startup and target-PC media access are still incomplete.

The active solution now builds OnAirNext.Web. On Air, Library and System are the initial screens; unfinished screens and unavailable controls are explicitly disabled. A visible screen is not proof of connected operation. Real browser upload/folder readback, remaining workflows, installation and Codex-free operator acceptance are still open.

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
