# Handoff snapshot status

Broadcast-PC continuation: see the [delivery ledger](BROADCAST_PC_DELIVERY_LEDGER.md) for current 37-unit/9-check isolated recovery evidence and incomplete release gates. The native UI is being developed in the requested dark Palantir-like direction; production has not been migrated.

Prepared 15 October 2026. This is a development snapshot for the broadcasting-PC agent, not a production-ready release.

## Included

- Previously committed independent engine/lab milestone.
- All subsequent source changes to journal/checkpoints, decoder offsets, planner contracts, station engine and host commands. These are **unfinished recovery work**.
- Initial `OnAirNext.Application` contracts/storage and its dependency lock. It builds separately but is not included in `OnAirNext.slnx` or connected to a native UI.
- Approved development/operating plans, 162-entry legacy feature inventory, source-declared API/model appendix and broadcasting-PC migration handoff.
- Sanitized historical evidence and a new focused baseline result.

## Verification for this snapshot

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

1. Replace checkpoint-position resume with the approved unfinished-item **restart from beginning** policy. Preserve the interrupted occurrence and create a linked actual restart attempt; do not report one uninterrupted occurrence.
2. Finish desired-state persistence, transition ownership, command ID/payload validation and crash-boundary tests. Review synchronous/awaited disk commits in the audio path and recovered snapshot state.
3. Replace/extend the initial single-studio storage model for eight station folders, durable shuffled selection, distinct jingle/sweeper counters, indexed/virtualized views and safe migration.
4. Implement the service installer/startup/recovery, service/client pipe permissions, service-account credential/media access and Windows 10 compatibility path.
5. Obtain/build and qualify libfdk_aac capability. Independently decode the actual eight AAC-LC/eight HE-AACv2/two FLAC roster. Missing FDK must never trigger silent codec fallback.
6. Implement the connected English native UI and every routine configuration/content operation, including guided legacy import, staged handover and rollback.
7. Finish microphone capture, private cue and conditional microphone-on-air program recording. Music-only broadcasts and private mic tests must not create recordings.
8. Finish scheduled local/GitHub report publishing, catch-up, UI credential management, full annual-volume export and the remaining approved workflows.
9. Run target-PC resource/driver tests and long-duration qualification. Discover actual legacy jingle/sweeper/media/output configuration before migrating one station group at a time.

The legacy installation, databases, media and live broadcasts were not changed on this development host. No production credentials, database, audio assets, runtime binaries or private test configuration belong in this repository. Follow `docs/BROADCAST_PC_HANDOFF.md` for the user's newly authorized broadcasting-PC transfer and eventual legacy-service retirement.
