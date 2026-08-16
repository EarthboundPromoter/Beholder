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

## Closed

(none yet)
