# OnAir Next engineering instructions

Read `README.md` and `DEVELOPMENT_PLAN.md` before changes. This is a new standalone implementation. The sibling reference checkout and release archives are read-only requirements material.

- Keep production installations, services, databases, mounts, audio libraries and credentials untouched.
- The later user-authorized broadcasting-PC handoff is documented in `docs/BROADCAST_PC_HANDOFF.md`. On that PC only, after its build/verification gates, production changes are limited to the UI-driven one-station-at-a-time transfer and final legacy-service retirement described there. Development and loopback tests must still leave live broadcasts untouched.
- This development host deliberately allows only literal loopback destinations and `/next-` mounts. Do not relax this boundary to make a test pass.
- Use the private SDK and local SSD build root created by `scripts/Setup-Dev.ps1`. Do not generate runtime databases, locks, binaries, credentials or media inside Google Drive.
- The active GitHub account must remain `akgularda`. Private source and reporting repositories must stay private.
- Never log or commit source credentials, real databases, private configuration or audio assets.
- Codec I/O uses overlapped Windows named pipes. Do not replace it with shared-pool blocking stdio without full fleet timing evidence.
- A failed or slow output may recover itself; it must not rewind the program or restart healthy siblings.
- Preserve configured audio quality. Reject a missing encoder rather than changing codecs.
- Start and terminal event positions must retain exact PCM sample counts. Do not label queued/programmed content as confirmed listener delivery.
- Run focused tests and real loopback scenarios for an engine change; use `scripts/Test-Engine.ps1`. Record failures as well as successful evidence.
- Do not claim production readiness from a short development run. Keep implementation limitations and commissioning gates current.
