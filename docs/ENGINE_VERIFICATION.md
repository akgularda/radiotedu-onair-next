# Engine milestone verification

Verified on 15 October 2026 using the private .NET SDK 10.0.402, .NET runtime 10.0.13, FFmpeg from the development PC, and official Icecast 2.5.0 extracted into the isolated local tools directory. The development PC is Windows 11 Insider build 29683 with 32 GB RAM and eight logical processors. It is not the HP Z220 broadcast PC.

## Completed tests

The release build succeeds with zero warnings and errors. All 24 unit tests pass, including production-destination rejection, PCM reference ownership, live-input queue bounds and limiting, duplicate journal acknowledgements, hash tamper detection, unknown schema rejection, Istanbul midnight boundaries, incomplete plays, CSV safety, and an export containing 10,025 occurrences.

The complete integration suite passes eight cases and 211 assertions:

| Test | Result | Evidence |
|---|---|---|
| Continuous transitions | Pass | Music, advertisement, sweeper and successor music retain exact sample lengths; AAC and FLAC each retain one encoder/source session |
| Encoder and origin recovery | Pass | An owned encoder is killed; its sibling remains unchanged; both outputs recover after restarting the owned Icecast process |
| Offline startup | Pass | The timeline advances while the origin is absent, then listener audio decodes after the origin becomes available |
| Eight stations and sixteen outputs | Pass | All programs remain active; all healthy outputs retain one session with no PCM drops; all sixteen listener streams decode |
| Live input and loss | Pass | Synthetic agent PCM is mixed and ducked; loss and return do not restart the source |
| Network backpressure | Pass | A real source socket accepts headers but stops reading; the affected output stays bounded and the healthy sibling remains uninterrupted in decoded content |
| Invalid upcoming media | Pass | A malformed file is rejected, an incident is recorded, and the approved successor plays without restarting the output |
| Independent host and IPC | Pass | Disconnecting a controller does not stop playback; identical Skip retries execute once; reusing the ID for Stop is rejected; explicit Stop exits cleanly |

A focused live-input follow-up also passed 13 assertions. It verified 75 decoded 20 ms windows of the distinct 1760 Hz synthetic microphone tone, or 1.5 seconds received by the listener, in addition to session continuity and recovery. The 24 unit tests passed again with an explicit clipping/microphone-addition assertion.

Checks inspect actual decoded listener PCM, not only TCP connections or source status. Synthetic tones identify the content order. The first 200 ms is excluded from continuing-stream RMS checks for decoder priming; subsequent low-RMS 20 ms windows and unexpected stable tone changes fail the healthy-stream tests. Short transient scheduling lateness is retained in the station snapshots rather than hidden.

## Longer development run

The 60-second fleet observation passed all 105 assertions. The whole case lasted about 66 seconds including setup and cleanup. It continuously decoded sixteen outputs without storing full audio recordings. Healthy outputs retained their initial encoders and source sessions with no PCM drops or unexpected tone order. No test-owned child processes remained at completion.

| Resource measurement | Observed |
|---|---|
| Instrumented engine/lab host peak working set | 82.0 MiB |
| All codec workers peak working set | 482.8 MiB |
| Sum of those separate peaks, a conservative upper bound | 564.8 MiB |
| Listener analysis workers peak, reported separately | 348.7 MiB |
| Measured host plus codec CPU average | 10.9 percent of this eight-logical-processor PC |
| Highest one-second CPU sample | 28.5 percent |

The host includes test instrumentation. Codec totals include active and prebuffered media decoders and sixteen encoders. Listener processes and the local Icecast server are separate from engine codec totals. Synthetic items last roughly 0.7 to 2 seconds, exercising frequent decoder transitions. These figures do not establish production capacity or compare against a measured legacy baseline.

Machine-readable evidence is preserved in `artifacts/evidence`:

- `20261015-125214-15166930.json`: initial source handshake failure.
- `20261015-125346-b6c82174.json`: the initial fleet scheduling failure.
- `20261015-125646-f387cda1.json`: fleet success after switching codec I/O to overlapped named pipes.
- `20261015-130243-708992f9.json`: complete eight-case suite with 211 successful assertions.
- `20261015-130523-0430752b.json`: successful 60-second fleet observation and resource measurements.
- `20261015-130801-81b2f895.json`: final complete eight-case regression after engine cleanup changes.
- `20261015-131144-ea85f77a.json`: focused live-input follow-up proving the added microphone tone reaches decoded listener audio.

The first handshake implementation waited for a final HTTP 200 response before starting the encoder, while Icecast waited for source bytes. The corrected implementation negotiates `100 Continue`. The initial fleet then exposed synchronous anonymous-pipe reads delaying shared thread-pool scheduling. Overlapped Windows named pipes now carry codec audio, and bounded dedicated readers retain stderr diagnostics. The original failures remain available to explain and reproduce the design decisions.

## Report archive

The private [broadcast report repository](https://github.com/akgularda/radiotedu-broadcast-reports) is owned by `akgularda`. Local fixture reports and checksums have been uploaded beneath `development-tests`, accompanied by a manifest with `IsSynthetic=true`. The published commit is read back from GitHub before success is reported. Repeating publication of the same run was verified to preserve the same remote commit.

`scripts/Publish-LabReports.ps1` is a development publishing helper. It verifies the active account and repository privacy, accepts only isolated lab run directories, stages only the corresponding synthetic report folder, refuses unrelated local changes, uses no force operations, and retains local reports if publication fails. This is not yet the application's scheduled daily publisher or its service-account authentication workflow.

## Production gates still open

- Native operator UI, library ingestion/editing, persisted queue and scheduling workflows.
- Durable admission/checkpoint recovery across hard engine/power failure. A bounded in-memory audit handoff currently precedes FULL WAL commits; pending events can be lost on hard termination.
- Physical WASAPI microphone capture, device loss, headphone cue isolation, and listening QA.
- The exact commissioned roster, including approved HE-AACv2 outputs; this lab used eight AAC-LC and eight FLAC branches.
- Tests on the actual Windows 10 Pro build and HP Z220 hardware.
- Automated daily scheduling, upload outbox/credential renewal in the product UI, annual submission template mapping, and hourly duration apportionment.
- Full 24-hour and 72-hour soak runs, import/report load, production-server session behavior, and migration/rollback rehearsals.

No production service was installed or restarted, no production source destination was contacted, and no live database or audio library was opened. Icecast processes were loopback-only and were stopped with the test resources they owned. A successful short run is evidence for that workload and duration, not a zero-downtime guarantee.
