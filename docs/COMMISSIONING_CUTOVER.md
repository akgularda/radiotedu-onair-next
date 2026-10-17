# Protected production output commissioning

Development hosts continue to accept literal loopback destinations and `/next-*` mounts only. Production Icecast settings are overlaid from a complete commissioning profile stored in the installed service's protected data directory. The profile is LocalMachine-DPAPI encrypted and ACL-checked; its source passwords are not written as plaintext. The installed Windows service loads it only after validating the exact eight-station, eighteen-output roster and station metadata.

The authenticated service pipe accepts a profile update only while all eight channels and their outputs are stopped. It keeps a protected previous-profile copy and restarts the service after saving, so the new settings are validated from disk before they can be used. A clear-profile command preserves that backup and returns the service to the loopback-only base configuration.

Each commissioned source checks the former `RadioTEDU.OnAir.Supervisor` service immediately before connecting and before every retry. The old service must report `STOPPED`, have no process ID, and have startup set to `DISABLED`. A stopped but still automatic service is not a release. If the service cannot be queried, the new source stays disconnected. This protects all configured mounts when the old service is released as one unit; EN/FR mount names are rejected.

Station information is sent in Icecast source headers from validated UTF-8 values. This sets stream-level name, description, genre, website and public-directory state. It does not claim per-track title updates, listener delivery, or encoded-output loudness compliance.

## Existing broadcast inventory

The broadcast PC's authenticated legacy API was read without updating its database. The current eight active RadioTEDU stations are IDs 1, 2, 4, 5, 8, 9, 10 and 11. ID 7 is disabled/unmapped. The old source has eight AAC-LC/192 mounts and six enabled HE-AACv2/64 mounts; Classic and Cazz also have Ogg FLAC mounts. The requested final roster adds `/situation-low` and `/maincharacter-low` using the established `-low` mount convention. Confirm each new mount accepts a source before counting it as active.

| Station | Music root | Primary | Enabled extra outputs | Jingle root |
|---|---|---|---|---|
| Classical (1) | `H:\RadioTEDU Songs\Classical` | `/classic` | `/classic-low`, `/classic-flac` | `H:\RadioTEDU Songs\Ads\Jingles\Classical` |
| Lo-Fi (2) | `H:\RadioTEDU Songs\lofi` | `/lofi` | `/lofi-low` | `H:\RadioTEDU Songs\Ads\Jingles\Lofi` |
| Pop (4) | old managed import; confirm its current contents against `H:\RadioTEDU Songs\Pop` before selecting the live-folder root | `/radio` | `/radio-low` | `H:\RadioTEDU Songs\Ads\Jingles\Pop` |
| Jazz (5) | `H:\RadioTEDU Songs\Jazz` | `/cazz` | `/cazz-low`, `/cazz-flac` | `H:\RadioTEDU Songs\Ads\Jingles\Jazz` |
| Rock (8) | `H:\RadioTEDU Songs\Rock` | `/rock` | `/rock-low` | `H:\RadioTEDU Songs\Ads\Jingles\Rock` |
| Energize (9) | `H:\RadioTEDU Songs\Energize` | `/energize` | `/energize-low` | `H:\RadioTEDU Songs\Ads\Jingles\Energize` |
| Situation Room (10) | `H:\RadioTEDU Songs\Situation Room` | `/situation` | `/situation-low` (new HE-AACv2 64 kbps) | empty |
| Main Character (11) | `H:\RadioTEDU Songs\Main Character` | `/maincharacter` | `/maincharacter-low` (new HE-AACv2 64 kbps) | not set |

The legacy library has 27 active jingles: Classical 7, Pop 6, Jazz 7 and Rock 7. Lo-Fi, Energize and Situation Room have none. Jingle rescans run every five minutes. Legacy sweeper settings are enabled for Classical every three tracks, Pop every two, Jazz every three and Rock every three; however, all four saved sweeper IDs (21684–21687) are inactive, so there is no currently playable sweeper inventory. Do not reactivate those records as part of migration. Hourly advertisements, startup sound and AI startup announcements are disabled. The old hourly ad folder is shared and its selected tracks are not scheduled while hourly insertion is off.

All eight primary streams use the same configured Icecast host/port and 192 kbps AAC-LC source profile. The six enabled HE-AAC streams use 64 kbps libfdk_aac. The two FLAC outputs use Ogg FLAC. Legacy reports show the local origin currently has 40 configured source slots and a recommendation of 20 for the requested roster. The legacy diagnostics did not verify listener delivery for the 14 enabled local mounts during this inspection; source-handshake state and listener delivery are distinct facts.

The source credentials were read only to a user-local file protected by Windows DPAPI (machine scope) and an ACL granting access to the signed-in operator, SYSTEM, Administrators and LocalService. Plain API token and plaintext output inventory were removed after protection. Credentials and media are excluded from this repository.

## Handover bound

Do not stop the former service until the complete new service package, host configuration, media roots and rollback action are prepared. At cutover, stop and disable the old Supervisor service before enabling any commissioned output; the new engine refuses each source handshake while the former service is running or automatic. Restore the old service's former `Automatic` startup type and start it if the new roster cannot be verified promptly. The user authorized up to one hour with the former service stopped; this does not remove the source identity, metadata, codec, decoded-audio, log or rollback checks.
