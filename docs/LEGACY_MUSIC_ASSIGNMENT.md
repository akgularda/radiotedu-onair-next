# Guided music folder assignment

This is the music-folder step of the guided migration. It does not commission a station or replace the former broadcaster.

## Operator workflow

1. In System, prepare the legacy preview and save the complete station mapping after checking its recovery reference.
2. Select **Review music folder** for one saved row. The target channel must be explicitly stopped and have no configured outputs. Other channels may continue running.
3. Review the former music path. Use the folder picker to choose that source or an intentional replacement, such as a different Pop folder. Choose only that station's music; branding sources stay separate.
4. Run **Check engine access**, then **Apply music folder** before the one-minute access proof expires.
5. Read the Library receipt. Its command ID, library revision, mapping revision, selected path and backup reference are returned from the journal. An acknowledgement alone does not establish current readback.

The button requires the engine capability legacy-music-assignment-v1. An older console rejects the dedicated command, and an older engine never interprets it as an ordinary folder assignment.

A disabled former output may have an empty destination. The preview retains its saved fields and shows a warning so other stations can be reviewed. Leave that row with the former program: mapping rejects an invalid address even for a disabled output. An invalid enabled output still fails preparation with the station ID and field names. Oversize fields still fail regardless of enabled state. Reviewing these rows does not enable them.

## Durability and isolation

The root change, command deduplication and import receipt commit in one SQLite transaction in the new application's data directory. Access denial or a stale target revision rolls back the whole operation. A completed request can be retried after a transient source check expires, after the mapping changes, or after restart; it reads the original receipt without changing a later folder choice or the current running intent.

Source inspection opens the former database read-only, without connection pooling, with a two-second busy limit. It only rechecks the mapped station identity and music setting. Passwords and AI settings are not queried. This inspection runs on a dedicated below-normal thread before the new journal write transaction. The commit callback only uses cached mapping, target-state and access gates.

Known other-station music roots, jingle roots, shared parents and overlapping new-channel roots are rejected. No audio file is copied, renamed or deleted by the assignment. The existing library worker subsequently validates stable files; assigning a folder does not prove that its contents decode, meet loudness requirements or are ready for broadcast.

A historical receipt remains available when a later ordinary folder choice replaces the current root. It is no longer displayed as the current assignment. Damaged optional receipt data is reported by Library; station root recovery does not depend on reading that optional proof.

## Remaining commissioning work

This step does not import legacy metadata overrides, retired-item state, branding assets/counters, advertisements, schedules, protected source credentials or output destinations. Those need their own reviewed import steps. Output arming, per-group legacy fencing, rollback, independent decoded-output verification and the documented actual-PC/long-run gates remain mandatory before a station transfer.

The original preview preparation still requires all new channels stopped with no configured outputs. Prepare its mapping before the first transfer. A later workflow for refreshing only unmigrated mappings is still required; do not stop healthy new groups merely to refresh that preview.

The current initial installer also needs a reviewed update path before deploying a newer package over an existing installation. A source-assets development console is an acceptance arrangement, not proof of a matching production package deployment.
