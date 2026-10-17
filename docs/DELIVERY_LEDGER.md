# Browser validation notes

The full, current product acceptance ledger is [Broadcast-PC delivery ledger](BROADCAST_PC_DELIVERY_LEDGER.md). The entries below retain earlier browser-stage observations.

### Browser UI revision and validation — 15 October 2026

- Native UI was explicitly rejected by the user; active application is now the browser console.
- Source `22cc4538208a683a9d856239f00e0af2aa6687f6`: Windows build and 77/77 tests passed in run 38794289373. Browser artifact is 324,093 bytes unpacked and isolated Windows host artifact 2,662,521 bytes unpacked; these exclude .NET/encoders/music.
- Fixed compact transport clipping, separate Stop and station-bound confirmation, station-bound folder selection, real current/next/duration snapshots, and unnecessary browser SQLite dependency.
- Source-only dependency changes initially failed compilation (EventJournal path helper); the bounded path helper was extracted and the final build passed. These failed runs are not acceptance evidence.
- Mouse checks cover the unconnected preview, including the actual target scale equivalent 911×512. Static UI matches the successful build except checkout newlines.
- Connected library QA has not passed: the resource guard stopped its owned job on legacy transport health changes before application readiness. Zero owned processes remained; production was not restarted. See artifacts/browser-ui-validation-20261015.json.
