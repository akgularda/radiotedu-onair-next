# Live folder workflow

This describes the current development workflow. Connected browser mouse acceptance, the guided service installer, actual media access before logon and production migration are still unpassed release gates.

## Choose the station folder

In Library, select the channel first, then Choose live folder. Browse to that station’s music directory and choose Use this folder. Read back the channel and full saved path in Library. Wait for the engine’s validation result; a visible path in the browser’s folder picker does not prove that the Windows service can read it.

For a parent containing Rock, Jazz and Ads, select Rock for the Rock station. Do not choose the shared parent: all supported audio in subfolders belongs to the selected station. Jingles, sweepers, advertisements, archives and other station folders need their own verified assignments. Existing filenames alone do not establish which branding is active.

## Change music

For an external live folder, copy or remove songs in Windows Explorer. The isolated build watches that location read-only. Stable additions and changed files pass metadata and complete decode validation before becoming eligible; a file still being copied is not ready. Unreadable files are shown as invalid. Removing a file removes it from future rotation while preserving catalog and playback history. Removing the source of audio already being decoded can affect that playback; do not unplug a live source drive as an operational test.

Add files uploads to the engine’s private managed folder. The isolated build prevents copying uploads into an external live folder. UI metadata edits are separate catalog overrides and preserve original bytes and prior attempt metadata. Remove excludes a track from future rotation; Restore requires validation before reuse.

## Check service access

Before commissioning, use the completed setup/Library UI to verify the actual service identity can read the chosen music and branding folders without a signed-in user. A drive letter visible to the operator may be session-specific. Do not grant broad permissions or silently remap source directories. Real volume visibility and service read/decode checks must succeed, and stations must stay disarmed until their handover checklist passes. This setup workflow remains unfinished.

An unavailable source retains its catalog history and is retried. That does not prove continuous audio while the source is absent; a verified fallback and independently decoded continuity check remain required.
