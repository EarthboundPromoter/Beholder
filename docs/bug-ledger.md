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

**Second half found on the visual-style-modal ride (2026-08-16, owner):** the join DID fire but composition had no `UITextSliderControl` branch — its scrollable elements are the rows' minus/plus arrow buttons, so every path fell through to graceful silence. Fixed: arrow-element → owning-row reverse lookup, then the standard "Header: Value, plus/minus" composition + queued description, with the element count trailing. Covers the modal and the settings sliders through the same class. Verify "Name: Value" now speaks on vertical nav in both places before closing B2.

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

**AMENDED (2026-08-16, owner's class-list ride):** the "never load-bearing" claim was WRONG for paged list sheets — the button canvas is a fixed WINDOW (`SkaldObjectList.maxPageSize`, default 20) over the model list, and the edge press's window-slide (`scrollUpByIncrement`, one entry per press) is the only keyboard route to entries beyond the page. The clamp made everything past entry 20 of long lists unreachable (found because the class list's spoken "of 20" is the page size, and the alphabetical roster "ended" at Thief). Fix: the clamp became an **observer** — the native slide always runs; the drain diffs the focused slot's composed line pre/post re-render and speaks the newly revealed entry, or the edge line when nothing changed (true model end). Never silent, never blocking. Open refinement: spoken counts are page-relative ("7 of 20" on a longer roster) — model-total counts are a future composition pass.

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

## B7 — Overland status strip reads in full on every party step (OPEN, found 2026-08-16, owner ride; ruling pending)

**Symptom:** every party step speaks the whole overland status bar — "Time: 02:02 Day: 151 X Pos.: 8 Y Pos.: 16 Overcast Night" — burying everything else. Owner: "entirely too noisy."

**Receipts (LogOutput, 2026-08-16 ride):** consecutive steps f60290/f60299/f60308/f60317 each speak the full strip (X Pos. advancing 7→10); the strip also interleaves into loot/list browsing (f60325, f62196, f62476).

**Root cause:** not a mod routine — the game repaints the strip through `setSecondaryDescription` and position changes every step (clock every few steps), so the per-source diff honestly speaks each new value. The source ALSO carries real overland events ("You see: A Door", "Picked up: …", "THEO is now leading the party.") — source-level silencing is off the table.

**Owner constraint (ruled 2026-08-16):** the bar must never read out in full per step. Fix SHAPE undecided — candidates surveyed with the owner: (a) recognize the strip's fixed shape in overland, silence auto-read, diff only rare-change tail components (day, light/weather phrase) as announcements; (b) a dedicated status-bar reader (on-demand key or review-panel section) with full silence otherwise. Design ruling pending; strip stays in the review panel regardless.

**PARTIAL RULING + FIX (owner, 2026-08-17):** the strip was overrunning the game-opening dialogue on load. Ruled: forced quiet on initial load and whenever dialogue or other UI takes precedence. Landed same day: the strip (recognized by its rendered "Time: " shape on the SecondaryDesc source) speaks only while OverlandState is the settled, popup-free state, and its first value after any state transition settles silently (the B1 shape) — the diff record still updates so a suppressed value can never speak late. Per-step chatter during plain overland walking remains the OPEN half of this row.

## B15 — Attribute-editor flip teleports the cursor; rows land unlabeled (FIX LANDED 2026-08-29, first external report — ninetails16; reporter-verify pending. Numbered out of sequence: the row predates the B8–B14 triage and briefly shared the B8 number; renumbered 2026-09-01)

**Symptom (ninetails16, first bug report, Beholder 0.5.0):** "the pluss/minus speech delay is quite long, I press right arrow to get to pluss and when I want to get back to minus, I have to press the left arrow multiple times." Owner could not reproduce; controller/Steam Input ruled out by the reporter's own unplug test.

**Receipts (reporter's LogOutput, 2026-08-30):** input fully exonerated — every Left/Right press emits exactly one immediate `[Speech:Nav]` Minus./Plus. line; reader=NVDA; ~240fps; zero phantom input in idle stretches. The defect pair: (1) f9022 — a single Left press produces "Minus." AND a same-frame SheetDesc change WILLPOWER→AGILITY: the flip teleported the cursor across entries. (2) The whole stats section contains **no Nav row-landing lines** — row identity arrived only as the queued ~250-char description (class/background lists speak "Bard, 2 of 20"; appearance sliders speak "Hair Style: Style 6, plus, 3 of 9"; stats rows spoke nothing).

**Root cause (decomp-verified):** the flip swaps which arrow column is the scrollable list, then snaps the mouse to `scrollableElements[currentSelectedButton]` — but the canvas index tracks hover only through up/down presses (`UICanvas.increment/decrementCurrentSelectedButton` walk for the hovered element and silently no-op when nothing scrollable is hovered; the index starts at −1 and bounds to 0). A flip pressed before any up/down — canonically right after the intro popup parked the mouse — snaps to row zero regardless of the hovered row. Owner-can't-repro explained: any up/down press first syncs the index, so a tested flow never desyncs.

**Fix (landed 2026-08-29, this session):** (1) resync prefix on both sheet flip methods calls the game's own `setCurrentSelectedButtonIndexToHoveredElement` on the sheet canvas before the flag flips — flips now stay on the hovered row by construction; (2) row-landing join (`Pump.DrainEditorRow`, noted from `updateEntry1/2` postfixes): rows speak "{name}, {side}, {i} of {n}" on landing, the slider-row idiom; a same-frame flip is consumed by the landing line (it carries the side), the bare "Plus."/"Minus." remains for same-row flips. The 2026-08-17 side-only ruling stands for same-row flips; the landing line is the ruled amendment for row changes. Covers CC and level-up (same sheet class).

**Desync-class sweep (owner request 2026-08-29 — other index-vs-hover snap surfaces):** the hazard exists only where a snap reads `currentSelectedButton` while the scrollable-list COMPOSITION can change or hover can be absent. Surveyed: **stats editor** — the one list-swap offender, fixed above. **Settings/appearance sliders** — flip is per-element (`element.controllerScrollSidewaysLeft` flips the current row's own arrow), no list swap, no snap: immune. **Feat tree** — sideways rides its own registry (`currentControllerSelectedFeatTree` / `FeatTreeCollection.controllerScrollIndex`), tree hops deliberately land at the target column's element; no observed defect (reporter log's feat section is clean); watchlist, not patched. **Selector grids** — WP9 layer drives the game's own scroll calls and the mouse snaps per move; Tick suspends under popups: immune. **Inventory/trade tables** — TableCursor owns the surface, native sideways unreachable (ClaimsStick): immune. **Popups** — fresh canvases with an entry snap, popup lifetime too short to desync; native behavior kept. **Up/down after a hover-void** (post-popup, screen entry): increment no-ops and the snap recovers to the canvas's remembered index — native recovery behavior, now always audible via the landing join on the one screen where it teleported; elsewhere the selection joins already announce the landing. General guard considered and rejected: prefixing `setMouseToSelectedOption` itself is a small-method detour (the Mono inline hazard class); the surgical resync at the already-detoured flip choke covers the only proven offender.

## B8 — Character-sheet sections dead mid-session (OPEN, Chaosbringer216 report 2026-08-30, ship/new-game context; NEEDS HIS LOG)

**Symptom:** on the ship (early game), companion's stats then own character's stats unreadable; his diagnosis "it isn't properly moving between sections." Worked earlier in his session, then broke and STAYED broken.

**Static eliminations (2026-08-30):** stale `_state`/`_gui` impossible — `TableCursor.Refresh()` re-resolves both per frame; redirect-canvas staleness revalidated per read (the R13 MUST-FIX); popup lingering ruled out — `PopUpControl` pops handled popups every frame from `MainControl`. Owner-side logs (2026-08-29/30) show CharacterState/AttributeState resolving all sections correctly — but NO in-place PC-swap ("." on a sheet) appears in any owner ride, and that is exactly Chaos's sequence.

**Decider:** his `BepInEx/LogOutput.log` — the [Gate] lines log every registered screen's section map at entry and every landing. One Discord ask. (Bridge repro blocked: /press injects stick/confirm/cancel/numbers only — no W/S/C/"." path; extending the bridge with raw key injection is the fallback.)

**Owner-account revision + log archaeology (2026-08-30):** the owner has himself been stranded on the attributes page ("the mod decides there's no exit from one of the tabbed elements"; PC-swap relevance unknown). FOUND ON RECORD — session-20260822-161016 f211416: on a swapped-to companion's attributes page, S from Skills lands Buttons (skips the right column) and W from Secondary Stats speaks "No section above." — but that log is the R14 BUILD, where the clamped 2D column grammar was the design. The nav revision (2026-08-23) deleted it: the string is gone from source, the current SectionStep is a pure wrapping ring, and the post-revision log (session-20260823-184650) shows Primary→Skills→Secondary landing in sequence with zero refusals. The owner's stranding memories are consistent with pre-revision builds. No current-build stranding exists in any local log; Chaosbringer's current-build report remains the only open claim — his log still decides.

## B9 — Tutorial popup titles unspoken (FIX LANDED 2026-08-30, ride-verify pending)

**Symptom (same report):** "the first part of each tutorial popup isn't reading the text."

**Root cause:** `PopUpUITutorial` carries its TITLE in its own nested class's private `header` field — not one of the three `PopUpUIBase` description slots the popup announce reads — so every tutorial's leading chunk was invisible to the compose and the speak. (Its main description IS a base-slot field and was spoken; whether the title accounts for the whole report or only part of it, the ride tells.)

**Fix:** the announce and the review-buffer compose now read a concrete popup UI type's own `header` field (resolved per type, cached, UITextBlock-typed only); the title leads the announce, body queues behind.

## B10 — "Walking X tiles" travel line intermittently absent (OPEN, same report)

Z on a tile sometimes moves the party with no travel line — "you don't know if you actually moved unless you check the tile manually." The course-join speaks on course set; find the silent path.

## B11 — Looted-container "empty" tag fails out of view (OPEN, same report; his diagnosis)

The ", empty" tag on unlocked emptied containers doesn't speak when the tile is out of view. Suspect: the emptiness read (or the prop/inventory surface it reads) changes under the game's in/out-of-view handling. Compare `IsEmptyContainer`'s reads against the renderer's out-of-view path.

## B12 — Page-relative counts read as model totals (FIX LANDED 2026-08-30, ride-verify pending)

The class picker spoke "10 of 20" — 20 is `SkaldObjectList.maxPageSize`, the button canvas's fixed slot window, not the roster; long rosters likewise spoke page-relative positions. The B4 amendment (2026-08-16) named model-total counts as a future composition pass; the first outside player hit it within the hour. **Fix:** ComposeSelection's trailing counter now resolves the current state's backing `SkaldObjectList` (the "list" field every list-sheet family declares — creation pickers, load/save, load-module, journal family, settings), speaking `scrollIndex + row` of `getCount()`; any resolution failure keeps the page numbers, never silence. Verify: class picker "10 of 10", and a long roster (load-save with 15+ saves, the class list's Thief-last check) mid-scroll.

## B13 — Creation feat screens navigate on WASD, not arrows (OPEN, same report; owner-acknowledged known exception)

The stick-feed element fence (e8c1ba3) was believed to cover the creation family via CharacterBuilderBaseState; the creation feat tree still reads WASD. Either the state is outside the fence's families or a regression. Also the named candidate for registering the creation stats/feats editors into the table contract outright.

## B14 — Dialogue prose counted as its own one-item list (FIX LANDED 2026-08-30, ride-verify pending)

The scene prose crossing announced a sentence counter of its own ("…, 5 of 5.") alongside the correctly-counted options list — read by the player as a phantom second list. **Fix:** the prose sentence line drops its "N of M" outright (owner ruling: the prose is not a list); the topic trailer stays — it carries content, not position. Verify: W from the top dialogue option into the story text — sentence plus topic count only.

## Closed

(none yet)
