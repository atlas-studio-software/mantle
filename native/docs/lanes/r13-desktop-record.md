# Lane r13-desktop-record (2026-11-25)

Branch `native/r13-desktop-record`, off native/r12-app-wiring b756536e. EM ticket t_38ccde (daily-driver blocker SEE-020:
native publishes look.json `desktop`, so Helm and Quartz paint native's still instead of falling back to Windows'
TranscodedWallpaper). The contract is look-sync CONTRACT.md and look-sync.js on the EM's branch dca42dc (unmerged).

## Audit: what lane r10-look already had, against dca42dc

| Contract rule | Before this lane | Now |
|---|---|---|
| Only Mantle writes `desktop` and the stills; published with sync OFF | yes (`LookSync.PublishDesktopAsync` refuses any app but mantle; DesktopPublisherTests sync-off test) | unchanged |
| Record shape `{rev, by, ok, reason, id, type, title, file, still, stillSize}` | yes, PLUS `guard{dark, light}` with `region {topDip 0, heightDip 100}` and `monitor`, or `guardReason` / `guardLightReason` | **gap closed:** exactly those 10 keys. No guard, no guardReason, no region (CONTRACT.md:385, "Retired on v2": native neither publishes it nor adds a region) |
| Write: re-read, change only desktop, rev = docRev + 1 on doc and field, tmp `look.json.<app>.<pid>.<hex>.tmp`, rename, retry EBUSY/EPERM/EACCES at 50/100/200 ms, failure in status | yes (`LookSync` is look-sync.js ported line for line: `_mutate`, `WriteAtomic`, `SafeFile.WriteAtomic` retries) | unchanged |
| Loop rules: own writer.instance, applied hash, newer field rev | yes (`LookSync.Check`, LookSyncContractTests `Loop_guard_...`, `A_noop_event_same_bytes_...`) | unchanged |
| Still first, then look.json | yes | unchanged; the test seam moved from the guard's picture read to `BeforeRecord` |
| Keep the last 3 stills | yes (`Prune`, never the one the record names, leftover .tmp removed) | unchanged |
| Once per wallpaper change, never on a timer | republished on every GLASS change too (the guard depended on dim and blur): Mantle.App listened to look host "glass", "apply" and "sync" events | **gap closed:** only start and WM_SETTINGCHANGE SPI_SETDESKWALLPAPER; no glass listener |
| Game rest keeps the last record rather than ok:false | missing | **gap closed:** `DesktopPublisherOptions.RestingWhy`; while it names a reason, a check that would publish ok:false writes nothing ("kept"), and the next check after the rest looks again. A new picture with a still is still published during a rest |
| `answers` / `wallpaperRequest` | not written, not read | unchanged, on purpose: dca42dc's own "Lane 0 freeze" says "wallpaperRequest is gone ... `migrate()` strips it ... `desktop.answers` is no longer written", and look-sync.js `migrate` deletes it. The brief's section-4 text is older than that freeze. Not built; flagged to the EM |
| Schema stays 1 | yes | unchanged (pinned in a test) |
| A record from the last run is adopted | yes, also one with a guard | a stored record with any of `guard`, `guardReason`, `guardLightReason`, `region`, `answers` (`DesktopPublisher.NotPublished`) is republished once without them |

## The still per type

Native has no wallpaper library: the desktop's picture is Windows' own wallpaper (SPI_GETDESKWALLPAPER), always published as
type `picture`, `file` = that path, still = a full-size JPEG at quality 0.9, at most the cover size of the primary screen
(`DesktopPublisher.CoverSize`, WinRT BitmapEncoder in `WindowsWallpaper.Still`). A plain colour or a missing file is
`ok:false` with the reason. NOT producible by native yet: `builtin`, `gif` (a .gif set as Windows' wallpaper would be
decoded at its first frame by BitmapDecoder, but it is still typed `picture`), `video` and `web`. These come with a native
wallpaper library.

## Mantle.App hooks (the WDE's area; the smallest change)

- `Shell/Glass/DesktopPublish.cs`: `Start(LookHost look, SafeFile files, Func<string?>? restingWhy = null)` (was two
  parameters); the options pass `RestingWhy = restingWhy` instead of `Glass = () => look.Local()["glass"]`; the
  `look.OnChange(...)` glass listener and its `_off` field are removed; `WindowsWallpaper.Picture` (the guard's decode) is
  removed. Header comments follow.
- `App.xaml.cs`, the `DesktopPublish.Start` call after `DesktopGlass.Feed`: passes `() => _rest?.RestingWhy` (the
  RestCoordinator field, made later in the start; the lambda reads it at each check).

## Effect on native's own surfaces

Native's windows paint `Wash.Alpha(glass, record)`. With no guard in native's own record, that answer stays at Dim (with the
reason) unless Electron Mantle published a guard; native's own NativeGuard (the 140 DIP band, SEE-017) still raises its
windows where the still needs it. That is the v2 rule: nothing new is built on the top band.

## Tests (Mantle.Core.Tests/Look/DesktopPublisherTests.cs, temp folders only)

Changed: `The_still_is_written_before_the_record_and_the_record_names_it` (hook `BeforeRecord`, no picture read back),
`The_record_rev_is_one_past_a_concurrent_writers_and_its_field_is_kept` and `A_locked_look_json_is_retried_and_the_record_lands`
(the same hook), `No_picture_publishes_ok_false_with_the_reason` (no guardReason), `A_source_that_throws_is_a_reason_not_a_crash`
(the still throws: ok:false with the reason), `A_record_from_the_last_run_that_still_fits_is_adopted_not_republished` (no Glass).
Replaced: `The_record_has_no_region_key_and_its_guard_is_the_contracts_100_DIP_band` by
`The_record_is_the_v2_shape_no_guard_no_region_no_answers_and_schema_stays_1` (also an old wallpaperRequest is dropped), and
`The_same_wallpaper_again_publishes_nothing_and_a_new_dim_republishes_the_same_still` by
`The_same_wallpaper_again_publishes_nothing_and_no_glass_input_republishes`: both pinned the v1 top band the contract retired.
New: `A_record_with_a_v1_top_band_from_the_last_run_is_republished_once_without_it`,
`During_a_game_rest_a_check_that_would_share_ok_false_keeps_the_last_record`, `A_rest_never_holds_back_a_new_picture_that_has_a_still`,
`A_torn_look_json_is_healed_and_the_record_lands_valid_naming_a_still_that_is_there`.
look-sync test vectors: dca42dc has none for the desktop record (its fixtures are accent, lift and guard), so none are used.

## Not verified (no real desktop)

- Helm and Quartz actually painting native's still while native is the shell (no run with both apps).
- `WindowsWallpaper.Still` on a real wallpaper (WinRT decode + JPEG encode), and the WM_SETTINGCHANGE trigger.
- A real game rest: `RestCoordinator.RestingWhy` during a game; and nothing re-checks when a rest ends (the next wallpaper
  change or start does), so a wallpaper that went missing during a rest is shared as ok:false only then.
- Electron and native taking turns on the same profile: Electron (v1) republishes a guard; native republishes once without it.
