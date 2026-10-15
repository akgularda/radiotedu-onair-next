# Guided Windows installation

This is a separate development installation. No station has migrated, and the current broadcasting program remains the production owner. A successful installer fixture is not broadcasting-PC, audio, loudness or mouse acceptance.

## Package and operator workflow

1. Extract the complete Windows x64 package to a local folder and open setup/OnAirNext.Setup.exe. It launches the local console in the default browser without Codex. Keep the package together until installation is finished.
The launcher opens an operator-private console session. A per-console startup capability is read from the current user profile and passed in a URL fragment, then removed from the address bar. It expires when that console process ends. If browser session storage is unavailable, the launch link still signs in for the current page; reopening the installed shortcut creates a fresh authorized page. Opening a bare loopback URL does not grant a session. Session creation and all API reads/writes are gated; health exposes only product/build/process identity. The startup file is restricted to the current operator, SYSTEM and machine administrators. No Windows password is stored.

2. Open System. Choose the local folder containing the approved ffmpeg.exe and its supporting DLL/license files. The package includes .NET, but excludes FFmpeg, music, credentials and actual station configuration.
3. Select Prepare and check. The package files are checked against their manifest and the runtime encoder list is inspected under the ordinary operator identity. libfdk_aac and FLAC must be listed; this is not a decoded-codec or loudness verification.
4. Review the separate install/data locations, operator SID, LocalService account, selected runtime and package checksum. The initial configuration has eight stopped stations and zero armed outputs. Choose whether to start the new service after installation.
5. Select Install separate service and approve the Windows administrator prompt. The privileged installer never runs the selected runtime as administrator. It copies only the reviewed, hash-checked files into its own fixed roots and reads back service identity, delayed startup and recovery settings.
6. Read the saved receipt, then connect the isolated engine. Create desktop and Start shortcuts from System. These start the installed local console independently of Codex. Existing matching shortcuts are reused; a different shortcut is preserved and must be renamed before creating this one.
7. If a different console build is already open, use Close console in its System screen and reopen the desired package or shortcut. This closes only the browser bridge; the broadcast service keeps running. A package launcher checks the console build identity rather than reusing a different build.
8. Continue the media/import, audio, resource and migration checklists. Install success is not permission to arm production outputs. Each station codec group still needs its complete verification before another station is touched.

## Protection and recovery

The former service, configuration, database and music are not opened for writing by the installer. The new service is RadioTEDU.OnAirNext.Development, under LocalService. Its fixed paths are Program Files/RadioTEDU-OnAirNext-Development and ProgramData/RadioTEDU-OnAirNext-Development. The service independently checks protected configuration and binary ownership on every start.

A preparation step does not create a machine installation or register a service. The reviewed plan is flushed before elevation. Once installation owns its separate data root, it retains a protected canonical plan and progress receipt. After the console closes, System can recover that plan and read the installer result. Do not delete the extracted package or selected runtime while an installation is pending.

Retry uses the same plan. Package or runtime changes, another plan's ownership, unrelated existing files, linked paths and a conflicting service identity are rejected. Existing different configuration is never overwritten. Active program files are not replaced while its service is running. An incomplete copy uses only its known manifest-owned partial filename; no recursive deletion is used by the product installer.

Installed files are explicitly owned by Administrators. Binaries/configuration are read-only to the service/operator; only the engine data folder grants LocalService write access. Original runtime files and source folder permissions stay unchanged. Identical immutable package files share a hard link inside the protected installation where supported, reducing duplicate runtime disk storage. No links are created to operator files, music, the source package or the former installation. The package's logical extracted size and unique payload bytes are measured separately; these are not measured target-PC RAM usage.

If Windows approval is cancelled, the plan stays available. If setup fails, read the retained step and retry that reviewed plan after resolving the reported disk/package/permission problem. Unexpected ownership or files fail closed rather than deleting data. Updates to an already configured installation and broader repair/rollback workflows remain commissioning work.

## Evidence limits

The isolated Windows CI fixture builds a self-contained package, installs it into disposable roots, reads back eight stopped channels, verifies its own coreclr runtime, source-byte preservation, service ownership and repeat behavior. The runtime encoder listing used in that fixture is explicitly a test double, with no actual codec/audio implementation. Further checks cover hard-linked runtime identities and the real loopback/CSRF shortcut route. Exact source 05b2d31f9c9ba77a3f24f529bfe240c93f984bc8 passed 134 unit cases, 35 service checks and all 17 setup/console checks in Windows run 38821160644. The operator-private startup-capability and authenticated-read follow-up is newer and requires fresh qualification before target-PC use. The reviewed Windows runtime-lock pinning passed the complete qualification; target-PC admission still requires its own resource and UI checks. Framework and Windows runtime graph restore both use locked mode.

Connected mouse/file-chooser operation, real UAC interaction, actual Windows 10 hardware and before-logon media/device access remain unqualified. Resource admission is required before running new software on the broadcasting PC. Linux/macOS packages and final desktop archives remain part of the delivery scope.
