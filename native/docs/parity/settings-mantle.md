# Parity: Settings app part 1 (framework + Mantle's own settings)
Baseline: origin/master 6ca499f (2026-11-06). Inventory by lane native-plan-inv-setm.
Rows: 208 (SETM-001 to SETM-208)   Scope: ui/settings/{settings,model,kit}.js + index.html + settings.css + SECTIONS.md; lib/features/settings.js; sections personalization, applist, apppower, ramreclaim, gaming, accounts (+ _settings-sections halves); the Mantle / Shortcuts / About / Home pages; feature options (lib/features/*.js `options`); the Google Calendar group in sections/time.js. Test column shorthand: `app` = ui/settings/app.test.js, `whole` = ui/settings/whole.test.js, `model` = ui/settings/model.test.js, `fs` = lib/features/settings.test.js, `sec/<x>` = ui/settings/sections/<x>.test.js.

## Framework: window, open, close
| ID | Area | Function | Electron source | Electron test | Native |
|---|---|---|---|---|---|
| SETM-001 | Window / Open | Win+I opens "Mantle Settings"; if one is already open it comes to the front where the user left it (no second window); a new window starts at Home | lib/features/settings.js#open, ctx.onChord | fs | todo |
| SETM-002 | Window / Open | Other ways in: top bar Mantle menu (topbar.settings runs Win+I handler), launcher row "Mantle Settings" and any Windows page it covers (app event `mantle-open` 'settings'), command `settings.open {section,page,row,query}`, bar links `ms-settings:` routed by lib/open-route.js (`ms-settings:` root = Home). There is NO `--open=settings` (allow-list is wallpapers, player only) and NO `mantle://` protocol | lib/features/settings.js, lib/open-route.js, lib/args.js, lib/launcher-index.js | fs, lib/open-route.test.js, lib/args.test.js | todo |
| SETM-003 | Window / Size | Frameless window 1040x700, min 760x520, centred on the work area, shrinks to work area minus 48 px; resizable, minimise, maximise, listed on the bar and Alt+Tab under the exact title "Mantle Settings" | lib/features/settings.js#makeWindow | fs | todo |
| SETM-004 | Window / Identity | Own app id `Mantle.Settings` and own icon assets/icons/mantle-settings.ico (missing icon: tray icon used and "needs a look" line on Home) | lib/features/settings.js | fs | todo |
| SETM-005 | Window / Title bar | Header strip is the title bar: Back arrow, title "Mantle Settings", "Test run" chip (only in a test copy), Minimise, Maximise/Restore (glyph follows maximised state), Close (settings.window op) | ui/settings/index.html, settings.js ACT win-op | app | todo |
| SETM-006 | Window / Cold open | Closing DESTROYS the window (no hidden keep, user: save RAM). Cold open shows at first paint; if not painted 300 ms after trigger it is shown on its plain ground anyway (SHOW_BY_MS); dark waits at most COLD_HOLD_MS for wallpaper picture | lib/features/settings.js (SHOW_BY_MS, showWhenPainted) | fs | todo |
| SETM-007 | Window / Cold open | Home is drawn at first paint from a localStorage cache `settings.cache` (slots qs, net, battery, pm, gm, mantle, audio, about; saved at most every 1.5 s and on hide); fresh reads replace it | ui/settings/settings.js#cacheLoad | none | todo |
| SETM-008 | Window / Keep ready | Setting `keepReady.settings` (off by default): when on, ONE hidden window is made 60 s after start (2 s after switching on; waits for CPU under 60 percent over 1 s, retries every 30 s up to 6 times then makes it anyway); Close HIDES it; Win+I shows it at Home; page sleeps while hidden | lib/features/settings.js#prewarmSoon, lib/keep-ready.js | fs, lib/keep-ready.test.js | todo |
| SETM-009 | Window / Rest | `rest: true`: a running game destroys the window and Win+I is Windows' again; install runs again after the game. Switching the feature off, a crash and quitting also destroy it | lib/features/settings.js | fs | todo |
| SETM-010 | Window / Failure | Page crash (render-process-gone) or load failure closes the window and says why (health false: "Win+I is Windows' again"); next Win+I makes a new one. Page may not navigate or open windows | lib/features/settings.js | fs | todo |
| SETM-011 | Window / Focus | After show, if Electron focus lost to the foreground lock, the sidecar `focus {hwnd}` command hands the keyboard over (80 ms later); failure logged "click in it to type" | lib/features/settings.js#reveal | fs | todo |
| SETM-012 | Window / Glass light | Light: DWM acrylic system backdrop (Windows 11 22H2 build 22621+); page turns clear only once sidecar `taskmgr.frame` read-back says acrylic AND matching frame; else solid and About says why; page goes opaque --lg-ground while the window is in the background (inactive) | lib/features/settings.js#glassPlan,lightFrame, settings.js#glassApply | fs | todo |
| SETM-013 | Window / Glass dark | Dark: window opaque (ground #1B1C1F), page paints the desktop wallpaper picture blurred under its tint (ui/shared/wall-bdrop.js, events settings.wall, settings.wallpos); falls back to solid with a reason on About | lib/features/settings.js, ui/shared/wall-bdrop.js | fs | todo |
| SETM-014 | Window / Theme | Follows Mantle appearance live (frame light/dark via taskmgr.frame, solid ground GROUND light #F4F6FA dark #1B1C1F), `mantle-theme` event repaints, no restart | lib/features/settings.js#themeNow | fs | todo |
| SETM-015 | Window / Bridge | If window.mantle is missing: banner "Settings is not connected to Mantle, so nothing can be read or changed. Close this window and open Settings again (Win+I)." | ui/settings/settings.js NO_BRIDGE | none | todo |
| SETM-016 | Window / Hello | Page asks `settings.hello` for test flag, glass, hidden flag and where to open; failure banner "Mantle did not say which page to open (...), so this is Home." | settings.js#boot | none | todo |
| SETM-017 | Window / Sleep | Hidden/closed page stops loops and the `qs.watch` lease (who 'settings'); `settings.sleep`/`settings.wake` events and visibilitychange/pagehide drive it; lease re-asserted every 30 s; fallback re-read of qs every 5 s with banner if the lease is refused | settings.js#wake,sleep,qsWatch | fs | todo |
| SETM-018 | Window / Redraw | `settings.changed` pushed (120 ms debounce) on any feature switch/option/health change from tray, cards or games; page re-reads mantle slot and refreshes the mounted module page | lib/features/settings.js, settings.js | fs | todo |
| SETM-019 | Window / Test copy | --test: window made but never shown/focused; every write to Windows is a dry run (openWindows, openLogs, startupSet, window minimise/maximise); folder dialog refused in words | lib/features/settings.js | fs | todo |
| SETM-020 | Window / Folder dialog | `settings.pickPath {title,defaultPath}`: folder dialog owned by the window (openDirectory, createDirectory); cancel returns cancelled; refused if no window or test run | lib/features/settings.js | fs | todo |
| SETM-021 | Window / Paths | `settings.openWindows {uri}` only accepts `ms-settings:[a-z0-9-]*`; `settings.openLogs` opens the folder of `--log=`; "this run writes no log file" otherwise | lib/features/settings.js | fs | todo |
| SETM-022 | Window / Layout | Left nav 244 px, content column max 780 px (+32 px padding); below 860 px width sliders shrink (200 to 150 px) | ui/settings/settings.css | none | todo |

## Framework: navigation and search
| ID | Area | Function | Electron source | Electron test | Native |
|---|---|---|---|---|---|
| SETM-023 | Nav / Sidebar | Sidebar order: Home, group "Windows" (system 10, devices 20, network 30, personalization 40, apps 50, accounts 60, time 70, gaming 80, accessibility 90, privacy 100, update 110), group "Mantle" (Mantle 1000, App power 1005, Windows pieces 1006, Keyboard shortcuts 1010, About Mantle 1020); order key then built-ins before modules then registry | ui/settings/model.js ORDER,sectionModel | app, whole, model | todo |
| SETM-024 | Nav / Sidebar | Current item has aria-current page; click goes to section (module with pages opens its overview card listing pages) | settings.js#renderNav, V.overview | app | todo |
| SETM-025 | Nav / Sidebar | Warning dot on a DOWN section (tooltip "<label> could not be loaded: <reason>") and on "Mantle" when any enabled feature is not healthy (tooltip "N not healthy") | settings.js#renderNav, model.health | app | todo |
| SETM-026 | Nav / Deep link | `settings.open/nav {section,page,row,query}`: a row's own place wins over the section named with it; a built-in id goes to the module that replaced it; unknown goes Home; `query` fills the search box and focuses it; rows are validated `[A-Za-z0-9_.+-]{1,80}`, query clipped to 100 chars | model.js#resolve, lib/features/settings.js#navFrom | fs, model, app | todo |
| SETM-027 | Nav / Highlight | Landing on a row scrolls it to centre, flashes it for 1.6 s (accent-soft), focuses its first enabled control; retries up to 25x100 ms; if never found: note "That setting is not on this page right now: Mantle did not report it" | settings.js#jumpTo | app | todo |
| SETM-028 | Nav / History | Back/forward: header arrow, Alt+Left/Alt+Right, BrowserBack/BrowserForward keys, mouse buttons 3/4; max 50 entries, one per section+page, scroll position restored; Back from search results closes the search and keeps the page; Back disabled at first entry | settings.js#histGo | app | todo |
| SETM-029 | Nav / Search box | Placeholder "Find a setting", Ctrl+F focuses and selects; typing a printable key outside any field focuses it; Esc clears; role combobox with aria-activedescendant | index.html, settings.js | app | todo |
| SETM-030 | Nav / Search | Ranking: label equals 1000, starts-with 900, words in order 700, any order 650, inside label 600, keyword phrase 560, keyword prefixes 500, fuzzy 150 to 300; ties Mantle before Windows, section less than page less than row less than feature less than option less than shortcut less than catalog, shorter label, registry order; 12 per group | model.js#score,search | model, whole | todo |
| SETM-031 | Nav / Search | Index covers sections, pages, static rows (ROWS), module search rows, every feature, every feature option, every shortcut, and the ms-settings catalog (claimed pages fold their name into the claiming row; page and row with same name show as one hit) | model.js#buildIndex | model, whole | todo |
| SETM-032 | Nav / Search results | Results page "Results for <q>", groups "In Mantle Settings" and "Windows Settings", matched text highlighted, up/down arrows move selection, Enter opens it (catalog entry with no section opens Windows page); "No matches"; Enter with no match opens `ms-settings:` | settings.js#vSearch,activate | app | todo |
| SETM-033 | Nav / Search empty | No hits: "Nothing in Mantle Settings matches <q>." with button "Search Windows Settings" | settings.js#vSearch | app | todo |
| SETM-034 | Nav / Search partial | If catalog or mantle reads fail, a line in results: "Windows Settings pages are not searched: <why>" / "Mantle's features and shortcuts are not searched: <why>" | settings.js#vSearch | none | todo |
| SETM-035 | Nav / More links | Each built-in section foots with "Related settings" (links into Mantle for claimed pages) and "More in Windows Settings" (rest open the real page); lists per section in model.MORE (home, display, sound, bluetooth, network, power, notifications, apps, mantle, shortcuts, about) | model.js MORE,more, settings.js#moreCard | model | todo |
| SETM-036 | Nav / Claims | A module `uri` claims a Windows page for Mantle: launcher lists it as "Mantle Settings", search folds it in, in-window links go to the module; first file by name wins; a DOWN module claims nothing | model.js#sectionModel,claimOf, lib/launcher-index.js | whole, fs, lib/launcher-index.test.js | todo |

## Framework: sections, banner, notes
| ID | Area | Function | Electron source | Electron test | Native |
|---|---|---|---|---|---|
| SETM-037 | Sections / Load | Page files ui/settings/sections/<id>.js (and optional main half lib/features/_settings-sections/<id>.js) are discovered on each open: `<id>` lower-case letters/digits/dash, files starting `_` and `*.test.js` ignored, id may not be a built-in id | lib/features/settings.js#sectionsList, kit.js#load | fs, whole | todo |
| SETM-038 | Sections / DOWN | Module that fails to compile, throws while loading, has wrong id, no section() call, bad shape, main half down, or two replacing the same built-in is DOWN: stays in sidebar with warning dot, page says "This section is down, so nothing of it is drawn" + reason, replaces nothing | kit.js#load, model.js#checkSection, settings.js V.down | fs, whole, model | todo |
| SETM-039 | Sections / Render fail | render() that throws or rejects shows "<title> could not be drawn: <why>" with button "Try again" (remounts) | settings.js#modFail | none | todo |
| SETM-040 | Sections / Fixtures | fx-broken (throws on load), fx-bad (wrong id), fx-lonely (one-page section), fx-system (every kit control): test-only, listed with MANTLE_SETTINGS_FIXTURES=1 in --test | ui/settings/test-sections/, lib/features/settings.js | fs, whole | todo |
| SETM-041 | Sections / Main halves | Each main command is `settings.<id>.<name>` (letters, digits, _); a throw becomes `{ok:false, reason:"<id>.<name> failed (...)"}` and logs only error kind and file:line; install() runs once (again after a game rest), stop fn run on stop; env has sidecar, log, settings store (`sections.<id>` in features/settings.json), onRest, elevate, push (events must start `settings.<id>.`) | lib/features/settings.js#loadMainSections,sectionEnv | fs | todo |
| SETM-042 | Sections / Overview | Section with pages opens an overview card: icon, title, help, chevron per page; a built-in replaced by a module becomes a page "Section > Page" crumb | settings.js V.overview, model.js | app, model | todo |
| SETM-043 | Banner | Page-wide problems show in a red banner (role alert): bridge, hello, sections list, qs.watch refused, a live-update handler error; plus dismissible toast ("Dismiss") for notes with no row; info toasts vanish after 7 s | settings.js#renderBanner,note | none | todo |
| SETM-044 | Notes | A set shows pending, then the ANSWER decides: ok:false or throw springs back and writes the reason under the row; `dryRun` shows "Test run: nothing was changed" as a quiet note; read-back value wins | settings.js#act, kit.js#act | kit-field.test.js, app | todo |
| SETM-045 | Notes | All reasons pass through `plain()`: no sidecar/command names/errno in view; maps to plain sentences (e.g. "Windows took too long to answer. Try again in a moment", "Mantle's connection to Windows is restarting...") | model.js#plain | model, sec/plainwords | todo |
| SETM-046 | Retry | A failed data read shows "<What> could not be read: <why>" with "Try again"; loading shows "Reading <what>..." | settings.js#gate,failCard | app | todo |
| SETM-047 | Keyboard | Enter in a field with data-enter clicks its action; Esc in a password field clears and closes it; Enter in number/text input commits; segmented chips and switches are real buttons (role radio/switch, aria-checked) | settings.js keydown handlers | none | todo |
| SETM-048 | Restore defaults | There is NO global "restore defaults" or global undo in Settings. Per-option "Reset" (back to default, shows "Back to <default>" tooltip), "Reset glass", "Reset all" app limits and Undo exist (see rows below) | settings.js optionCtl | none | todo |

## Kit controls (every module page draws with these)
| ID | Area | Function | Electron source | Electron test | Native |
|---|---|---|---|---|---|
| SETM-049 | Kit / toggle | `kit.toggle`: On/Off label + switch button (role switch); click flips, calls onChange(bool), pending state while in flight | ui/settings/kit.js#toggle | kit-field.test.js, sec/* | todo |
| SETM-050 | Kit / choice | `kit.choice`: segmented chips (default for up to 4 options each label 14 chars or less) or dropdown inside .selwrap with chevron; picking the current value does nothing | kit.js#choice | kit-choice.test.js | todo |
| SETM-051 | Kit / slider | `kit.slider`: min/max/step/unit/format; onInput fires live once per frame, onChange on release; a held slider is not moved by set({value}); shows "-" when value null | kit.js#slider | none | todo |
| SETM-052 | Kit / number, text | `kit.number`/`kit.text`(password): commit on change/Enter/blur; refused value springs back INTO the focused box with its reason; set({value}) never overwrites a box being typed in | kit.js#field | kit-field.test.js | todo |
| SETM-053 | Kit / button | `kit.button`: row with one button (tone acc/quiet), disabled while busy | kit.js#button | sec/* | todo |
| SETM-054 | Kit / folder | `kit.folder`: path line + "Choose..." (settings.pickPath); "No folder chosen" when empty | kit.js#folder | none | todo |
| SETM-055 | Kit / status | `kit.status`: read-only value with a dot (ok green, warn, info) | kit.js#status | sec/* | todo |
| SETM-056 | Kit / list | `kit.list`: rows `<row>.<key>` each with value and action buttons (tone, admin shield, disabled+reason tooltip), per-item busy/notes, set({items}) redraws only changed items, empty text through plain() | kit.js#list | sec/* | todo |
| SETM-057 | Kit / reasons | Every control can show `reason` (warn line), `info` (quiet line), `help`, `mono` help, `disabled`, `icon` tile, group titles with aside note and row id | kit.js#base,group,card | none | todo |
| SETM-058 | Kit / admin | Row `admin: '<does>'` shows ADMIN shield badge and "Windows will ask for administrator permission to <does>."; waiting: "Waiting for the administrator prompt..."; cancelled: "You said no to the administrator prompt; nothing changed." | kit.js#act,admin, lib/elevate.js | fs | todo |
| SETM-059 | Kit / helpers | kit.call (always resolves, `{ok:false,reason}` on failure), kit.watch(event) until dispose, kit.every(ms>=250) paused while hidden, kit.open, kit.openWindows, kit.highlight, kit.test, kit.disposed; dispose on leave/close/search | kit.js | none | todo |
| SETM-060 | Kit / themes | Controls use only --lg-* tokens (light + dark sets via data-theme); no hard-coded colours; reduced-motion keeps switch colour transition only | settings.css, SECTIONS.md | kit-choice.test.js | todo |

## Home page
| ID | Area | Function | Electron source | Electron test | Native |
|---|---|---|---|---|---|
| SETM-061 | Home / Tiles | Tiles (click opens the section): Sound (volume % or Muted, default output name), Network (SSID/Ethernet/Not connected + words), Bluetooth (On/Off/No radio), Power & battery (percent, power mode, battery words), Display (brightness %, Night light on/off), Mantle ("N of M features on", "All healthy" / "N feature(s) need(s) a look" / "Resting: ...") | settings.js V.home | app | todo |
| SETM-062 | Home / Summary | Line under title only names what needs a look: "Needs a look: <tile names>" | settings.js V.home | none | todo |
| SETM-063 | Home / Attention | Card "Mantle needs a look" (or "Mantle is resting"): Settings' own problems (e.g. missing icon), features not healthy grouped by shared reason/keys cause (one line each, opens that feature's row), resting features "Resting while <why>" | settings.js V.home,attnLines, model.health | app | todo |
| SETM-064 | Home / Live | Home re-reads audio every 10 s; qs/net/battery/pm updates arrive by push | settings.js#startLoops | none | todo |

## Mantle page (features, switcher, keep ready, options)
| ID | Area | Function | Electron source | Electron test | Native |
|---|---|---|---|---|---|
| SETM-065 | Mantle / Header | Title "Mantle", line "N features . N on", "N need(s) attention", "N resting"; read failure shows fail card with Try again | settings.js V.mantle | app | todo |
| SETM-066 | Mantle / Switcher Alt+Tab | Switch "Alt+Tab" (row `mantle.switcher`, key `altTab`, default on): Mantle's window switcher instead of Windows'; reason line when not live (e.g. turned off, resting, hook not confirmed) | settings.js, lib/switcher.js | fs, lib/switcher.test.js | todo |
| SETM-067 | Mantle / Switcher panels | Switch "Win+N and Win+A" (row `mantle.switcher.winKeys`, key `winKeys`, default on): Mantle's notification center and quick settings; `settings.switcher {altTab?,winKeys?}` refuses an empty patch; "the switch did not move" if state differs | settings.js, lib/features/settings.js | fs | todo |
| SETM-068 | Mantle / Keep ready | Group "Keep ready in the background" (aside: "Off: each opens fresh and uses no memory while closed"): switch per surface (Mantle Settings 45 MB, Task Manager 35 MB, Files 35 MB), all default off; help states memory cost and Win+I/Ctrl+Shift+Esc/Win+E | settings.js, lib/keep-ready.js | fs, lib/keep-ready.test.js | todo |
| SETM-069 | Mantle / Keep ready | `settings.keepReady {surface,on}` unknown surface refused in words; unreadable keep-ready.json moved aside to .unreadable and said | lib/keep-ready.js | lib/keep-ready.test.js | todo |
| SETM-070 | Mantle / Features | Group "Features" (aside "The same switches as the tray menu"): one card per feature with a letter tile (accent when on+healthy), state line (Off / On + chords / On, starting / Stopped / Resting while ... / keys are Windows' right now: why), and switch (`settings.enable {feature,on}`) | settings.js V.mantle, model.featureState | app, model | todo |
| SETM-071 | Mantle / Feature state | Look-away blur's line shows camera state ("Watching", "Paused while another app uses the camera") | model.featureState | model | todo |
| SETM-072 | Mantle / Options | Sub-rows under a feature render by option type: bool switch, choice (segmented when up to 3 choices and 30 chars total else dropdown), number (min/max/step/unit), text, folder ("Change..." + path); disabled while the feature is off; "Reset" button for non-default non-bool; unknown type: "This page cannot edit a <type> option yet" | settings.js#optionCtl | none | todo |
| SETM-073 | Mantle / Options | `settings.option {feature,key,value}` validates (bool must be boolean, choice in list, number in min/max with unit in message, text up to 500 chars, folder absolute `C:\` or `\\` and existing); null resets to default; running feature hears it via onOption; logs without text/folder values | lib/features.js#checkOption,setOption | lib/features.test.js | todo |
| SETM-074 | Mantle / Options | `settings.pickFolder {feature,key}` opens a folder dialog titled with the option label starting at its current value; sets it on pick | lib/features/settings.js | fs | todo |

### Feature switches (stored in features.json, name to boolean; missing = default)
| ID | Area | Function | Electron source | Electron test | Native |
|---|---|---|---|---|---|
| SETM-075 | Feature / apppower | `apppower` "App power (CPU, memory and graphics limits)": default on | lib/features/apppower.js | lib/features/apppower.test.js | todo |
| SETM-076 | Feature / autopause | `autopause` "Spotify auto-pause": default on, rests in games | lib/features/autopause.js | none | todo |
| SETM-077 | Feature / clipboard | `clipboard` "Clipboard history (Win+V)": default on, rests | lib/features/clipboard.js | none | todo |
| SETM-078 | Feature / clips | `clips` "Clips and Record (Win+G)": default on, does not rest | lib/features/clips.js | none | todo |
| SETM-079 | Feature / corners | `corners` "Hot corners": default on, rests | lib/features/corners.js | lib/features/corners.test.js | todo |
| SETM-080 | Feature / deskmenu | `deskmenu` "Desktop right-click menu (Shift+right-click: Windows' own)": default on, rests | lib/features/deskmenu.js | lib/deskmenu.e2e.test.js | todo |
| SETM-081 | Feature / downloads | `downloads` "Downloads shelf (bar)": default on, rests | lib/features/downloads.js | none | todo |
| SETM-082 | Feature / emoji | `emoji` "Emoji picker (Win+.)": default on, rests | lib/features/emoji.js | none | todo |
| SETM-083 | Feature / files | `files` "Mantle Files (Win+E)": default on, does not rest | lib/features/files.js | none | todo |
| SETM-084 | Feature / gamemode | `gamemode` "Game mode (quick settings)": default on | lib/features/gamemode.js | lib/features/gamemode.test.js | todo |
| SETM-085 | Feature / gcal | `gcal` "Google Calendar in the clock's calendar": default on, rests | lib/features/gcal.js | lib/features/gcal.test.js | todo |
| SETM-086 | Feature / jumplist | `jumplist` "Jump lists (right-click an app on the bar)": default on | lib/features/jumplist.js | none | todo |
| SETM-087 | Feature / layouts | `layouts` "Window layouts (Mantle menu > Layouts)": default on, rests | lib/features/layouts.js | none | todo |
| SETM-088 | Feature / lookaway | `lookaway` "Blur the screen when I look away": default OFF (uses the camera), rests | lib/features/lookaway.js | none | todo |
| SETM-089 | Feature / maxfold | `maxfold` "Unfold when windows maximize": default on, rests | lib/features/maxfold.js | none | todo |
| SETM-090 | Feature / notes | `notes` "Quick notes (Win+Alt+N)": default on, rests | lib/features/notes.js | none | todo |
| SETM-091 | Feature / ontop | `ontop` "Keep a window on top (Win+Ctrl+T)": default on, rests | lib/features/ontop.js | none | todo |
| SETM-092 | Feature / osd | `osd` "Volume and brightness pop-up": default on, rests; chords VolumeUp, VolumeDown, VolumeMute | lib/features/osd.js | none | todo |
| SETM-093 | Feature / player | `player` "Mantle Player": default on, does not rest | lib/features/player.js | none | todo |
| SETM-094 | Feature / powermode | `powermode` "Power mode switch (quick settings)": default on | lib/features/powermode.js | none | todo |
| SETM-095 | Feature / project | `project` "Project (Win+P)": default on, rests | lib/features/project.js | none | todo |
| SETM-096 | Feature / qstiles | `qstiles` "Quick settings tiles (Do not disturb, Airplane mode, Focus)": default on | lib/features/qstiles.js | none | todo |
| SETM-097 | Feature / quicklinks | `quicklinks` "Quick links (Win+X)": default on, rests | lib/features/quicklinks.js | none | todo |
| SETM-098 | Feature / ramreclaim | `ramreclaim` "Turn off Windows pieces Mantle replaces (saves memory)": default on | lib/features/ramreclaim.js | lib/features/ramreclaim.test.js | todo |
| SETM-099 | Feature / run | `run` "Run (Win+R)": default on, rests | lib/features/run.js | none | todo |
| SETM-100 | Feature / screenrec | `screenrec` "Screen recording (Record in Win+Shift+S)": default on, rests | lib/features/screenrec.js | none | todo |
| SETM-101 | Feature / screenshot | `screenshot` "Screenshots (Win+Shift+S, PrintScreen)": default on, rests | lib/features/screenshot.js | none | todo |
| SETM-102 | Feature / search | `search` "Search (Win+S)": default on, rests | lib/features/search.js | none | todo |
| SETM-103 | Feature / settings | `settings` "Mantle Settings (Win+I)": default on, rests | lib/features/settings.js | fs | todo |
| SETM-104 | Feature / snap | `snap` "Window snapping (Win+arrows, Win+Z)": default on, rests | lib/features/snap.js | none | todo |
| SETM-105 | Feature / spotify | `spotify` "Spotify queue (now-playing card)": default on | lib/features/spotify.js | none | todo |
| SETM-106 | Feature / taskmgr | `taskmgr` "Task manager (free RAM for games)": default on, Ctrl+Shift+Esc, rests | lib/features/taskmgr.js | none | todo |
| SETM-107 | Feature / taskview | `taskview` "Task View (Win+Tab)": default on, rests | lib/features/taskview.js | none | todo |
| SETM-108 | Feature / timer | `timer` "Timer and stopwatch (top bar)": default on | lib/features/timer.js | none | todo |
| SETM-109 | Feature / toasts | `toasts` "Show notifications in Mantle's style": default on, rests | lib/features/toasts.js | none | todo |
| SETM-110 | Feature / topbar | `topbar` "Top menu bar": default on, rests, chord Win+Alt+T | lib/features/topbar.js | lib/features/topbar.test.js | todo |
| SETM-111 | Feature / unfold | `unfold` "Unfold on unlock and wake": default on, rests | lib/features/unfold.js | none | todo |
| SETM-112 | Feature / updates | `updates` "Check for Mantle updates": default on | lib/features/updates.js | lib/updater.test.js | todo |
| SETM-113 | Feature / widgethide | `widgethide` "Hide desktop widgets under windows": default on | lib/features/widgethide.js | none | todo |
| SETM-114 | Feature / helmbadge | `helmbadge` (lib/features/helmbadge.js) is in the feature table; its label and default were not read (see Not covered) | lib/features/helmbadge.js | lib/helm-badge.test.js | todo |

### Feature options (stored in features/<feature>.json, key to value; missing = default)
| ID | Area | Function | Electron source | Electron test | Native |
|---|---|---|---|---|---|
| SETM-115 | Option / autopause | `autopause.apps` choice "Pause Spotify while": `media` "Another app plays video or sound" (default) / `any` "Any other app makes a sound" | lib/features/autopause.js | none | todo |
| SETM-116 | Option / clips | `clips.winG` bool "Win+G opens Mantle's Record panel (Windows' Game Bar otherwise)", default true | lib/features/clips.js | none | todo |
| SETM-117 | Option / emoji | `emoji.closeAfterInsert` bool "Close after picking", default false (Enter types and stays open, Shift+Enter types and closes; on = reverse) | lib/features/emoji.js | none | todo |
| SETM-118 | Option / files | `files.showHidden` bool "Show hidden files and folders", default false | lib/features/files.js | none | todo |
| SETM-119 | Option / files | `files.showExtensions` bool "Show file name extensions", default true | lib/features/files.js | none | todo |
| SETM-120 | Option / files | `files.defaultView` choice "View for folders you have not set": details (default) / list / grid | lib/features/files.js | none | todo |
| SETM-121 | Option / files | `files.startIn` choice "Win+E opens": `last` The last folder (default) / `home` Home | lib/features/files.js | none | todo |
| SETM-122 | Option / gamemode | `gamemode.auto` bool "Turn on by itself for games", default true | lib/features/gamemode.js | lib/features/gamemode.test.js | todo |
| SETM-123 | Option / gamemode | `gamemode.liftSaver` bool "Lift Energy saver while gaming on battery", default false | lib/features/gamemode.js | lib/features/gamemode.test.js | todo |
| SETM-124 | Option / gamemode | `gamemode.boost` bool "Boost: free memory and power for the game", default true | lib/features/gamemode.js | lib/features/gamemode-boost.e2e.test.js | todo |
| SETM-125 | Option / lookaway | `lookaway.delay` number "Blur after looking away for", 0.5 to 10 step 0.5 unit s, default 1.5 | lib/features/lookaway.js | none | todo |
| SETM-126 | Option / lookaway | `lookaway.turn` choice "A turned head counts as away": small / normal (default, "Clear turn") / never | lib/features/lookaway.js | none | todo |
| SETM-127 | Option / player | `player.folderQueue` bool "Opening one file queues the rest of its folder", default true | lib/features/player.js | none | todo |
| SETM-128 | Option / player | `player.resume` bool "Resume videos where you left off" (per file; music starts at beginning), default true | lib/features/player.js | none | todo |
| SETM-129 | Option / screenrec | `screenrec.fps` choice "Frame rate" 30 (default) / 60 (numbers, not strings) | lib/features/screenrec.js | none | todo |
| SETM-130 | Option / screenrec | `screenrec.audio` bool "Record the sound playing on this PC", default true | lib/features/screenrec.js | none | todo |
| SETM-131 | Option / screenrec | `screenrec.mic` bool "Record the microphone too", default false | lib/features/screenrec.js | none | todo |
| SETM-132 | Option / screenrec | `screenrec.saveDir` folder "Save recordings to", default null = Videos\Captures | lib/features/screenrec.js | none | todo |
| SETM-133 | Option / screenshot | `screenshot.mode` choice "Start in": rect (default) / window / full; last pick remembered | lib/features/screenshot.js | none | todo |
| SETM-134 | Option / screenshot | `screenshot.autoSave` bool "Also save every screenshot automatically", default false | lib/features/screenshot.js | none | todo |
| SETM-135 | Option / screenshot | `screenshot.saveDir` folder "Save screenshots to", default null = Windows' Screenshots folder | lib/features/screenshot.js | none | todo |
| SETM-136 | Option / spotify | `spotify.closeForGames` bool "Close Spotify while I game", default true | lib/features/spotify.js, _spotify-revive.js | none | todo |
| SETM-137 | Option / spotify | `spotify.reviveAfterGames` bool "Restart Spotify after games", default true | lib/features/spotify.js, _spotify-revive.js | none | todo |
| SETM-138 | Option / spotify | `spotify.trimAboveMB` number "Trim the hidden Spotify above", 50 to 4000 step 50 MB, default TRIM_ABOVE_MB (value not read) | lib/features/spotify.js | none | todo |
| SETM-139 | Option / taskmgr | `taskmgr.autoFree` bool "Free up RAM when a game starts", default false | lib/features/taskmgr.js | none | todo |
| SETM-140 | Option / topbar | `topbar.hidden` bool "Hide the top bar" (Win+Alt+T or the top edge brings it back), default false | lib/features/topbar.js | lib/main-tbhide.test.js | todo |
| SETM-141 | Option / topbar | `topbar.overGames` bool "Keep the top bar over full-screen games", default false | lib/features/topbar.js | none | todo |
| SETM-142 | Option / unfold | `unfold.when` choice "Play it": `unlock` Every unlock and wake (default) / `wake` Only when I open the lid or wake it | lib/features/unfold.js | none | todo |

### Hot corners
| ID | Area | Function | Electron source | Electron test | Native |
|---|---|---|---|---|---|
| SETM-143 | Mantle / Hot corners | Under the `corners` feature: four dropdowns (top-left, top-right, bottom-left, bottom-right) with actions Off, Task view, Show desktop, Launcher, Quick settings, Lock screen; defaults tl Task view, tr Off, bl Off, br Show desktop; saved in features/corners.json `actions`; `corners.set {corner,action,via}` | settings.js#cornersRow, lib/features/_corners.js | lib/features/corners.test.js | todo |
| SETM-144 | Mantle / Hot corners | Lines: "Hot corners could not be read: ...", "The corners cannot fire: <watch reason>", "The last try did not run: <corner (said)>", help "Push the pointer into a screen corner to run its action" / "Turn Hot corners on to use them"; dropdowns disabled when the feature is off | settings.js#cornersRow | none | todo |

## Shortcuts page
| ID | Area | Function | Electron source | Electron test | Native |
|---|---|---|---|---|---|
| SETM-145 | Shortcuts / List | "Every key Mantle owns": table Keys (key caps), What it does (+ "Part of <owner>"), State, On. One row per feature chord plus Alt+Tab, Win+N, Win+A; row ids `shortcuts.<keys>` | settings.js V.shortcuts, lib/features/settings.js#shortcutsOf | fs, model | todo |
| SETM-146 | Shortcuts / Names | Chord descriptions (WHAT): Win+S Search, Win+V Clipboard history, Win+Tab Task view, Win+Left/Right Snap left/right, Win+Up Maximise or snap up, Win+Down Restore or minimise, Win+Z Snap layouts, Win+Shift+S Screenshot (pick an area), PrintScreen Screenshot, VolumeUp/Down/Mute (Mantle pop-up), Win+Alt+T Hide or show the top bar, Win+Ctrl+T Keep the window on top, Win+Alt+N Quick notes, Win+I Mantle Settings, Ctrl+Shift+Esc Task manager, Win+R Run, Win+X Quick links; unknown chord shows its keys | lib/features/settings.js WHAT | fs | todo |
| SETM-147 | Shortcuts / State | State cell: "Mantle has it" (live), else the reason ("Windows' own while Mantle rests (<why>)" or keysReason), "Off", or "Not live, and no reason came back"; green/amber pip | settings.js V.shortcuts | none | todo |
| SETM-148 | Shortcuts / Toggle | Switch per row toggles the owning feature (settings.enable) or the switcher (altTab / winKeys; Win+N and Win+A share one switch); disabled if no toggle | settings.js ACT sc-toggle | none | todo |
| SETM-149 | Shortcuts / Footer | Footnote "Rebinding is not available yet. A key that is off, or not live, goes to Windows as usual."; header line "N keys Mantle can own . N live now" | settings.js V.shortcuts | none | todo |

## About page
| ID | Area | Function | Electron source | Electron test | Native |
|---|---|---|---|---|---|
| SETM-150 | About / Version | "Mantle <version>" (package.json) with "Build <7-char commit>" (git rev-parse, else .git/HEAD, else reason "this build is not a git checkout") | settings.js V.about, lib/features/settings.js#about | fs | todo |
| SETM-151 | About / Uptime | "Running since <weekday, date, time>" with "Up for 2 d 3 hr / 3 hr 4 min / 4 min / 12 s", ticks every second while About is open | settings.js V.about, model.dur | none | todo |
| SETM-152 | About / Mode | "Running as your shell" or "Test copy" (with explanation) | settings.js V.about | none | todo |
| SETM-153 | About / Welcome | Row `about.welcome` "Welcome tour": button "Show again" (`welcome.show {via:'Settings'}`); "The welcome is already open" note | settings.js ACT welcome | lib/welcome.e2e.test.js | todo |
| SETM-154 | About / Window | Row `about.glass`: light "Frosted glass window" / "Solid window (glass is off)" + reason / "checking with Windows..."; dark "Wallpaper glass window" / "Solid window (no wallpaper picture)" | settings.js V.about | none | todo |
| SETM-155 | About / Runtime | Facts: Electron, Chromium, Node, Windows version ("Not reported" when absent) (native port: replace with .NET / WinUI / Windows build) | settings.js V.about | none | todo |
| SETM-156 | About / Logs | "Log folder" with path and button "Open log folder" (`settings.openLogs`); "Not reported" without --log | settings.js | fs | todo |
| SETM-157 | About / Windows | "All Windows settings" button opens `ms-settings:` in the real app | settings.js | fs | todo |

## Mantle-specific rows on the Windows-category pages
| ID | Area | Function | Electron source | Electron test | Native |
|---|---|---|---|---|---|
| SETM-158 | Notifications / Panels | Row `notifications.panels` "Mantle's notification and quick settings panels": switch = switcher `winKeys`; sibling row `notifications.dnd` "Do not disturb" is disabled with "Mantle cannot switch Do not disturb yet (no backend for it)" + "Open in Windows" | settings.js V.notifications | sec/notif-status | todo |
| SETM-159 | Power / Game mode | Row `power.gamemode` "Game mode": status words (Switched off in Mantle / On . <game file> . turned on by hand / Off . turns on by itself when a game starts / games are not being watched) + button "Game mode options" going to `mantle.feature.gamemode` | settings.js V.power | none | todo |
| SETM-160 | Apps / Task manager | Row `apps.taskmgr` "Task manager" button Open (`taskmgr.open {view:'apps'}`), disabled with reason when not installed, off, resting, unhealthy or not running | settings.js, model.taskmgr | model | todo |

## Personalization section (sections/personalization.js; main half = sidecar SysPerso*.cs)
| ID | Area | Function | Electron source | Electron test | Native |
|---|---|---|---|---|---|
| SETM-161 | Personalization / Section | Section `personalization`, group windows, order 40, replaces nothing; pages: Background, Colors, Themes, Lock screen, Text input, Dynamic Lighting, Start, Taskbar, Fonts, Device usage; claims ms-settings personalization, colors, themes, lockscreen, taskbar, fonts, deviceusage, etc. | sections/personalization.js | sec/personalization | todo |
| SETM-162 | Background / Mantle wallpaper | `personalization.background.mantle` status of Mantle's wallpaper (title + type, health reason, "Paused now: <reason>", count in library); `...wallpapers` button "Open Wallpapers" (`wallpaper.open`; test: dry run) | personalization.js#renderBackground | sec/personalization | todo |
| SETM-163 | Background / Built-in | Gallery of 23 built-in photos: category chips (All first, `aria-pressed`), Shuffle (`wallpaper.shuffle {category}`; disabled with reason if none usable), thumbnails radio grid with accent check, arrow/Home/End roving focus, unusable photo shows warning and reason, pick = `wallpaper.set {id:'builtin:<name>'}`, credit line "Photo by <name> on Unsplash" opening `wallpaper.creditOpen` | personalization.js#gallery, lib/wallpaper-presets.js | sec/personalization, lib/wallpaper-presets.test.js | todo |
| SETM-164 | Background / Lively | `...lively` status Running / Not running; Mantle never closes or restarts Lively | personalization.js | sec/personalization | todo |
| SETM-165 | Background / Windows picture | `...kind` segmented Picture / Solid colour / Slideshow (`perso.set` key `picture`/`solid`/`slideshow`); refusals "Pick a picture first..." / "Pick a picture album first..."; `...picture` status + preview image | personalization.js | sec/personalization | todo |
| SETM-166 | Background / Fit | `...fit` dropdown Fill, Fit, Stretch, Tile, Centre, Span (key `fit`) | personalization.js | sec/personalization | todo |
| SETM-167 | Background / Recent | `...recent` list of Windows' recent pictures with "Use" (disabled if file gone) and `...path` text "Use a picture from its path" (.jpg .png .bmp .gif .tif .heic .webp) | personalization.js | sec/personalization | todo |
| SETM-168 | Background / Solid + slideshow | `...color` text (#RRGGBB uppercased), `...slideshow` folder, `...interval` dropdown 1 min / 10 min / 30 min (default) / 1 hour / 6 hours / 1 day (+custom), `...shuffle` toggle; all read-only with reason when locked or the wallpaper service is down | personalization.js | sec/personalization | todo |
| SETM-169 | Colors / Mantle appearance | Row `personalization.colors.mantle-mode` "Mantle appearance": segmented Light / Dark / Match Windows (default Match Windows, `theme.set {mode}`); info "Following Windows: dark right now"; live via `theme` event; reasons if unreadable | personalization.js#mantleLook, lib/theme.js | sec/personalization, lib/theme.test.js, lib/theme.e2e.test.js | todo |
| SETM-170 | Colors / Look sync | `...look-sync` toggle "Sync look with Helm and Quartz" (`look.sync {on}`, default on); `...look-status` status (sync text, guard note, light top band, lock-screen failure); `...look-desktop` status "Desktop wallpaper" (shared wallpaper + Windows wallpaper + lock screen text) | personalization.js#lookSync, lib/look-host.js | sec/personalization, lib/look-host.test.js | todo |
| SETM-171 | Colors / Glass | Sliders edit the glass of the look Mantle shows now (glass.light in light, glass.dark in dark; row info says which): `...glass-blur` 0 to 64 px (seeds dark 5, light 12), `...glass-dim` "Bar tint" 20 to 90 percent (dark 50, light 45), `...glass-panel` "Panel opacity" range base (dark 83, light 76) plus offset -0.30..+0.15; each writes once on release via `look.glass {look,...}`; guard note "Top bar raised to N% over this wallpaper..." | personalization.js | sec/personalization, lib/look-guard.test.js | todo |
| SETM-172 | Colors / Glass reset | `...glass-reset` "Default glass" button "Reset glass" (disabled with info when already at defaults) | personalization.js | sec/personalization | todo |
| SETM-173 | Colors / Accent | `...accent-mode` segmented "Mantle's own" / "Custom hue" (`look.accent {hue}`), swatch of --lg-acc; `...accent-hue` slider 0 to 360 degrees (default 212 when custom); applies to the dark look only (info) | personalization.js | sec/personalization | todo |
| SETM-174 | Colors / Windows mode | `...mode` Light/Dark/Custom, `...windows-mode`, `...apps-mode` (perso.set `systemLight`, `appsLight`), `...transparency` toggle | personalization.js#renderColors | sec/personalization | todo |
| SETM-175 | Colors / Windows accent | `...auto-accent` toggle (`autoAccent`), `...accent` status, 8-column swatch grid (`accent` key), `...custom` hex text, `...accent-start` (`accentOnStart`), `...accent-titlebars` (`accentOnTitleBars`); read-only with reason when locked | personalization.js | sec/personalization | todo |
| SETM-176 | Themes | `...themes.current` status + `...themes.list` installed themes (read-only: "Read only here: <why>") | personalization.js#renderThemes | sec/personalization | todo |
| SETM-177 | Lock screen | `...lock.picture` + preview, `...lock.path` (`lockPicture`; picking turns spotlight off), `...lock.spotlight` toggle (`spotlight`), `...lock.facts` toggle (`lockFacts`), `...lock.status` and `...lock.signin` read-only | personalization.js#renderLock | sec/personalization | todo |
| SETM-178 | Text input | Read-only: touch keyboard theme, size, key background | personalization.js#renderTextInput | sec/personalization | todo |
| SETM-179 | Dynamic Lighting | Read-only: state, foreground apps control lighting, brightness, match accent | personalization.js#renderLighting | sec/personalization | todo |
| SETM-180 | Start page | `personalization.start.mantle` status "In use . N of M on", list `...features` (search, jumplist, layouts, notes) each with "Open in Mantle" going to that feature's row, button `...open` "Open Mantle settings" | personalization.js#mantlePage | sec/personalization | todo |
| SETM-181 | Taskbar page | Same for features topbar, jumplist, downloads, timer, taskview, corners, osd | personalization.js#mantlePage | sec/personalization | todo |
| SETM-182 | Fonts | `...fonts.count`, `...filter` search, `...sample` preview text (default "The quick brown fox jumps over the lazy dog"), `...install` path (.ttf .otf .ttc, per-user, no admin), list capped at 60 with each sample in its own face, "Remove" for user-installed faces; system fonts read-only | personalization.js#renderFonts | sec/personalization | todo |
| SETM-183 | Device usage | Read-only list of 7 usage kinds (Gaming, Family, Creativity, School, Entertainment, Business, Development) On/Off/Not reported | personalization.js#renderUsage | sec/personalization | todo |

## Apps section `applist` (replaces built-in Apps)
| ID | Area | Function | Electron source | Electron test | Native |
|---|---|---|---|---|---|
| SETM-184 | Apps / Installed | `apps.installed` list with search (name/publisher), sort (Name, Size, Install date, Publisher), filter by drive; per app Modify (if possible) and Uninstall each need a SECOND confirming click showing exactly what will run ("Uninstall X? Mantle will ...", Store: "Remove X and its data from your account?"), "Keep" cancels; request carries only the key; test run = dry run; polls while sizing/removing | sections/applist.js, _settings-sections/applist.js | sec/applist | todo |
| SETM-185 | Apps / Advanced | `apps.advanced.share` dropdown Off / My devices only (default) / Everyone nearby (HKCU CDP); source, archive, aliases read-only with reason | applist.js#renderAdvanced | sec/applist | todo |
| SETM-186 | Apps / Defaults | Read-only default browser/mail/PDF/file and link types (Microsoft's lock explained); `apps.defaults.player` "Mantle Player": Set up (adds Mantle Player to Windows' list then opens Default apps) and Undo (removes exactly what was added), via `player.defaults {op}` | applist.js#renderDefaults | sec/applist | todo |
| SETM-187 | Apps / Optional features, websites | Optional features (read-only, installed + Windows features) and Apps for websites (read-only, "N of M verified") | applist.js | sec/applist | todo |
| SETM-188 | Apps / Video | Four HKCU switches: `apps.video.hdr`, `.enhance`, `.battery` (Optimize for battery life / video quality), `.lowres` | applist.js#renderVideo | sec/applist | todo |
| SETM-189 | Apps / Startup | `apps.startup.<hash>` toggle per startup entry (StartupApproved only, never deletes a Run value; `settings.startup` / `settings.startupSet {item,on}`), disabled with reason when not switchable; `apps.taskmgr` Open | applist.js#renderStartup | sec/applist, fs | todo |

## App power, Windows pieces, Gaming, Accounts
| ID | Area | Function | Electron source | Electron test | Native |
|---|---|---|---|---|---|
| SETM-190 | App power / List | Mantle group, order 1005: list `apppower.rules` of apps with limits ("Efficiency mode, CPU limit 25%", " . Only until Mantle restarts" when not remembered), refreshed every 4 s; empty "No app limits yet. Open the CPU, memory or graphics meter in the top bar to set one." | sections/apppower.js | sec/apppower | todo |
| SETM-191 | App power / Remove + Undo | "Remove" per app (`apppower.forget {key}`) then an Undo card "Removed the limits for <app>" / "It runs normally again" (`apppower.restoreRules` puts back exactly the removed rule) | apppower.js | sec/apppower | todo |
| SETM-192 | App power / Reset all | `apppower.reset` "Reset all" (`apppower.resetAll`), disabled "Nothing to reset"; Undo "Removed N app limits" | apppower.js | sec/apppower | todo |
| SETM-193 | App power / Notes | Notes: feature off ("App power is turned off in Mantle's features, so these limits are not being applied"), test copy, read failure | apppower.js | sec/apppower | todo |
| SETM-194 | Windows pieces / Status | Mantle group, order 1006: `ramreclaim.status` "Turned off by Mantle" (N pieces; "Still loaded from before: N MB" until next sign-in); notes for test copy, feature off, journal error, measure reason; refresh every 10 s | sections/ramreclaim.js | sec/ramreclaim | todo |
| SETM-195 | Windows pieces / Switches | Toggles (`ramreclaim.set {id,on}`): `widgets-prelaunch` (default on), `search-highlights` (default on), `lockscreen-widgets` (default off); On = Mantle keeps that Windows piece off, effect from next sign-in / lock; states applied, already, paused, off, drift, error shown in words | ramreclaim.js, _ramreclaim-core.js | sec/ramreclaim, lib/features/ramreclaim.test.js | todo |
| SETM-196 | Windows pieces / Locked | "Your call" read-only locked-off rows (admin shield): `search-window`, `widgets-all`, `widgets-package`; "Stays on": `start-menu` (memory shown) | ramreclaim.js | sec/ramreclaim | todo |
| SETM-197 | Gaming / Game Bar | `gaming.game-bar.controller` toggle (controller Xbox button) and `.app` status Installed / Not installed (warn: Win+G does nothing until installed) | sections/gaming.js | sec/gaming | todo |
| SETM-198 | Gaming / Captures | Folder status (read-only) + "Open Clips" (`clips.open`); toggles/choices: `captures.background` (warns it costs frames), `.battery`, `.wireless`, `.buffer`, `.max-length`, `.audio`, `.mic`, `.audio-quality`, `.frame-rate`, `.quality`, `.cursor`; gated read-only when background/audio off or capture policy blocks | gaming.js, SysGaming.cs | sec/gaming | todo |
| SETM-199 | Gaming / Game Mode | `gaming.game-mode.windows` toggle (Windows Game Mode); Mantle: status, `.mantle` toggle (feature gamemode via `settings.enable`), `.mantle-auto`, `.mantle-saver`, `.mantle-boost` (via `settings.option`), action list "What Mantle game mode is doing" (Done / Test run / Not possible / Failed / Skipped), "All of Mantle's options" | gaming.js | sec/gaming | todo |
| SETM-200 | Accounts / Info, sign-in | Read-only name, type, administrator, picture; sign-in: Hello and password read-only; toggles `accounts.signin.dynamic-lock`, `.restart-apps` (`settings.accounts.signinSet`); Mantle NEVER changes the account | sections/accounts.js | sec/accounts | todo |
| SETM-201 | Accounts / Linked | Read-only: email accounts, family, OneDrive backup folders, other users, work or school | accounts.js | sec/accounts | todo |

## Google Calendar (group inside Time & language > Date & time; sections/time.js)
| ID | Area | Function | Electron source | Electron test | Native |
|---|---|---|---|---|---|
| SETM-202 | GCal / Status row | Row `time.datetime.gcal` "Google Calendar": value Off (feature off or not running, with reason) / Not connected / Signed in as <email> / Sign-in ended (account); help "Read-only: your events show in the clock's calendar; Mantle never changes them"; warns if Windows encryption (DPAPI) unavailable | sections/time.js#gcalGroup | none | todo |
| SETM-203 | GCal / Sign in | Button `time.datetime.gcal.signin` "Sign in with Google" (acc tone; "Waiting for your browser..."), shown when not signed in or sign-in ended; disabled without client file / while signing in / without DPAPI; `gcal.signIn` | time.js, lib/features/gcal.js | lib/features/gcal.test.js | todo |
| SETM-204 | GCal / Last synced | Status `time.datetime.gcal.synced` "Last synced": just now / N min ago / N h ago / N days ago / Syncing now... / Not yet; ticks every 30 s; warn reason from last sync error | time.js | none | todo |
| SETM-205 | GCal / Calendars | One toggle per calendar `time.datetime.gcal.cal.<hash>` (name, "Your main calendar" for primary); default on for primary or Google-selected, else off; saved in features/gcal.json `calendars` {id:bool} via `gcal.setCalendar`; none: "Calendars: None" with reason | time.js, lib/features/_gcal-core.js#pickCalendars | lib/features/gcal.test.js | todo |
| SETM-206 | GCal / Sign out | Button `time.datetime.gcal.signout` "Sign out" (ends access at Google, deletes sign-in and cached events); `gcal.signOut` | time.js, _gcal-store.js | lib/features/gcal.test.js | todo |
| SETM-207 | GCal / Client file | Button `time.datetime.gcal.client` "Choose Google client file..." (`gcal.pickClient`: open dialog, .json, max 64 KB, default folder Downloads or the user's own folder; stored encrypted as client.bin); help says whether a Desktop-app client is saved; reason `clientReason` | time.js, lib/features/gcal.js | lib/features/gcal.test.js | todo |
| SETM-208 | GCal / Live | Group redraws from `gcal.status` push only when something but the clock changed; test run: signIn/signOut dry runs | time.js | none | todo |

## Settings key index
Storage root: `<userData>` = `%APPDATA%\mantle` (live) or `%TEMP%\mantle-test-<profile>` (test). Features files are JSON objects written whole; unreadable files are moved aside to `<name>.json.unreadable` and said.
| Key | Type | Default | Read by | Persisted by | Storage file |
|---|---|---|---|---|---|
| (each feature name, e.g. `topbar`) | bool | feature's `defaultOn` (all true except `lookaway` false) | lib/features.js | lib/features.js#saveSwitches (tray, Settings `settings.enable`) | features.json |
| `<feature>.<option key>` (27 option keys, SETM-115 to 142) | per option | per option | lib/features.js#optionView + the feature | lib/features.js#setOption | features/<feature>.json |
| `corners.actions` {tl,tr,bl,br} | object of action ids | tl taskview, tr off, bl off, br desktop | lib/features/corners.js | corners.set | features/corners.json |
| `apppower.rules` | list of {key,exe,path,label,set,at} | [] | lib/features/apppower.js | apppower.* commands (persist()) | features/apppower.json |
| `ramreclaim.pieces` | map piece id to bool | per piece defaultOn (widgets-prelaunch, search-highlights true; lockscreen-widgets false) | lib/features/ramreclaim.js | ramreclaim.set | features/ramreclaim.json (+ ramreclaim-journal.json undo record) |
| `gcal.calendars` | map calendar id to bool | primary or Google-selected = on | lib/features/_gcal-core.js | gcal.setCalendar | features/gcal.json |
| `gamemode.auto/liftSaver/boost` | bool | true/false/true | lib/features/gamemode.js | settings.option, gamemode.* commands | features/gamemode.json (+ gamemode-restore.json crash record) |
| `sections.<id>.<key>` | any | none | section main halves (env.settings) | env.settings.set | features/settings.json |
| `altTab`, `winKeys` | bool | true, true | lib/switcher.js | settings.switcher / tray | switcher.json |
| `keepReady.settings/taskmgr/files` | bool | false | lib/keep-ready.js | settings.keepReady | keep-ready.json |
| `mode` | system/light/dark | system | lib/theme.js | theme.set | theme.json |
| `lookSync`, `accent`, `glass` | bool, {hue}, {dark,light,render} | true, hue null, dark 5 px/0.50/0, light 12 px/0.45/0 | lib/look-host.js | look.sync / look.accent / look.glass | look-sync.json (local copy); shared %APPDATA%\look-sync\look.json (also Helm and Quartz) |
| `selected` (`builtin:<name>` or library id) | string | none | lib/wallpaper.js | wallpaper.set / shuffle | wallpapers/library.json |
| `settings.cache` | json | none | ui/settings/settings.js | same | page localStorage (Electron profile; native: own cache) |
| Google token / client | encrypted blobs | none | lib/features/_gcal-store.js | gcal.signIn / pickClient | gcal/token.bin, gcal/client.bin (Electron safeStorage = DPAPI), gcal/cache.json |
| Windows-backed (not Mantle prefs) | various | n/a | sidecar | perso.set, apps.videoSet, apps.advancedSet, gaming set, startup.set, accounts signinSet | Windows registry (HKCU) / StartupApproved: native app must read/write the same values |
| Other feature-store keys found by grep, NOT shown in Settings (carry over too) | various | various | lib/features/*.js | ctx.settings | emoji recents/tone/apps; downloads seenAt; files pins/bounds; jumplist pins; layouts layouts; notes active; ontop pins; qstiles focus; run history; taskmgr ticks/lastAuto/tm/bounds; timer clock/last/held; updates cache/laterTag/laterUntil; unfold when (core) |

## Notes for the port
- Settings is a hub with 3 data planes: Mantle prefs (JSON in userData), Windows state via sidecar (registry/WinRT), and live pushes (qs, net, battery, theme, look, gamemode.state, gcal.status). A native port needs a typed settings store that reads the SAME files unchanged (features.json, features/<name>.json, switcher.json, keep-ready.json, theme.json, look-sync.json, wallpapers/library.json).
- The ANSWER-has-the-last-word contract (SETM-044) is the heart of the UX: optimistic flip, spring back with a reason, dry-run in --test. Port it as one control base class, not per page.
- Rows are addressed by global row id (`<section>.<page>.<name>`); search, deep links, highlight, launcher claims and Home attention links all depend on them. Keep ids identical.
- Search ranking (SETM-030) is pure and pinned by model.test.js; port the scoring table exactly. Index also folds in the ms-settings catalog (lib/launcher-index.js SETTINGS).
- "Claims": launcher and Settings both parse section files in a vm sandbox to learn claimed ms-settings pages. A native app needs the claims as data (a manifest) instead of running JS.
- Dynamic section modules (drop a file, restart) have no native equivalent unless modules become compiled pages; the DOWN/fixture machinery (SETM-037 to 041) may be dropped only if the user agrees, because all 11 categories are built-in then.
- Glass: light = DWM acrylic with a frame read-back handshake (taskmgr.frame); dark = opaque window plus wallpaper painted by the page. In WinUI 3 use SystemBackdrop (Acrylic) and a wallpaper layer; the read-back-and-explain behaviour on About must survive.
- Cold open budget: first paint <= 300 ms or shown on plain ground (SHOW_BY_MS); Home cached values; no hidden window by default (user: save RAM). Keep-ready is opt-in only.
- A hidden/closed page must hold no `qs.watch` lease and no timers (SETM-017).
- The user asked: remove "All Windows settings" footer (kept only on About); nothing links out to ms-settings when a module claims the page (SETM-036).
- The user asked for Mantle appearance default Match Windows; light shows when Windows mode unreadable and says so (SETM-169).
- The glass sliders edit the look Mantle shows now (light vs dark) and write once on release; guard note logic is subtle (personalization.js#guardRaised).
- Uninstall/Modify need the second confirm click and carry only the app key to the sidecar (privacy and safety, SETM-184).
- Reasons are plain words only: `plain()` rewrites transport errors; the whole.test.js jargon and sentence-case checks (labels) are part of the contract.
- Admin actions go through lib/elevate.js with the Settings window as UAC owner; until present, "Mantle cannot ask for administrator rights yet".
- Wallpaper gallery thumbnails, font samples in their own face, and image previews are file:// loads; WinUI needs ImageSource and font fallback; CSP forbids inline styles in the web page (not relevant natively).
- Hard in WinUI 3 (WebView2 fallback candidates): the font-sample list (per-row font family), the wallpaper thumbnail grid with roving focus, and the segmented/dropdown kit if a section stays web-based.
- Sidecar commands used by this lane: taskmgr.frame, focus, startup.list/startup.set, perso.colors/background/more/set/lock/lockPicture/fonts/fontInstall/fontRemove, apps.installed/uninstall/modify/defaults/features/websites/video/videoSet/advanced/advancedSet, accounts.* (SysAccounts.cs), gaming (SysGaming.cs), apppower.rules, qs.*, powermode.*, net.get, battery.get.

## Not covered
- Built-in Windows pages drawn in settings.js (V.display, V.sound, V.bluetooth, V.network, V.power battery/mode, V.notifications DND, brightness, night light, mixer, Wi-Fi, startup) are mostly replaced by the system, devices, internet categories (lane setw); only their Mantle-specific rows are listed (SETM-158 to 160).
- `lib/features/helmbadge.js` label, default and options were not read; the full defaults of `spotify.trimAboveMB`, `clipboard/search/etc` chord lists beyond those shown, and `hot corner` icon labels were not read.
- Exact home-tile wording for every unavailable state, and the full text of every `reason` string in the personalization / applist / gaming pages (summarised, not enumerated).
- `ui/settings/mock-mantle.js` (test mock) and `shoot.js` / `test-browser.js` were not inventoried (tooling).
- Persistence of look sync internals (CONTRACT in the look-sync project), wallpaper library internals, and each feature's non-Settings behaviour belong to other lanes. The "other feature-store keys" row was found by grep and may be incomplete.
- Unit-test mapping is by file; individual assertions were not matched per row (many rows say `none` where only whole-page tests exist).
