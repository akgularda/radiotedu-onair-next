# Queue process-crash qualification

`queue-durable` exercises the real developer host, persisted library and typed command pipe against a disposable local Icecast. It is restricted to a disposable CI runner. Do not run this writer-stall/process-kill fixture on the broadcasting PC. `scripts/Test-WindowsAudio.ps1` invokes it after the ordinary queue scenario.

The scenario configures its own generated media, three isolated codec destinations (AAC-LC, FDK HE-AACv2 and Ogg FLAC), three queued music occurrences and independent two-song jingle/three-song sweeper rules. No source, configuration, service, database or media from the broadcasting PC is used.

It checks these distinct boundaries:

- Kill after a queued song's distinctive opening has passed. Independently decode its opening again after restart, and verify a new attempt linked to its interrupted predecessor.
- Kill immediately after durable Skip admission. Retry the same Skip and original consumed append request; only the intended occurrence may be consumed and neither request may duplicate another action.
- Hold SQLite's writer reservation while real decoded samples cross EOF into the next item. Kill before the completion/checkpoint transaction can commit. The last committed unfinished item must restart from its beginning. The uncommitted audible tail remains explicitly unknown in recovery history.
- Kill after a completion was committed and the following item began. The completed queue entry must stay consumed, while only the unfinished following item restarts.
- Verify cadence survives these restarts: skipped/interrupted/uncommitted attempts do not count as completed music, two genuine completions produce one jingle and leave the independent sweeper counter at two.
- Persist an explicit Stop, restart the host and verify no codec branch or additional attempt is started. Verify the immutable event chain and owned-child cleanup.

The deliberate crashes create source gaps. Decoded opening/transition checks do not claim uninterrupted listener delivery across a killed process. This scenario does not cover the actual Windows service/power path, physical devices, schedule ownership, browser mouse operation, every possible commit race, worst-case loudness or 24/72-hour operation. Those remain commissioning gates.

This source change adds the scenario; results must be tied to the exact executed source and original retained CI artifacts before it is described as passed. Previously qualified source `6834746e8a3fac68132c25731005a8325ecad897` did not run this scenario.

The [first completed run](../artifacts/evidence/queue-durable-first-failure-20261017.json) passed 48 checks, then failed a fixture assertion that expected a stopped channel to have no configured output profiles. The coordinator intentionally keeps these profiles visible as stopped. The corrected check retains all three profiles and requires stopped state, no encoder PID, zero encoder/connection/frame/queue counters and an independent HTTP404 for every listener mount. It also retains the actual stopped snapshot in both successful and failed evidence. Product Stop/recovery semantics were not relaxed; fresh exact-source qualification remains required.
