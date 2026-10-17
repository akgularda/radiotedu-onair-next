# Native broadcast desk research and interaction proposal

Research date: 15 October 2026. This document proposes an original RadioTEDU interface; it is not an implementation or mouse-test result. Source images, vendor artwork and proprietary layouts are not copied. All application-owned text is English.

**Latest user direction:** use a dark, Palantir-like professional theme. The user explicitly rejected mAirList's visual appearance. PlayIt Live/mAirList remain workflow research only; do not reproduce their literal surfaces, colored decks, typography or component styling. The dark theme specification below supersedes earlier visual suggestions in this document.

## Primary references

- [PlayIt Live: Live-Assist Mode](https://docs.playitsoftware.com/PlayItLive/218/en-US/main-window/live-assist-mode/) documents players above the running order, intro/elapsed/remaining clocks, queue insertion from the library or Explorer, and a seek interlock. These support keeping current audio and the next transition visible rather than making a dashboard of summary cards. Its screenshot references include [players](https://docs.playitsoftware.com/PlayItLive/218/screenshots/en-US/main-playing/players.png) and [playout log](https://docs.playitsoftware.com/PlayItLive/218/screenshots/en-US/main-playing/playout-log.png).
- [mAirList: Radio Automation](https://www.mairlist.com/en/products/radio-automation/) describes Automation/Live Assist, continuously estimated start times, an intro countdown and short-form cart playback. Its [official manual, pages 13–14](https://download.mairlist.com/current/mAirList/v3.1/doc/en/mAirList-3.1-User-Manual.pdf#page=23) identifies the playlist, players, browser and cartwall. That manual is historical and supplies established conventions, not current product-version claims.
- [mAirList: Cartwall](https://mairlist.docs.mairlist.com/configuration/cartwall/) distinguishes ON and PFL operation. RadioTEDU must make the actual destination explicit and must never silently substitute program output for an unavailable private cue device.
- [RCS: Zetta’s Log Module](https://www.rcsworks.com/blog/zettas-log-module-did-you-know/) describes a station log with current-item progress, searching, filtering, hour navigation and configurable columns. A filtered log is still only a view: it does not change the running order.
- [RCS: Zetta’s Redesigned Hot Keys](https://www.rcsworks.com/blog/zettas-redesigned-hot-keys/) describes named banks, asset drag-and-drop, editing separately from triggering, and On-Air/Audition routing. RadioTEDU should expose that distinction without importing enterprise module complexity.

The user clarified that actual broadcast application screens must be inspected, not only documentation. Full-screen official targets are [PlayIt Live dark Live-Assist](https://www.playitsoftware.com/Content/Images/Live/Screenshots/liveassist-dark-950.png), [mAirList main window](https://www.mairlist.com/images/auto/carousel/mainwindow.png), and the [RCS native Zetta dark-mode demonstration](https://www.rcsworks.com/rcs-live-zetta-new-features/). These are better visual targets than cropped control illustrations. This research agent's initial browser connection timed out and its web tools returned no pixel blocks. The coordinating agent subsequently connected through the supported fallback and actually viewed both the PlayIt Live and mAirList screenshots, including clicking the mAirList image to inspect it at native size. Findings below are attributed to that coordinating agent's pixel inspection, not inferred from image descriptions. Zetta documentation was researched, but its image pixels were not inspected; no Zetta pixel-level claim is made. Two actual broadcast application screens now supply the visual research basis. Native RadioTEDU visual and mouse acceptance is still a separate delivery gate.

### Recorded visual inspection: PlayIt Live

The coordinating agent inspected the full official dark Live-Assist screenshot on 15 October 2026. Three horizontal player decks occupy the upper left, with cyan elapsed, green intro and red remaining clocks; the rightmost deck is marked playing. Below, the running-order grid dominates the left area, with time/title/duration rows and a white progress indicator in the green current row. Purple and yellow rows distinguish a fixed-time entry and a short station-ID element. A searchable library occupies the right side. A compact cart row sits at the lower left; clock, time-to-marker and transport controls sit at the lower right. A sparse menu uses little vertical space. Large decorative summary cards are absent.

Original RadioTEDU adaptation: preserve the strong running-order/library split and large countdown hierarchy, but avoid three permanently occupied manual decks when a station's normal task is continuous automation. Use a compact Now/Next strip, make song-versus-jingle-versus-sweeper explicit in a Type column, and keep the microphone/cue ownership strip visible. Replace color-only meaning with text and icons. Multi-station and output diagnostics belong in a compact expandable table, rather than pushing the playing item below a dashboard.

### Recorded visual inspection: mAirList

The coordinating agent inspected the full official main-window screenshot and clicked the image to its native display size on 15 October 2026. It is a light native window with menus and toolbar at the top. An ASSIST/AUTO row is directly above the playlist; AUTO is red in this example. The queue occupies roughly the upper left three quarters of the content width, with a narrow browser/library on the right. Queue rows carry start times, item icons, intro and duration information. An ON/PFL routing strip appears below the queue, followed by three compact cart controls. Large A/B player decks occupy the bottom. Deck A is strongly red and marked ON AIR, with waveform, negative remaining time and a narrow stereo meter; Deck B is gray and idle.

Original RadioTEDU adaptation: retain the direct relationship between mode, queue and physical routing. A selected station's ownership and the destination of a cart/preview action must be visible beside its controls. Use a small number of strong state accents with accompanying text, and reserve large type for time-critical information. The user's later preference rejects mAirList's literal styling; retain these workflow lessons while implementing the original dark theme below.

## Approved visual direction: dark operations desk

Official research references are [Palantir Blueprint documentation](https://blueprintjs.com/docs/), [Palantir's Blueprint design-token structure](https://github.com/palantir/blueprint/blob/develop/packages/core/src/design-tokens/README.md), and [Foundry Workshop](https://www.palantir.com/docs/foundry/workshop/overview/) with its [example application screenshot](https://www.palantir.com/docs/resources/foundry/workshop/object-apps-workshop-module.png). Blueprint's documented separation of palette, semantic intent, surfaces, typography and focus supports a small native resource dictionary. These sources are visual/system references, not runtime dependencies. Do not add React, Blueprint packages, a web server, WebView or Electron to obtain the appearance. No Palantir logos, product names, proprietary artwork or copied screens belong in the delivered application. All AI functionality remains deferred; the visual reference does not authorize adding AI features.

### Recorded visual inspection: Palantir Blueprint dark theme

The coordinating agent actually viewed the official Blueprint documentation through the browser and activated its dark theme with the visible Shift+D keyboard shortcut on 15 October 2026. The inspected pixels showed a dark charcoal main surface, a lighter slate full-height navigation column, fine low-contrast separators, readable off-white text, restrained cyan/blue links and selection, and compact labelled navigation with small category glyphs. The blue release banner and documentation starter cards are specific to that documentation page; they are not proposed broadcast controls or reasons to introduce large cards into the running-order screen. The Foundry screenshot is an additional official reference link, not a claim that its pixels were inspected.

RadioTEDU translates this observed surface hierarchy and restraint into its own native resources, table density and broadcast state model. The palette below is approximate original design, not sampled or asserted to be Palantir's exact production tokens. Palantir logos and icons will not be copied.

The following is an original proposed palette, not a claim that these are Palantir's exact colors:

| Native resource | Hex | Use |
| --- | --- | --- |
| AppBackground | `#131A21` | Outer window and navigation rail |
| Surface | `#1B242E` | Main desk, forms and menus |
| SurfaceRaised | `#232F3B` | Toolbars, active tab, modest modal separation |
| Divider | `#344454` | Thin structural separators; not the only boundary of an essential input |
| ControlBorder | `#65788B` | Essential input/button boundary where no other shape cue exists |
| TextPrimary | `#E7EDF3` | Artist/title, labels and ordinary readable text |
| TextSecondary | `#A9B5C2` | Supporting metadata and labels |
| Selection | `#233F5B` | Selected row; selection never means playing |
| Accent / Focus | `#85B8E5` | Links, focused control outline, restrained primary intent |
| ProgramCurrent | `#1B3834` | Now-playing row background plus explicit NOW marker |
| Success | `#74C7A4` | Verified/ready state icon and text, used sparingly |
| Warning | `#E4B96B` | Waiting, stale proof, fallback or incomplete setup |
| Danger | `#F28B82` | Actionable failure text/icon |
| MicLiveBackground | `#57272E` | Active microphone state with explicit MIC ON AIR text |
| DangerAction | `#9A3943` | Deliberate stop/exit-impact action, with `#F8ECEE` text |

Calculated sRGB relative-luminance contrast ratios for opaque pairs: primary text/surface 13.30:1; secondary text/surface 7.53:1; primary text/selection 9.21:1; secondary text/selection 5.21:1; primary text/current row 10.71:1; light text/mic-live background 10.50:1; light text/danger action 5.99:1; control border/surface 3.45:1. These computations validate token pairs only; they are not an accessibility pass for the rendered application, translucent states, meters, icons or actual Windows high-contrast behavior.

Typography uses installed Segoe UI with Regular/Semibold only. Body and table content: 13–14 DIP, 18–20 DIP line height; secondary labels: 12 DIP; section titles: 14–16 DIP Semibold; now title: 18–20 DIP; remaining clock: 30–36 DIP, tabular numerals or installed Consolas for numeric clocks only. Avoid excessive uppercase, wide letter spacing and miniature 10-DIP metadata. Large countdowns are functional; giant branding text is not.

Spacing uses 4/8/12/16 DIP steps, 1-DIP dividers and 2–3 DIP corner radii. Normal table rows are 28–30 DIP; an expanded/current row may be taller only when extra information is useful. Toolbar controls are 30–32 DIP; live actions are 40–44 DIP with direct labels. Use aligned table columns, split panes and compact headers instead of floating cards. No glow, blur, glossy gradients, decorative shadows, animated backgrounds or gratuitous waveform/spectrum graphics. A genuine waveform may appear in a cue/transition editor on demand.

### Exact state treatment

- **Selected:** muted blue background, ordinary text and focus outline when keyboard-focused. It does not alter playback or routing.
- **Playing:** restrained teal row background, a 3-DIP leading marker, `NOW`, and a clear remaining clock. If selected too, retain the NOW marker and add the selection/focus outline instead of losing the playback state.
- **Automation:** small explicit `AUTOMATION` mode indicator; no large green status block. Distinguish expected playback from remote listener proof.
- **Microphone live:** solid muted-red live strip, `MIC ON AIR`, elapsed/live ownership and actual recording indicator. Preview uses blue and `HEADPHONES ONLY`; it must never inherit the live red state.
- **Waiting event:** amber clock icon and `After current item`, scheduled time and revised expected time. Avoid a misleading failure-red state merely because an approved soft event starts later.
- **Output:** separate text/icon states for Accepted, Decoded/verified, Stale, Reconnecting and Failed; last proof time remains visible in the expanded table. Color alone never asserts delivery.
- **Disabled/unavailable:** controls cannot trigger commands, and readable explanatory text states what is missing. Never reduce essential explanation to barely visible gray.

### Compact native layout

At widths above roughly 1100 logical DIP, use a 136-DIP navigation column, a flexible running-order workspace and a 280–320-DIP library/carts inspector. At narrower widths collapse navigation to a 48-DIP rail with accessible names/tooltips and move the secondary inspector into an explicit Library/Carts drawer. Do not sacrifice the Now/Next display or microphone actions to retain an always-open library.

On a 1366×768 screen at 150% scaling the nominal space is about 911×512 logical DIP before window chrome. A compact target is a 36-DIP station/clock header, 76-DIP Now/Next region, 32-DIP queue toolbar, at least 180-DIP queue area, an approximately 80-DIP two-row live strip and a 20-DIP status line, with restrained padding. Reflow device controls and titles; do not shrink all text or draw outside the client area. Roughly five usable queue rows are preferable to twelve unreadable ones. Validate actual native chrome, taskbar and scaling behavior on the target display; these dimensions are a proposal, not measured acceptance.

The final screen should feel like a dense operational workstation: quiet navy-charcoal surfaces, careful alignment, readable data and clear local consequences. It must still look unmistakably like RadioTEDU's own broadcast application.

## Product constraints and latest architecture

The user requests a native Windows application, not a browser wrapper. The latest parent-coordinated clarification restores a background product service with a native operator UI. Audio work remains independent of the WPF dispatcher and continues when the window closes or minimizes to the tray. Closing or exiting the operator window must not implicitly stop the service; stopping broadcasts is a separate, clearly labelled workflow that names the affected stations. This supersedes the intermediate same-process/tray interpretation.

UI design must accommodate eight station groups and eighteen configured outputs. Selecting a station changes the viewed station; it must not change ownership, start it or stop it. Exact station names, jingle assets, sweeper assets, gains, cadence and collision rules come from the current application's verified inventory and guided import. Do not invent branding or substitute a generic cadence. Preserve separate jingle and sweeper counters and their recovery semantics.

## Main window

Use a restrained charcoal-and-neutral native desk: flat surfaces, thin dividers, Segoe UI, tabular countdown numerals, and one restrained RadioTEDU accent. A current row has a left marker and a readable state label; color is supplementary. No gradients, glow, glass, decorative hero area, oversized rounded cards or animated waveform wallpaper. Standard Windows title bar, menus, resize behavior, keyboard focus and high contrast support remain usable.

1. A compact header contains the active station selector, `All Stations`, clock with `Europe/Istanbul`, and a persistent automation/live ownership label. The selected station's name remains visible while a dialog is open.
2. The upper program strip shows `NOW`, immutable artist/title, item type, elapsed time, large remaining time, and `NEXT` with prepared/not-ready status. Display the next transition and whether it will finish the current item. A small reserved warning line reports an actionable problem without shifting the layout.
3. The running order occupies most of the screen. Suggested columns: State, Estimated Start, Type, Artist / Title, Duration, Transition. Keep the current row and next few rows legible. An explicit `Follow current item` control restores tracking after the operator scrolls away.
4. A resizable secondary pane has `Library` and `Carts` tabs. Library search offers visible `Add next`, `Add to end`, and `Preview` actions; drag-and-drop is an additional route. Carts display asset name, duration, playback state and destination, not anonymous colored squares. Edit mode cannot trigger audio accidentally.
5. A persistent lower live strip contains selected input device, input level, `Preview microphone`, `Mic On Air`, music duck amount, `Return to Automation`, and recording state. The private cue destination is named beside `Headphones only`. Recording becomes active only while the microphone is actually on air, including quiet pauses.
6. A compact bottom status line distinguishes `Source accepted`, `Listener verified`, `Verification stale`, and `Not verified`. Each output's detailed state, codec, bitrate, stereo channels, probe time and measured loudness opens in an output table. Do not collapse an accepted handshake into an unconditional green `On Air` promise.

Primary navigation: On Air, Library, Schedule, Ads & Sweepers, Reports, System, Recovery. Multi-station monitoring is a dense table with one row per station and expandable output rows. Routine selection must never look like a stop/start toggle.

At 1366×768 and 150% Windows scaling, the usable logical space is roughly 911×512 before window chrome. Collapse secondary panes into tabs or drawers at narrow widths; preserve current title, countdown, running order and microphone controls. Do not fit a fixed three-column 1366-DIP layout by shrinking type. At higher widths show the library alongside the log. Save splitter widths per user, with `Reset layout` available.

## Critical workflows using only mouse and keyboard

| Task | Visible route and persisted result |
| --- | --- |
| Live folder | Library → Sources → Add folder → native folder picker → station mapping → include-subfolders preview → validation → Save → reopen and read back. Show scanning, stable-file admission, missing files, retired entries and drive-disconnected state. A missing drive must not retire the entire library as though every song was deliberately deleted. |
| Queue edit | Search → select → Add next/Add to end; reorder via drag-and-drop or Move up/Move down. Show command pending/accepted/rejected and final engine order. An optimistic row alone is not completion. |
| Soft scheduled program | Schedule → New event → stations, date/time, playlist/folder, `After current item`, end policy, optional metadata → timeline preview → Save → reopen. On Air shows `Scheduled 09:00 · After current item · Expected 09:02`; the item finishing at 09:02 is not cut. Expected time changes with queue changes. |
| Special day | The same event editor supports an explicit date range, optional recurrence, an all-day metadata override and a preview of return to normal rotation. Show precedence and overlapping-event conflicts before saving. |
| Branding | Ads & Sweepers → station → distinct Jingles/Sweepers sections → imported source and rule preview → private audition → save/readback. On Air exposes counters and next-due sequence, including a visible reason when both are due. |
| Microphone | Select a real device → private preview and level check → one clear `Mic On Air` action → visible ownership/recording state → Return to Automation. Device loss must expose recovery and actual state rather than leaving a false live light. |
| Output metadata | System → station outputs → station identity, description, genre, website, directory visibility and supported codec fields → validation/test → save/readback. Separate automatic track metadata from a timed override; show which source currently owns the published title and when the override expires. Server capabilities/unsupported fields are reported explicitly. |
| Reports | Reports → station/date range → local completeness and all attempts → Export CSV or Retry Upload. Display pending/retrying/confirmed remote commit states independently; an upload failure never suppresses local history. |
| Safe close | Standard window close → tray with a short first-use explanation. Tray menu → Open or Exit operator UI; the background service continues. Stopping the service or broadcasts is a separate authorized operation naming the station impact. Closing an edit dialog must not accidentally stop playback. |

Every primary operation has a normal visible button. Context menus and shortcuts supplement it. Search fields, queue navigation and form controls use standard focus behavior; typing in a form must never trigger a global cart or stop command. Do not bind Space to global transmission stop. Show shortcut labels in menus and allow conflicts to be resolved. Destructive fleet actions belong in System/Recovery, visually separated from `Next`.

## Lightweight implementation guidance

These are design proposals requiring measurement, not performance claims:

- Use WPF DataGrid/ListView row and column virtualization with recycling; page database-backed library queries, and keep exports independent of UI page limits.
- One coalesced UI update stream supplies visible station state. Render meters at a bounded rate; calculate PCM/loudness on audio/background workers. Avoid a timer, event subscription or task per row.
- Throttle nonessential updates when minimized; audio, retries, watchdogs and history continue. Do not repeatedly reconstruct the queue or reset selection when status changes.
- No whole-library artwork/waveform preload, spectrum visualization by default, embedded browser runtime or large UI framework solely for decoration. Load settings screens on demand.
- Search is cancellable and debounced; old responses cannot overwrite a newer query. Import, tagging, report generation and scans are bounded jobs with visible cancellation/progress.
- Use a prevalidated successor and configured emergency pool to avoid avoidable dead air. Show `Next ready`, fallback activation and last failure. Never label genuine quiet music as a fault from level alone or restart healthy sibling outputs in response.
- Measure minimized/visible UI, search, scrolling, import and report rollover alongside the actual eighteen encoders on the commissioning hardware. Styling simplicity is not evidence of low memory or continuity.

## Mouse acceptance evidence required

Record native screenshots, mouse/keyboard steps, expected result and persisted readback for each workflow above. Include 100% and 150% scaling, keyboard-only fallback, long Turkish titles, empty/missing sources, device loss, stale listener proof and upload failure. On the isolated test station verify successive actual audio, soft-event timing, jingle/sweeper ordering and microphone-only recording independently of the displayed UI state. Reopen the app to verify saved configuration, minimize to tray during playback, and test the explicit exit/recovery policy on an isolated instance. Operational tests must not use a CLI, SQL update or private API to repair a missing UI action: fix the control and repeat the same mouse workflow.
