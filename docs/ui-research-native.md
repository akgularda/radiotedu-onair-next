# Native desktop UI research

Research date: 15 October 2026. This is an implementation recommendation, not completed native UI, measured capacity, or mouse-test evidence. Production installations, services, databases, audio and sources were not changed. The authoritative scope remains DEVELOPMENT_PLAN.md, docs/OPERATING_PLAN.md and docs/BROADCAST_PC_HANDOFF.md, with later user instructions taking precedence.

## Concrete direction

Final user clarification: Windows service startup before user logon is desired. This supersedes the intervening interpretation that UI and engine must share one process. Deliver one integrated RadioTEDU product, installer, settings model and native operator interface. The product-owned Windows service hosts the broadcast engine; the native WPF desktop and tray control that same engine. Product integration does not require process identity: Windows Session 0 cannot provide the operator's interactive desktop. Do not introduce a second independent product, separate manual setup, hidden parallel engine or browser/WebView2 UI.

Closing the main window or minimizing it hides the desktop to the Windows notification area while its service continues broadcasting. The visible tray menu provides Show and a clearly labelled Close Desktop action explaining that broadcasts continue; stopping broadcasts is a separate deliberate native UI operation with station scope and consequences shown. A desktop close/crash or user logoff must not terminate the engine. Only an explicitly authorized broadcast/service stop or system shutdown ends it. All service state, startup policy and faults remain visible and manageable in the one product UI; separate processes must not become separate operator workflows.

Configure and verify service startup before user logon through the guided native installer/System screen. The WPF desktop/tray requires an interactive user session and reconnects to the already running product service after logon. Existing auto-logon is not required for automation playback and must not be silently reconfigured. Service identity must have validated access to media, protected credentials, database and output network destinations. Interactive microphone/cue device behavior and logoff/device loss require explicit testing; do not infer those capabilities from successful service automation startup. The Windows 10 Pro 19045 / .NET 10 support and actual deployment gate remains unresolved; WPF selection alone is not proof of vendor OS support.

Keep the shell small: one Window, shared resource dictionary, seven native views, typed presentation models and asynchronous typed commands to the product-owned engine service. Prefer built-in Button, TextBox, ComboBox, DataGrid, Menu, TabControl and common file dialogs. Avoid a large commercial theme suite, browser/chart runtime, general-purpose reactive framework or new media engine unless a measured need justifies it. A small observable base and asynchronous ICommand implementation are sufficient initially. Domain behavior belongs outside WPF and remains independently testable.

## Observed code gaps

At the research snapshot the repository had Engine, Host and partial Application contracts/storage, with no completed native UI. Application/Contracts.cs StudioStatus embeds Library, Queue, Schedule and RecentEvents. Rebuilding and binding that entire object on every meter tick would allocate growing collections. Separate compact station/output/meter snapshots from paged library/history queries and versioned queue/schedule changes. Host/Program.cs had only status, skip and stop through a current-user pipe; that historical developer host is not the new operational architecture. The native UI must expose the complete engine/application command surface with persisted commands, permission checks and operation-specific forms. Use a local typed, access-controlled asynchronous command boundary to the product-owned service; developer pipe assumptions must not become the production service security model. Both processes share the same product configuration and lifecycle controls rather than requiring an operator to launch or configure a second engine.

## Native layout and operational hierarchy

Use the seven approved destinations: On Air, Library, Schedule, Ads and Sweepers, Reports, System and Recovery. A restrained menu/toolbar and station selector replace a dashboard of decorative statistic cards. A persistent top strip identifies station, Automation / Live / Stopped, programme time and connection evidence age. On Air centers the chronological playout log and Now / Next with a readable remaining time, then provides a compact microphone/cue/return-to-automation strip. Cart wall and station overview are useful secondary panes, not giant empty panels. Put source acceptance and independently decoded listener status in separate labelled fields.

The primary log should have aligned columns for scheduled/actual time, type, artist/title, duration, remaining, origin and state. Distinguish Music, Jingle, Sweeper, Advertisement, Recorded Programme and Live with both text and restrained color. Show pending takeover as 'After current item' with expected start, not an unexplained delayed schedule. Show the recovered jingle and sweeper rules, their distinct persisted counters and next eligible insertion; import exact assets/collision behavior from the actual legacy installation rather than inventing a generic cadence.

Use the final dark operations workspace palette below: Segoe UI, solid dark surfaces, fine separators, compact aligned tables and an original RadioTEDU identity. No gradients, glass, oversized rounded cards, glowing borders, animated backgrounds or manufactured success badges. Only real connected controls ship. A pending feature stays out of the operator workflow until it works; development fixtures must be explicitly identified. Errors state the affected station and a usable next action. All owned text is English; original artist/title metadata remains intact.

## Final visual direction: restrained dark operations workspace

Latest user preference replaces the earlier radio-software appearance references: use an original Palantir-like dark, dense operations workspace. Do not imitate mAirList's appearance or recreate a vendor screen. Radio-specific behavior still comes from the actual legacy programme; its visual styling does not. The product remains the integrated Windows engine service plus native WPF desktop/tray described above.

The proposed WPF resource palette below is original project guidance, not a claim to reproduce Palantir's exact theme. Blueprint's official semantic intent/surface organization is a useful reference; no React, CSS, Blueprint runtime, browser or WebView dependency is introduced.

| Semantic resource | Proposed color | Use |
|---|---|---|
| AppBackground | #10151C | Main dark workspace |
| PanelBackground | #171E27 | Solid panels and log surface |
| HoverBackground | #1F2935 | Restrained row/control hover |
| SelectedBackground | #273B55 | Selected row/navigation |
| StructureBorder | #334155 | Fine nonessential dividers |
| ControlBorder | #64748B | Identifiable editable controls |
| TextPrimary | #E5EAF0 | Main metadata, controls and times |
| TextSecondary | #B5C0CD | Supporting labels and details |
| FocusAccent | #7DB8FF | Keyboard focus, active navigation and links |
| StateHealthy | #6FD3A0 | Verified healthy state with text |
| StateWarning | #F4C36B | Pending/stale/retrying with text |
| StateDanger | #FF9292 | Fault/destructive state with text |

Use solid brushes, 1-DIP separators, 0-2 DIP corners and a compact 4-DIP spacing rhythm. Default body text starts around 13-14 DIPs Segoe UI; time/countdown fields may use tabular or monospaced numerals. Ordinary data rows start at 28-32 DIPs, ordinary commands at 32 DIPs, and live/stop controls at least 40 DIPs; verify actual readability and pointer targets at the supported DPI settings rather than shrinking all content. Native context menus, dialogs, disabled states, validation and tooltips share the same semantic resources. Override with Windows system colors when High Contrast is enabled.

Use a narrow navigation rail, compact station/service strip, dominant chronological log, and a clearly bounded Now/Next/live control region. Thin separators and aligned table columns provide hierarchy. Small current station/output state markers replace large statistic cards; detailed technical evidence opens in a native inspector when requested. No generic SaaS dashboard, giant welcome heading, decorative charts, glass/blur, neon, glow, gradients or continuous animations. Do not color every row or turn every panel into a card. Selection, hover, keyboard focus and actual ON AIR state must remain visibly distinct.

Keep one consistent 2-DIP focus outline using FocusAccent, visible focus order and English text labels such as ON AIR, AUTOMATION, RETRYING, STALE and STOPPED. Color supplements those labels rather than replacing them. Verify text contrast on every real background, including selected/disabled/error cells; fine structural dividers are not the sole boundary of an editable control. Never use a green indicator unless its underlying evidence is present and current.

Primary reference: [Palantir Blueprint semantic design tokens](https://github.com/palantir/blueprint/blob/develop/packages/core/src/design-tokens/USAGE.md). The source documents semantic intents and theme-specific surfaces; palette values, native dimensions and composition above are project proposals, subject to real WPF screenshot, keyboard and mouse validation.

## Low-resolution and DPI behavior

1366 x 768 at 150% scaling provides approximately 911 x 512 WPF DIPs before taskbar and window chrome. Setting MinWidth=1366 and MinHeight=768 in DIPs would fail the requirement. Use a compact layout breakpoint around 1050 DIPs and verify the actual available client rectangle. Collapse text navigation to a labelled menu, move secondary detail/cart panes into tabs, reduce optional columns and keep the station/live state, Now/Next, countdown and core controls accessible. Forms may scroll vertically, with Save/Cancel reachable; do not shrink the whole interface into illegible text using a Viewbox. Support 100%, 125% and 150%, visible focus, Windows High Contrast and keyboard navigation.

## Bounded service threading and native status updates

Within the product service, audio/codec/network timing runs on bounded background lanes independently of the WPF Dispatcher. Native UI command handlers await asynchronous typed local service commands; no .Result, .Wait(), synchronous database/network/file analysis or per-track waveform calculation on the Dispatcher. Marshal small coalesced presentation updates with Dispatcher.InvokeAsync at a non-input-blocking priority. Cancel obsolete searches and navigation requests; discard late responses by request/version rather than overwriting newer state.

Proposal for measurement: meter summaries at 10 Hz only for visible On Air controls; selected-station transport state around 4 Hz; station overview about 1 Hz; countdown rendered locally at 4 Hz from the latest authoritative sample/timestamp; Reports/System refresh on revision/event or explicit action. These frequencies are engineering starting points, not standards or proven budgets. Minimized/inactive views should suppress visual meter updates while retaining application commands, background audio and incident notifications. The local countdown is an estimate and must never decide actual audio/schedule transitions.

Coalesce volatile status in a latest-value slot so a stalled UI cannot accumulate unbounded snapshots. Durable incidents, playback occurrences and command results must not be discarded with volatile meters; retrieve them from bounded paged history or reliable acknowledged commands. Permit at most one in-flight request per polling category with timeout/backoff, give operator commands priority, and ensure an offline or slow status query cannot indefinitely block Stop/Return actions. Persist command ID plus payload and confirm the saved revision by read-back before presenting success. Health snapshots must not replace dirty form models or reset queue selection/scroll position.

## Library, log and rendering costs

Microsoft distinguishes UI virtualization from data virtualization; WPF does not supply automatic data virtualization. Use indexed, bounded queries with stable ordering and explicit pages (a starting size of 100-200 rows). For large history/library listings prefer keyset/cursor paging; render only loaded pages and keep a bounded adjacent-page cache. The product engine/storage layer owns indexed full-dataset search/filter/sort; do not replace that with ICollectionView over an entire loaded library. Debounce text search around 250 ms and cancel previous queries. Full annual exports stream on background application tasks outside the Dispatcher and never inherit display page limits.

Use DataGrid EnableRowVirtualization=true, EnableColumnVirtualization=true, VirtualizingPanel.VirtualizationMode=Recycling and CanContentScroll=true. Do not wrap a virtualized grid in an unconstrained vertical StackPanel/outer ScrollViewer, populate row containers manually, or attach per-row permanent timers. Store selection/edit/expanded state in stable item IDs/models, not recycled containers. Use fixed or bounded column widths instead of repeated all-row automatic measurement.

Keep visual templates shallow, use shared/frozen brushes and geometries, unsubscribe view timers/events when leaving a view, and avoid full-window invalidation for changing meters. A small custom-drawn meter/waveform can avoid thousands of Shape children, while still exposing an accessible text/value peer. Generate optional waveform summaries in one throttled background worker, cache compact min/max bins by asset identity/content revision, and render only the visible section. Decode cover art at requested thumbnail size with a bounded cache. Never retain full-length decoded PCM just for UI decoration. Check hardware and software rendering; do not force software rendering globally as a speculative optimization.

## Mouse and keyboard workflows that must actually pass

The operational acceptance run starts from the built executable/installer and uses actual mouse clicks and keyboard input. UI Automation may inspect names/bounds/state; invoking a control pattern or calling hidden APIs does not demonstrate the user's requested mouse operation. Each mutation must save, show confirmation/read-back, survive closing/reopening the UI, and be undoable or have a rehearsed recovery path.

1. Install/configure the one integrated native product through its UI. On an isolated test system, verify its Windows service starts automation and independently decoded test outputs before user logon. Sign in and open the WPF desktop without a browser; it must discover/control the same already-running service and must not create duplicate sources. Inspect all stations and distinguish source acceptance from decoded listener evidence. Minimize, close-to-tray, restore and close/reopen the desktop through actual mouse actions while decoded test audio continues. Verify service continuity through desktop failure and user logoff in isolation. Test deliberate scoped broadcast/service stop and recovery through the product UI, with the intended consequences confirmed. Do not reboot the healthy production PC for this acceptance check.
2. Use native folder picker to choose a nested music root. Inspect validation/progress, add/replace/remove stable decodable files, verify queue eligibility, missing-drive state and restoration without manual database/configuration edits. Import media with native multi-file picker; cancel safely.
3. Search a large library, scroll, select, privately audition through the chosen cue device, add to queue, drag reorder, then use Move Up/Move Down keyboard alternatives. Preserve unsaved metadata edits while live meters update.
4. Import actual jingle/sweeper configuration through the guided read-only legacy snapshot workflow. Review mapping, gains, cadence, collision and restart behavior. Save/read-back, demonstrate completed-song counters and insertion order through several short test tracks, and verify independent outputs.
5. Create an event for a future boundary with 'After current item'; confirm it starts after the current track rather than cutting it. Preview the normal-program return and inspect actual timing/history, including overlaps and expiration.
6. Pick microphone/cue devices, verify private test is never broadcast, arm/open/close Live, inspect duck/return and recording state. Test device loss in isolation and verify recording triggers only for actual live microphone programme mix.
7. Configure each codec/output and supported Icecast information, reject invalid or missing encoder capabilities, and verify persisted read-back without exposing passwords. Keep station-specific stop/disarm distinct from a separated deliberate global stop.
8. Use Reports to inspect all roster attempts, interrupted/restarted links, 24 Istanbul hours and full annual export. Configure/test private GitHub publication, disconnect/retry/catch-up and confirm a verified remote commit through UI.
9. Perform backup/restore, recovery, product service startup/identity/repair configuration and one-group migration/rollback rehearsal entirely through the native UI. Operators must not need SCM consoles, shell commands, SQL or a second hidden configuration workflow. Use isolated destinations first; production requires the explicit evidence gates and no simultaneous source ownership.
10. Repeat compact-screen cases at 1366 x 768 and 100/125/150% scaling; tab through controls, preserve visible focus, verify dialogs/popups/validation are reachable and no essential button is clipped. English-owned strings and original Turkish metadata both render correctly.

UI acceptance evidence should include test build/commit, screen size/DPI, station/test origin, numbered actions with screenshots before/after, persisted read-back, independent audio/report evidence and every failure/fix/retry. A screenshot-only walkthrough or mocked green labels is not a successful test.

## Resource acceptance

Use the original measured-acceptance targets for the integrated service/desktop product: native desktop ordinary views <=250 MiB, engine service excluding codec children <=300 MiB, complete product including all owned codec/audio components preferably <=2 GiB, full-roster typical CPU below 60% and ordinary local command acknowledgement p95 below 200 ms. These are engineering goals, not achieved measurements or a reason to consume the full budgets. Add before-logon service readiness, desktop first-usable-view latency, visible/minimized UI CPU, Dispatcher input latency, typed status/query payload sizes, allocation rate and memory trend while navigating/searching/importing/reporting. Measure every product process and all owned codec processes together with the full roster and measurement overhead on the HP hardware; hiding the UI must not merely shift unbounded memory into the service. Do not repeatedly force GC to disguise leaks. Do not run a duplicate fleet on the live PC while headroom is limited.

No dead-air claim can come from a fast-looking UI. Independent continuous decode, sample-boundary assertions, prebuffer readiness, genuine quiet-section handling, slow-output isolation, asset failure fallback and the full 24/72-hour qualification remain engine acceptance gates. The UI must display real stale/missing evidence rather than restart all stations on silence alone.

## Native mouse automation capability

Read C:/Users/tedu/.codex/plugins/cache/openai-bundled/computer-use/26.1007.21434/skills/computer-use/SKILL.md. It explicitly documents the native computer-use package @oai/sky imported within node_repl, using SendInput, UI Automation and Windows.Graphics.Capture. The disabled native surface in cua_repl does not prove this separate package unavailable. No package initialization, app launch or UI mutation was attempted during this research. Before using it, read the referenced ../../docs/guidance.md, api.md and confirmations.md; import @oai/sky as documented. Do not spawn or reverse engineer its helper. The eventual run must verify the package is functional and follow its target-window/input/screenshot rules.

## Primary sources consulted

The recommendations above combine the existing product requirements, actual code inspection and these Microsoft references; numerical refresh/page/cache suggestions are project design proposals rather than Microsoft's prescribed values.

- [WPF control performance](https://learn.microsoft.com/en-us/dotnet/desktop/wpf/advanced/optimizing-performance-controls): UI versus data virtualization, recycling and accidental virtualization disablement.
- [WPF threading model](https://learn.microsoft.com/en-us/dotnet/desktop/wpf/advanced/threading-model): Dispatcher ownership, short UI work and asynchronous background work.
- [WPF layout performance](https://learn.microsoft.com/en-us/dotnet/desktop/wpf/advanced/optimizing-performance-layout-and-design): visual/layout work and unnecessary measure/arrange costs.
- [WPF rendering hardware](https://learn.microsoft.com/en-us/dotnet/desktop/wpf/advanced/optimizing-performance-taking-advantage-of-hardware): software rendering costs and pixel overdraw.
- [WPF object behavior](https://learn.microsoft.com/en-us/dotnet/desktop/wpf/advanced/optimizing-performance-object-behavior): event lifetime leaks and frozen resources.
- [WPF drawing and imaging](https://learn.microsoft.com/en-us/dotnet/desktop/wpf/advanced/optimizing-performance-2d-graphics-and-imaging): lightweight drawings, DrawingVisual, StreamGeometry and thumbnail decoding.
- [DataGrid column virtualization](https://learn.microsoft.com/en-us/dotnet/api/system.windows.controls.datagrid.enablecolumnvirtualization?view=windowsdesktop-10.0): explicitly enable the default-false column virtualization property.
- [Native common dialogs](https://learn.microsoft.com/en-us/dotnet/desktop/wpf/windows/dialog-boxes-overview) and [WPF .NET 8 folder dialog introduction](https://devblogs.microsoft.com/dotnet/wpf-file-dialog-improvements-in-dotnet-8/): built-in folder/file selection without a browser or third-party picker.
- [WPF focus overview](https://learn.microsoft.com/en-us/dotnet/desktop/wpf/advanced/focus-overview) and [focus visual styles](https://learn.microsoft.com/en-us/dotnet/desktop/wpf/advanced/styling-for-focus-in-controls-and-focusvisualstyle): predictable keyboard traversal and focus indicators.
- [Accessible WPF button names](https://learn.microsoft.com/en-us/accessibility-tools-docs/items/wpf/button_name): expose clear control purpose through UI Automation.
