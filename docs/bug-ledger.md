# Bug ledger — ride findings awaiting a build pass

Lineage idiom: each row = finding, receipts, root-cause hypothesis, fix shape. Owner rules disposition; rows close with the commit that fixes them.

## B1 — Settings entry reads every row's description (OPEN, found 2026-08-16, owner ride)

**Symptom:** entering Settings, all settings read out "straight off the rip" — every row's full description queues and plays serially.

**Receipts (LogOutput, 2026-08-16 ride):** SheetDesc notes arrive one row per frame across the screen build — f21869 (Enter Map Auto Save), f21871 (Rest Auto Save), f21873, f21875, f21877, f21879, f21881 (Tutorials) — then the queue plays all seven back (f21885–f21945).

**Root cause:** the WP5 latest-per-source collapse works *within* a frame, but the game's screen build walks `setSheetDescription` across the rows **one frame at a time** — seven consecutive frames, seven successive settled values, and the per-source diff faithfully speaks each. Not a regression of the old behavior (the old dictionary had the same hole); the burst just isn't collapsed by a per-frame latest-wins.

**Fix shape (next build pass):** tie content sources to the state clock — on a state transition, hold volatile content sources (SheetDesc class) in a settle window: speak only the value that survives N frames unchanged (the OAG "settled-value speech" rule applied to content), or first-note-per-state-silent for description sources so screen entry reads header + buttons only and descriptions speak on navigation.

## B2 — No on/off state for settings toggles (OPEN, found 2026-08-16, owner ride)

**Symptom:** arrowing through settings rows gives no indication of a toggle's Enabled/Disabled state.

**Receipts:** the ride log shows **zero `[Speech:Nav]` lines inside Settings** — row navigation spoke only `SheetDesc` (the game repaints the description per hover). The "Name: Value" describer (`SliderArrowPatch.ReadSliderRow` via the selection join) never fired.

**Root cause hypothesis:** in Settings, Up/Down moves the virtual mouse via the mod's `setMouseToClosestOption*` re-implementations, and on this screen that path evidently never writes `currentSelectedButton` for the slider control — so the selection join has nothing to note. The pre-WP6 "Tactical Grid: Enabled" lines came from the deleted hover watcher, which read `hoverButton` directly. State information was lost in the WP6 trade.

**Fix shape (next build pass):** give sliders a real join instead of resurrecting the watcher — candidates: (a) note-only postfix on the slider control's hover-assignment site (`UITextSliderControl.update` sets `hoverButton`, UITextSliderControl.cs:306-312 — hook the assignment, not a per-frame compare), or (b) make the mod's setMouseToClosest re-implementations also write the selection index for slider controls, restoring join coverage. Either way the row then speaks "Name: Value" + queued description through the normal composition. Verify toggles' value text lives in `currentValueTextBlock` (pre-WP6 receipts say yes: "Tactical Grid: Enabled").

**WP7 note (2026-08-16):** expected to dissolve under forced controller mode — the blamed re-implementations are deleted; the native `setMouseToClosestOptionBelow` increments the settings sliderControl list, which writes `currentSelectedButton` and fires the join. Verify on the WP7 ride before closing.

## B3 — Contradictory slider value announcements on adjust (CLOSED BY DELETION 2026-08-16, ride-verify pending)

**Symptom:** adjusting music volume, spoken values contradict the direction of adjustment (press one way, hear a value from the other direction).

**Receipts:** f25880–f26092: "100%, 100%, 80%, 60%, 40%, 60%, 80%, 100%" — the spoken sequence runs one press BEHIND the actual value; at every direction change the announced value moves the wrong way.

**Root cause:** an off-by-one-frame read. `UITextSliderControl.update` redraws `currentValueTextBlock` from the backing value, THEN our postfix runs and mutates the backing — so the rendered block still holds the pre-press value when the Pump drains at the end of that same frame. The redraw lands next frame; every announcement is one press stale.

**Fix shape (next build pass):** defer the slider-value read one frame — the note carries "speak on the NEXT drain," after the game's own redraw has landed (settled-value speech, one frame later). Alternative rejected: reading the backing setting directly would be fresh but violates render-first — the deferred rendered read is both correct and honest.

**CLOSED (2026-08-16, native-slider commit):** the arrow-key adjust path that carried this bug is deleted (owner ruling: follow the game's controller idiom — A/D flips the minus/plus arrow, Z clicks it). The native click path mutates and re-renders the value inside the same row update, BEFORE the control-level postfix notes it — the drain reads a fresh value the same frame, so the stale read is structurally impossible. Verify by ear on the next slider ride.

## B4 — Dead presses at list edges: bar scrolls, focus doesn't move, no speech (FIX LANDED WP7 2026-08-16, ride-verify pending)

**Symptom:** in Audio settings, Up/Down sometimes visibly moves the scroll bar but doesn't advance to the next option and produces no speech.

**Facts:** Audio has exactly TWO rows (Music Volume, Sound FX Volume — GlobalSettings.cs:588/624; that's the whole AudioSettings list). So nearly every press is an edge press.

**Root cause:** the game's own edge design, faithfully replicated by the mod's gate-removal re-implementations (GUIControl.cs:1806-1821): `setMouseToClosestOptionBelow` advances selection only while `canControllerScrollUp()` (index < count-1); **at the list edge it calls `sheetComplex.scrollLeftBarUp()` instead** — scrolling the pane, moving no selection, firing no join, speaking nothing. Not a focus/scroll desync: within the list, focus-follow works and the game snaps the virtual mouse per move; the dead zone exists only at edges, and with a 2-row list the edges are everywhere.

**Fix shape (next build pass):** never a silent press — when the press routes to the scroll branch at a true list edge, speak the edge ("Bottom of list." / "Top of list."), matching game logic (clamp, no wrap — standing owner rule). For genuinely scrollable long content the edge press scrolls the pane; a pane-scroll cue ("scrolled") is a phrasing-ruling row for the keymap session.

**RULING + FIX (keymap session, 2026-08-16):** owner ruled **clamp the focus** — decomp proved the scroll branch is never load-bearing for reaching options (`canControllerScrollUp/Down` tests the FULL element list, UICanvas.cs:71-79; scroll-only screens like GUIControlCredits override the methods without calling base and are untouched). Landed in WP7: edge-clamp prefix on `setMouseToClosestOptionAbove/Below` suppresses the scroll branch at a true edge and speaks "Top of list." / "Bottom of list." via the Pump. The pane-scroll cue question dissolved — nothing scrolls. Cosmetic loss accepted: no visual nudge revealing overflow content trailing a list (one-screen exception row if a ride ever surfaces one).

## B5 — Lateral selectors unannounced as such: pre-new-game modal + character creation (OPEN, found 2026-08-16, owner ride; owner diagnosed)

**Symptom:** the pre-new-game modal (visual style) and parts of character creation felt "weird" to navigate.

**Owner's diagnosis (2026-08-16):** not broken focus — the visual style picker is a **lateral selector** (Left/Right cycles the value in place), and its buttons are *another* set of lateral selectors. The game's UI idiom is idiosyncratic here, and the mod's speech conveys the focused label but nothing about the control's *shape* — a listener can't tell a lateral selector from a button, so Left/Right versus Up/Down versus Enter expectations break.

**Fix shape (next build pass):** role phrasing at composition time — when the focused control is a lateral-selector class, the utterance carries the idiom the way the CS lineage transcodes toggles ("X, on"): e.g. "Visual style: CRT, 2 of 3" with lateral movement speaking the new value in place. Requires a small decomp pass to identify the selector control classes in the modal and CC screens (candidates around PopUpVisualStyle and the CharacterBuilder screens), then a phrasing ruling from the owner (self-contextualizing, no tutorialization — P7).

**DECOMP + RULING (keymap session, 2026-08-16):** the lateral selector is one class family everywhere — `UITextSliderControl`/`UITextSliderButton` builds the visual-style modal's four rows (PopUpVisualStyle.cs:83-91), the Settings rows, the character-creation appearance screen (GUIControlApperanceEditorSheet), and the camping-food quantity selector. Role phrasing keys off the family once (universal hooks). Owner ruled the **count-only form**: focus = "Visual style: CRT, 2 of 3." (the value's position among its choices — the count replaces the list-position count so counters never stack); lateral adjust = new value in place with count, read one frame deferred (B3's fix, same stale-render trap). PROVISIONAL: owner is not yet sure the lateral idiom is intuitive from the count alone — revisit after more UI rides. Implementation = drain-side pass (with B1/B3).

## B6 — Two cursors on every list sheet: funnel focus vs SkaldObjectList current object (FIX LANDED 2026-08-16, ride-verify pending)

**Symptom (owner ride, rebind screen):** arming reassignment committed to the wrong binding — Enter acted on a row other than the focused one. Owner diagnosis confirmed: arrowing moves the funnel focus; the row that actions actually target is the list's *current object*, which only a **click** sets. The game shows the divergence visually (current object rendered yellow, `SkaldObjectList.getScrolledStringList`); the mod voiced only focus.

**Mechanism:** every list-sheet state routes clicks through `getListButtonPressIndex` → `getObjectByPageIndex` → `getObjectByIndex`, which sets `currentObject` — a read that mutates. Actions (`updateKey`, class choice, load, craft) act on `getCurrentObject()`, never on funnel focus.

**Landmine survey (same pattern, per-surface ride checklist):** keybindings (hit), load/save menus (LoadMenuState/SaveMenuState — loading acts on clicked row!), load-module, character-creation class + background pickers (choice commits the clicked row), crafting recipes, the generic ListSheetBaseState family (journal/faction-type sheets), inventory/combat consumable cycling (`setNextObject`), spell lists.

**Fix landed:** (a) browse composition transcodes the game's yellow current-row marker — the focused row that is also the selected row speaks "…, selected, N of M" (transcode markup, don't strip); (b) new join on `setCurrentObject`/`getObjectByIndex` — the drain diffs the list's current object and speaks "Selected: <row>." on actual change, so the click-to-select step is audible. First observation of a list settles silently.

## Closed

(none yet)
