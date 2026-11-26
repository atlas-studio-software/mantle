# Lane r16-trial-fixes

Two small fixes from the owner's trial, each with a UI test.

- The launcher's shadow room: the window is larger than its island (ShadowRoom in Theme/LvLauncher.xaml, the room the
  float-hi shadow is drawn in). A press there landed on the launcher's own window, so it never lost the focus and stayed
  open. A window region would pass the press on but clips the drawing too (the shadow is drawn in that room), so the
  root now takes a press outside the pane's rectangle and closes the launcher (log: "launcher: hidden (a click outside
  the island)"). The press is not passed on to the window beneath. Test: LauncherTests.A_click_in_the_shadow_room_below_the_island_closes_the_launcher_and_a_click_on_the_island_does_not.
- The levitated pop-ups (quick settings, Downloads) have no such room: PopupWindow clips its window to the island's
  rounded shape (the motion slack below is in the region only while a motion runs) and PopupHost counts a click outside
  the island's rectangle as outside. Test: QuickSettingsTests.A_click_just_below_the_island_closes_quick_settings_as_an_outside_click.
- MANTLE_TEST_NET=off: the Wi-Fi list one level down now agrees with the tile (nothing connected; a saved network reads
  "Saved · <security>" with Connect). Test: QuickSettingsViewsTests.Offline_test_switch_draws_the_wifi_list_with_no_network_connected.
