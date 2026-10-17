# Protected production output commissioning

Development hosts continue to accept literal loopback destinations and `/next-*` mounts only. Production Icecast settings require all three of these conditions: the host configuration explicitly sets `Commissioning: true`, the process runs as the installed Windows service, and every output is marked `Commissioned: true` with validated station information.

Each commissioned source checks the former `RadioTEDU.OnAir.Supervisor` service immediately before connecting and before every retry. The old service must report `STOPPED`, have no process ID, and have startup set to `DISABLED`. A stopped but still automatic service is not a release. If the service cannot be queried, the new source stays disconnected. This protects all configured mounts when the old service is released as one unit; EN/FR mount names are rejected.

Station information is sent in Icecast source headers from validated UTF-8 values. This sets stream-level name, description, genre, website and public-directory state. It does not claim per-track title updates, listener delivery, or encoded-output loudness compliance.

## Existing broadcast inventory

The broadcast PC's authenticated legacy API was read without updating its database. The current eight active RadioTEDU stations are IDs 1, 2, 4, 5, 8, 9, 10 and 11. IDs 7 is disabled/unmapped. The old source has eight AAC-LC/192 mounts and six enabled HE-AACv2/64 mounts; Classic and Cazz also have Ogg FLAC mounts. The remaining two HE-AAC mounts required by the requested 18-output target do not appear in the legacy enabled-output inventory and need origin mount names and source ownership confirmed before they are created.

| Station | Music root | Primary | Enabled extra outputs | Jingle root |
|---|---|---|---|---|
| Classical (1) | `H:\RadioTEDU Songs\Classical` | `/classic` | `/classic-low`, `/classic-flac` | `H:\RadioTEDU Songs\Ads\Jingles\Classical` |
| Lo-Fi (2) | `H:\RadioTEDU Songs\lofi` | `/lofi` | `/lofi-low` | `H:\RadioTEDU Songs\Ads\Jingles\Lofi` |
| Pop (4) | old managed import; confirm its current contents against `H:\RadioTEDU Songs\Pop` before selecting the live-folder root | `/radio` | `/radio-low` | `H:\RadioTEDU Songs\Ads\Jingles\Pop` |
| Jazz (5) | `H:\RadioTEDU Songs\Jazz` | `/cazz` | `/cazz-low`, `/cazz-flac` | `H:\RadioTEDU Songs\Ads\Jingles\Jazz` |
| Rock (8) | `H:\RadioTEDU Songs\Rock` | `/rock` | `/rock-low` | `H:\RadioTEDU Songs\Ads\Jingles\Rock` |
| Energize (9) | `H:\RadioTEDU Songs\Energize` | `/energize` | `/energize-low` | `H:\RadioTEDU Songs\Ads\Jingles\Energize` |
| Situation Room (10) | `H:\RadioTEDU Songs\Situation Room` | `/situation` | none reported | empty |
| Main Character (11) | `H:\RadioTEDU Songs\Main Character` | `/maincharacter` | none reported | not set |

All eight primary streams use the same configured Icecast host/port and 192 kbps AAC-LC source profile. The six enabled HE-AAC streams use 64 kbps libfdk_aac. The two FLAC outputs use Ogg FLAC. Legacy reports show the local origin currently has 40 configured source slots and a recommendation of 20 for the requested roster. The legacy diagnostics did not verify listener delivery for the 14 enabled local mounts during this inspection; source-handshake state and listener delivery are distinct facts.

The source credentials were read only to a user-local file protected by Windows DPAPI (machine scope) and an ACL granting access to the signed-in operator, SYSTEM, Administrators and LocalService. Plain API token and plaintext output inventory were removed after protection. Credentials and media are excluded from this repository.

## Handover bound

Do not stop the former service until the complete new service package, host configuration, media roots and rollback action are prepared. At cutover, stop and disable the old Supervisor service before enabling any commissioned output; the new engine refuses each source handshake while the former service is running or automatic. Restore the old service's former `Automatic` startup type and start it if the new roster cannot be verified promptly. The user authorized up to one hour with the former service stopped; this does not remove the source identity, metadata, codec, decoded-audio, log or rollback checks.
