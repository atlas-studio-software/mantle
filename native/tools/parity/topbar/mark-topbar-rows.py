import re, sys
# usage: python -I mark-topbar-rows.py <repo native dir> [ui [<sha> for the ported rows]]
native = sys.argv[1]
ui = len(sys.argv) > 2 and sys.argv[2] == 'ui'
P = 'Mantle.Core.Tests/Features/TopBar/TopBarPureTests.cs#'
F = 'Mantle.Core.Tests/Features/TopBar/TopBarFeatureTests.cs#'
U = 'Mantle.UiTests/TopBar/TopBarSurfaceTests.cs#'
rows = {}
def put(ids, status):
    for i in ids: rows.setdefault(i, status)
put(['TOP-018', 'TOP-019', 'TOP-021', 'TOP-022', 'TOP-023'], 'tested:' + P + 'Titles_glyph_then_bold_app_then_its_menus_or_Window_and_never_empty_TOP_018_019_021_022_023')
put(['TOP-020'], 'tested:' + P + 'App_name_rule_TOP_020')
put(['TOP-024', 'TOP-025'], 'tested:' + P + 'Fit_app_name_first_then_menus_into_more_TOP_024_025')
put(['TOP-303'], 'tested:' + P + 'Mantle_menu_offers_hide_or_show_while_peeking_TOP_303')
put(['TOP-241', 'TOP-243', 'TOP-245', 'TOP-246', 'TOP-249'], 'tested:' + P + 'Reserve_size_strip_gap_work_area_and_re_register_TOP_241_243_245_246_249')
put(['TOP-260', 'TOP-266'], 'tested:' + P + 'Aside_priority_lock_screen_and_words_TOP_260_266')
put(['TOP-236', 'TOP-237', 'TOP-238'], 'tested:' + P + 'Display_verdict_TOP_236_237_238')
put(['TOP-234', 'TOP-235'], 'tested:' + P + 'Burst_first_at_once_then_one_trailing_run_with_each_reason_once_TOP_234_235')
put(['TOP-290', 'TOP-291'], 'tested:' + P + 'Peek_constants_and_edge_TOP_290_291')
put(['TOP-292', 'TOP-294', 'TOP-295'], 'tested:' + P + 'Peek_dwell_flick_and_buttons_TOP_292_294_295')
put(['TOP-296'], 'tested:' + P + 'Peek_never_and_mid_peek_hides_TOP_296')
put(['TOP-299', 'TOP-300', 'TOP-301'], 'tested:' + P + 'Peek_grace_menus_and_cursor_TOP_299_300_301')
put(['TOP-293'], 'tested:' + P + 'Peek_starts_disarmed_TOP_293')
put(['TOP-201', 'TOP-203', 'TOP-204', 'TOP-205'], 'tested:' + F + 'The_module_fields_and_options_are_Electrons_TOP_201_203_204_205')
put(['TOP-222', 'TOP-242', 'TOP-253'], 'tested:' + F + 'A_test_run_shows_the_bar_and_never_asks_for_the_app_bar_TOP_222_242_253')
put(['TOP-247', 'TOP-248'], 'tested:' + F + 'A_live_run_reserves_the_strip_in_physical_px_checks_the_work_area_and_re_asks_TOP_241_243_247_248')
put(['TOP-282', 'TOP-283', 'TOP-287', 'BAR-348'], 'tested:' + F + 'Live_hidden_by_hand_lets_the_strip_go_and_shown_takes_it_back_TOP_282_283_287_BAR_348')
put(['TOP-286'], 'tested:' + F + 'Hidden_is_kept_across_a_restart_no_strip_no_show_and_the_edge_is_watched_TOP_286')
put(['TOP-261', 'TOP-262'], 'tested:' + F + 'The_lock_screen_steps_aside_and_the_bar_comes_back_when_it_closes_TOP_261_262_266')
put(['TOP-259'], 'tested:' + F + 'A_full_screen_window_steps_aside_and_the_log_names_no_app_TOP_259_260')
put(['TOP-233'], 'tested:' + F + 'Display_changes_skip_Mantles_own_work_area_move_and_burst_the_rest_TOP_233_238_239')
put(['TOP-298', 'BAR-349'], 'tested:' + F + 'Peek_on_a_fake_cursor_shows_over_the_screen_and_hides_after_the_grace_TOP_292_298_300_BAR_349')
put(['TOP-302'], 'tested:' + F + 'Show_during_a_peek_keeps_it_up_for_good_TOP_302')
put(['TOP-265'], 'tested:' + F + 'Over_games_keeps_the_bar_over_a_game_verdict_TOP_205_265')
C = 'Mantle.Core.Tests/Features/TopBar/TopBarCommandsTests.cs#'
put(['TOP-316'], 'tested:' + C + 'About_pushes_one_line_about_Mantle_and_the_machine_and_hello_drops_it_after_8_s_TOP_316')
put(['TOP-318', 'TOP-319'], 'tested:' + C + 'Settings_winSettings_and_taskmgr_are_dry_runs_in_test_and_open_or_run_live_TOP_317_318_319')
put(['TOP-314'], 'tested:' + C + 'New_window_takes_only_the_known_args_of_an_app_on_disk_and_is_a_dry_run_in_test_TOP_314')
# TOP-317: the Win+I delivered branch (Mantle Settings opening) has no test yet, only the fallback and the dry runs
if len(sys.argv) > 3: put(['TOP-317', 'TOP-175'], 'ported:' + sys.argv[3])   # TOP-175: the strip note is drawn, no UI test triggers one yet
if ui:
    put(['TOP-001', 'TOP-002', 'TOP-216'], 'tested:' + U + 'Top_bar_is_parked_then_shown_at_the_top_never_topmost_every_control_pressed_and_themed')
    put(['TOP-215'], 'tested:' + U + 'A_scale_change_re_places_it_28_DIP_tall_at_150_percent_without_a_restart')
    put(['TOP-218'], 'better:a WS_POPUP with no thick frame takes an exact 28 DIP height, so no window shape is needed (a region still crops it if Windows ever keeps it taller); ' + U + 'A_scale_change_re_places_it_28_DIP_tall_at_150_percent_without_a_restart')
    put(['TOP-281'], 'tested:' + U + 'Win_Alt_T_hides_it_the_top_edge_peeks_it_and_Win_Alt_T_shows_it_again_in_dark')
done = 0
for name in ('shell.md', 'bar.md'):
    path = native + '/docs/parity/' + name
    s = open(path, encoding='utf-8').read()
    out = []
    for line in s.split('\n'):
        m = re.match(r'^\| ((?:TOP|BAR)-\d+) \|', line)
        if m and m.group(1) in rows and line.rstrip('\r').endswith('| todo |'):
            cr = '\r' if line.endswith('\r') else ''
            line = line.rstrip('\r')[:-len('todo |')] + rows[m.group(1)] + ' |' + cr
            done += 1
        out.append(line)
    open(path, 'w', encoding='utf-8', newline='').write('\n'.join(out))
print('marked', done, 'of', len(rows))
