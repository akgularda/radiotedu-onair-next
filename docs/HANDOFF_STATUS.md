> Latest development update, 16 October 2026: source `384cc67a` passed the complete isolated FDK/FLAC 18-output scenario with zero measurement drops/history failures and confirmed Stop/hash checks. See [205-case qualification](../artifacts/evidence/fdk-full18-205-20261016.json). Actual-PC and connected mouse commissioning remain open; zero station groups migrated.

> **Current UI decision:** the user rejected the native prototype and requested a browser app. [Browser interface decision](./BROWSER_UI_DECISION.md) supersedes earlier native-only UI requirements. The new browser build is under qualification; production remains with the old application.

Exact source `28638a57` passed 194 unit cases, 9 browser-model cases, 168 real audio checks across nine scenarios, 35 SCM checks, 20 setup checks and 15 independent synthetic signal comparisons in [run 38847512000](https://github.com/akgularda/radiotedu-onair-next/actions/runs/38847512000). All six evidence archive digests and execution source identities were verified. See [scoped evidence](../artifacts/evidence/final-limiter-194-20261016.json). A native final-mix limiter, audible-only ownership and bounded asynchronous audit now passed their scoped fixtures. Worst-case encoded headroom/all18/HE-AAC, full official corpus, target-PC/connected mouse and24/72h acceptance remain. Zero station groups migrated; the old application retains production ownership.

# Handoff snapshot status

Latest qualified continuation: float32 programme/mic/encoder pipes, 24-bit FLAC, exact partial EOF, bounded final-mix metering, immutable measured-prefix history/restart handling and browser readouts/history are now in source. Exact source e3b19c5b passed 171 unit cases, 7 browser-model cases, 95 real audio checks across seven scenarios, 35 SCM checks, 20 setup checks and nine independent signals in completed successful push run 38835930561. All six evidence digests and execution sources match. Actual mouse channel selection/scrolling passed only in the unconnected preview; connected operation remains unproven. See [current pipeline scope](PCM_AND_METER_WORKER.md). No new runtime or service has been launched on the broadcasting PC.

Loudness continuation: exact source 6488c439 passed 160 Windows unit cases, 35 SCM checks, 20 installer/console checks and nine independent synthetic signals in completed successful run 38830016334. All evidence digests and execution source identities match. The preceding short-programme LRA criterion failure is retained separately. It is not connected to the programme loop, does not normalize/limit audio and does not certify any output. See [instrument scope and remaining work](LOUDNESS_MEASUREMENT.md). The live legacy engine is unchanged; zero station groups have migrated. Migration remains one station's codec group at a time.

Current continuation, 16 October 2026: exact source e852c2e1 passed 134 unit cases, 35 completed SCM checks and 20 completed installer/console checks on a disposable Windows runner. The self-contained Windows package, private runtime loading, protected immutable-file deduplication, same-plan retry, per-user shortcuts, build identity and console close without service restart were exercised. See [setup evidence](../artifacts/evidence/windows-setup-134-20261016.json), [guided setup](WINDOWS_GUIDED_SETUP.md) and the [complete delivery ledger](BROADCAST_PC_DELIVERY_LEDGER.md). The encoder-list fixture has no real audio implementation. The reviewed framework and Windows runtime graph both restored in locked mode during the successful package qualification. Actual Windows 10 installation, resource admission, UAC/mouse/file-chooser and before-logon source/device tests remain incomplete. Zero groups have migrated, and the former application still owns all broadcasts. Historical records below are not a release declaration.

Prepared 15 October 2026. This is a development snapshot for the broadcasting-PC agent, not a production-ready release.

## Included

- Previously committed independent engine/lab milestone.
- All subsequent source changes to journal/checkpoints, decoder offsets, planner contracts, station engine and host commands. These are **unfinished recovery work**.
- Initial `OnAirNext.Application` contracts/storage and its dependency lock. It builds separately but is not included in `OnAirNext.slnx` or connected to a native UI.
- Approved development/operating plans, 162-entry legacy feature inventory, source-declared API/model appendix and broadcasting-PC migration handoff.
- Sanitized historical evidence and a new focused baseline result.

## Broadcasting-PC continuation

- Restart-from-beginning with linked attempts is implemented. Desired enabled/running intent, durable stop receipts, cross-operation command validation and dedicated journal writer are implemented.
- Windows 10 broadcasting-PC validation: 42 unit cases; 13 real hard-kill/desired-state checks; 17 reconnect/encoder-recovery checks from the dedicated-writer regression. Public summaries are in `artifacts/evidence/hp-recovery-20261015.json` and `hp-desired-state-20261015.json`.
- An initial native dark WPF desktop is now in the solution and builds. It offers On air/System for isolated engines only. No Windows service installer, full operator workflow or production UI commissioning has shipped.
- Native window launch and accessibility readback succeeded. Keyboard close succeeded. Screenshot/mouse qualification failed from Computer Use capture/geometry errors and must not be marked passed.

## Original Windows 11 verification

| Check | Result |
|---|---|
| `scripts/Test-Engine.ps1 -Scenario baseline -SkipBootstrap` locked solution restore/build | Passed; zero warnings/errors. |
| Existing unit tests | 24 passed, zero failed. |
| Real loopback baseline | Passed, 22 checks, AAC-LC and Ogg FLAC listener decoding. |
| Separate `dotnet build src/OnAirNext.Application/OnAirNext.Application.csproj -c Release` | Passed; zero warnings/errors. |
| New baseline evidence | `artifacts/evidence/20261015-144431-0d1a4fc9.json`, generated at 14:44 UTC / 17:44 Europe/Istanbul. |
| Active GitHub identity and source repository | akgularda; akgularda/radiotedu-onair-next is public following the user's visibility correction. The report repository remains private. |

These checks were run on the Windows 11 development host using the private SDK and isolated local Icecast. The Application build is compilation evidence only. No new crash-policy, service, UI, 18-output HE-AAC or production migration acceptance is claimed. The older eight-scenario and fleet evidence describes the earlier engine milestone, not complete acceptance of these unfinished changes.

## Required fixes and missing acceptance

1. Complete the full crash-boundary/uncertainty matrix for the implemented linked restart-from-beginning policy; current focused recovery evidence is not full-product acceptance.
2. Finish runtime lifecycle/controller integration, transition ownership and the remaining command/crash-boundary tests. Desired-state persistence and receipt validation have focused passing evidence. Review synchronous/awaited disk commits in the audio path and recovered snapshot state.
3. Replace/extend the initial single-studio storage model for eight station folders, durable shuffled selection, distinct jingle/sweeper counters, indexed/virtualized views and safe migration.
4. Implement the service installer/startup/recovery, service/client pipe permissions, service-account credential/media access and Windows 10 compatibility path.
5. Obtain/build and qualify libfdk_aac capability. Independently decode the actual eight AAC-LC/eight HE-AACv2/two FLAC roster. Missing FDK must never trigger silent codec fallback.
6. Implement the connected English native UI and every routine configuration/content operation, including guided legacy import, staged handover and rollback.
7. Finish microphone capture, private cue and conditional microphone-on-air program recording. Music-only broadcasts and private mic tests must not create recordings.
8. Finish scheduled local/GitHub report publishing, catch-up, UI credential management, full annual-volume export and the remaining approved workflows.
9. Run target-PC resource/driver tests and long-duration qualification. Discover actual legacy jingle/sweeper/media/output configuration before migrating one station group at a time.

The legacy installation, databases, media and live broadcasts were not changed on this development host. No production credentials, database, audio assets, runtime binaries or private test configuration belong in this repository. Follow `docs/BROADCAST_PC_HANDOFF.md` for the user's newly authorized broadcasting-PC transfer and eventual legacy-service retirement.
