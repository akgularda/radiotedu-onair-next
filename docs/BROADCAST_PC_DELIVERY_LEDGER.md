# Broadcast-PC delivery ledger

Updated 15 October 2026. This is an implementation and evidence ledger, not a release declaration.

## Current user decisions

- Deliver one integrated native Windows product with a broadcast-program interface and tray behavior. The latest clarification proposes a Windows service so broadcasting can start before logon; native WPF controls the same product engine. Windows session isolation requires service and interactive UI processes. An earlier same-process interpretation is superseded. All operation, installation, recovery, configuration and migration must be possible through the native UI with mouse and keyboard.
- Research real radio automation interfaces. Actual official PlayIt Live and mAirList screen pixels have been inspected; RCS Zetta currently has documentation research. The latest visual direction is dark and Palantir-like; literal mAirList styling is rejected. Blueprint dark-theme pixels have also been inspected. Use original native WPF resources, explicit state text, a dominant running order and readable Now/Next clocks. Do not use a generic decorative dashboard or a browser wrapper.
- Keep the application lightweight on low-spec PCs. Respect the measured broadcast PC's headroom. A preflight with 88.7% one-second CPU and about 1.22 GiB available RAM deferred the additional audio laboratory rather than starting a second production fleet.
- Import jingle/sweeper assets and behavior from the actual current application. Do not apply one blanket cadence to all stations. Preserve disabled or missing collections in the import preview. Keep distinct content types and durable counters available; show any change from the imported legacy mapping explicitly. Earlier two/three-song defaults must not silently overwrite current per-station rules.
- Keep every actual broadcast attempt locally, with honest timing/uncertainty and all stations represented. Daily private GitHub publication may use the current program's existing report repository; radiotedu-broadcast-repor is not mandatory. Publication failures require persistent retry/catch-up and verified commit readback.
- Support Icecast source information and metadata according to official protocol/codec capabilities. Source handshake, metadata acceptance, server statistics and independent listener decoding are different evidence states. Never report all as green merely because a source socket connected.
- No AI features. No production changes or retirement until the full qualification and migration gates pass.

## Implementation and evidence

| Requirement | Status and remaining gate |
|---|---|
| Preserve legacy broadcasts | No legacy source/config/database/service mutation performed. Eight worker heartbeat files remained fresh during development; transport/PCM fields are inspected separately. This is not listener/audio proof. |
| Separate source and data | Fresh main clone in RadioTEDU-OnAir-Next-20261015; SDK, packages, lab origin and build output are separate under local application data. |
| Reproducible build | Official .NET SDK 10.0.402 archive verified against official release metadata. Initial restore failed from missing tool-process APPDATA; failure retained. Corrected environment produced a zero-warning, zero-error solution build and 32 passing unit tests. Subsequent recovery/cadence refinements passed 37 unit cases; desired-state changes passed 42. The separate native WPF project also compiled with zero warnings/errors. |
| Durable restart policy | Implemented new-attempt linkage, restart-from-zero selection, atomic interruption/checkpoint preparation, durable Skip recovery and duplicate-event payload checking. 42 unit cases now pass, including persistent stop intent and command receipt collisions. A real one-station hard-kill/decoded-listener scenario passed nine checks, including distinct restart linkage and a lost-response Skip retry after process death. First fixed-wait failure retained. A dedicated journal writer also passed 37 unit cases and 17 real reconnect/encoder-recovery checks. The extended end-to-end desired-state scenario passed 13 checks after correcting a client-side disposal/pipe-close race; the failed run remains retained. Stopped stations stay stopped after restart and receipt collisions do not shut down the command interface. The full recovery acceptance matrix is not complete. |
| Completion and ownership | Terminal event/checkpoint/queue consumption share a transaction. Complete crash-boundary, ad/event ownership, desired state and corrupt/missing-checkpoint qualification still required. |
| Command deduplication | Durable Skip binds the observed attempt separately from client payload; same-ID different-payload reuse is rejected. Durable enabled/running intent and atomic all-station stop receipts are implemented with five unit cases. Full lifecycle/controller routing and end-to-end verification remain. |
| Windows service and native UI integration | A native WPF desktop builds and opens, discovers isolated local engines, reads status and offers a selected-station Skip. No service installed or production operator desktop shipped. Complete controller, installation, tray and remaining workflows are not delivered. |
| Eighteen required outputs | Eight AAC-LC192, eight HE-AACv2 64 with libfdk_aac, two Ogg FLAC remain mandatory. Existing synthetic 16-output evidence is not this acceptance proof. |
| Audio continuity | Continuous engine/named-pipe baseline exists. Newly changed recovery needs listener testing. Empty/corrupt/drive-loss emergency content, crossfades, transition ownership and full target-resource qualification remain. |
| Loudness | BS.1770-5/R128 gated integrated loudness, genuine true peak and independently decoded measurements for all 18 outputs are not implemented/qualified. Sample limiting is not compliance. |
| Legacy migration and branding | Read-only discovery captured actual channel settings and source function semantics. New per-station jingle/sweeper completion counters persist atomically and have boundary tests. Import wizard, verified backup, path/media validation, effective broadcast-plan overrides, legacy counter migration and saved-state readback remain. No media copied or tags edited. |
| Live folders | Stable file discovery, decode validation, rename/change/remove/drive-loss, shuffled rotation and UI live-folder assignment remain. |
| Special days/programs | Soft-boundary scheduling, metadata override, return preview and full event ownership/recovery UI remain. |
| Microphone and private cue | Engine mixer proof exists; native device agent, enumeration/loss handling, private cue and UI workflows remain. |
| Mic-only recording | Conditional actual-program recording, quiet pauses, segmentation, disk failures and crash repair remain. No ordinary music-only recording may be enabled. |
| Complete reports | CSV restart linkage/timing-quality columns added. Midnight apportionment, full annual/roster coverage, outbox/publisher/credential UI and audit export still need completion and verification. |
| Native UI research | Two UI research documents saved by user-requested agents. Actual PlayIt/mAirList observations recorded. An original solid dark WPF theme and compiled desktop now exist. The real native window exposes On air/System via accessibility; Alt+S opens System and Alt+F4 closed the previous build. Work-area sizing, compact panels, stale-status command gating and stable output rows are implemented and compile, but their full interactive qualification remains open; there are no invented library/schedule controls or simulated audio meters. Computer Use screenshot capture failed twice (FrameArrived/window capture timeout); its mouse action failed because coordinate input geometry was unavailable. Mouse operations, visual/DPI verification, real connected readback and the remaining screens are not qualified. |
| Installer/update/repair | Native guided setup, separate service identity/ACLs, versioned installation, backup/restore and rollback remain. |
| Optional non-AI scope | Original approved capability inventory remains applicable; no capability is declared delivered by a mock/placeholder control. |
| GitHub identity and publication | Source must be published as akgularda. Account-specific credential/permission probe verified akgularda and push/admin access on the new repository; global account untouched. No production secrets/media/database may enter public source. |
| Target capacity | Actual HP has 8 GiB RAM and active legacy workload. One-worker low-priority builds used. Disk I/O measurement, combined child-process footprint, p95 control latency and sustained full-roster comparison remain. |
| Long qualification | Separate real 24-hour and 72-hour full-roster runs, injected failures, rollover and resource evidence remain. Short tests cannot substitute. |
| Production migration | None started. Each station's codec group must transfer separately with no duplicate ownership, independent decoded quality checks, measured gap and native UI rollback. |
| Legacy retirement | Not authorized by evidence yet. Only after all eight groups/18 outputs and complete observation gates pass; retain old installation/backups/media. |

## Local development evidence locations

Operational discovery and raw test/build logs are under the separate operations folder next-development. Never commit private discovery files or local generated source credentials. Public evidence must contain synthetic content and redacted configuration only.

- recovery-build-attempt1.json and restore-recovery-attempt1.log: retained initial developer environment failure.
- recovery-build-attempt2.json, build-recovery-attempt2.log, test-recovery-attempt2.log and test-results/recovery.trx: successful build/32-test snapshot before subsequent refinements.
- durable-verification-status.json and the isolated run ending 160547-a5c890ae: nine-check listener-confirmed hard-kill scenario passed. Prior resource deferrals and failures retained. Public bounded summary: artifacts/evidence/hp-recovery-20261015.json.
- journal-regression-status.json and run 20261015-162353-90232402: 37 unit cases and 17 real reconnect/encoder recovery checks passed after dedicated journal-thread change.
- desired-state-verification-status.json: 42 unit cases and 13 real desired-state/recovery checks passed in run 20261015-164826-0faf4b84. Failed run 20261015-163911-547601cb is retained; public summary: artifacts/evidence/hp-desired-state-20261015.json.
- desktop-build-status.json and build-desktop.log: native WPF build succeeded with zero warnings/errors. Window launched as OnAirNext.Desktop.exe; Alt+S and Alt+F4 keyboard operations passed. No successful mouse/screenshot or connected-operation qualification yet. Public summary: artifacts/evidence/native-ui-20261015.json.
- legacy-branding-discovery.json: private read-only capture, including actual media paths; not public source.

Continue the delivery order in OPERATING_PLAN.md. Do not mark this task complete until every required gate is actually satisfied.
