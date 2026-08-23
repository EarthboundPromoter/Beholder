# Nav Revision — the unified input contract (design of record)

**Status: BUILT AND DEPLOYED 2026-08-23** (commits `49c0569` gates 1+2, `bb480d4` gates 3+4, review fixes `HEAD`; Sonnet adversarial pass: 5 MUST-FIX + 1 SHOULD-FIX + 1 NOTE, all closed. Ride-unattested like everything since `08072cf`.) Supersedes the §3/§6 nav grammar of `table-ui-design.md` where they conflict; everything not named here stands. Born from the owner's first playtest of the table build (gates A–G) plus the ride findings of 2026-08-21/22.

## 1. The contract — one meaning per key class, everywhere

- **WASD** = move between containers. World movement outside UI; **section ring stepping** inside UI screens. Nothing else, ever. (The one inert context: a combat selector bar open — see §6.)
- **Arrows** = move focus within the current container. Tile cursor overland; rows in lists; **cells in grids (2D, restored)**; entries/pages in the K overlays; elements in combat selector bars.
- **Z = left click, X = right click**, on wherever focus is. X is never a modality; it renders the game's own tip, which the review cluster then browses.
- **Home/End + PgUp/PgDn (the review cluster)** = document browsing, and only that. No screen-nav meanings.
- The system statement holds with no asterisks: *WASD is the world and the screen's sections; arrows are focus and overlays; Z/X click; the cluster reads documents.*

## 2. Sections: one wrapped ring per screen (replaces the 2D section grammar)

- Each registered screen's sections form **one ordered ring** in composed logical order (the existing §3a per-screen orders are the source). Geometry is demoted to mouse-drive coordinates only.
- **S/D = next section, W/A = previous, wrapping.** No column cross, no per-column memory, no hard stops. Singletons are discoverable by construction — everything is on the ring.
- Landing announcements unchanged (label + census + restored row). Wrap seam audible via the landing prefix; no separate "wrapped" phrase needed at section level.
- RETIRED: the gate-C 2D grammar (A/D column cross, per-column remembered section, geometric flow stops).

## 3. Inventory family: grid nav restored, disclosure re-homed

- **Arrows walk the physical grid in 2D** (the game's own rows/columns, window drive unchanged). The direct-coordinate parks (commit `66a6362`) are the enabling mechanism.
- **Landing speaks identity only**: name, count, equipped-glyph — plus, for equipables, the R14 comparison **verdict word** ("Iron Sword, better.") — the facet-scan use-case carried over at zero grammar cost.
- **Full data = X** (native comparative tip) **+ the review cluster** over the composed document (§4). The R11 full-composition row is retired as a landing and becomes the document.
- Worn slots, filters, scroll clamp, trade/container sections: unchanged except rows→cells per above.

## 4. The examine document (review-cluster browsing, revised)

Browses whatever the panel/tip capture already holds — no new modality, no arrow claim, live right-panel behavior untouched.

- **Composed sections, never parsed, on examine surfaces.** Items: the R11 decision-weight order (identity / stats-with-verdicts / value-weight / prose). Combatant document keeps its ruled sections. Tiles keep theirs. **The overland panel keeps the native parse — it is the case that already works** (game-authored headers/attribute runs).
- **Keys**: Home/End = section walk, PgUp/PgDn = element walk — unchanged assignments, now meaningful everywhere.
- **Wrap**: elements FLOW across section seams, announced composed ("Stats. Soak 0 to 9."); past the document end, wrap with the seam named ("Wrapped. …"). Sections wrap too. Single-section documents have no section walk — Home/End orbit the one group harmlessly; no parser-invented slices anywhere.
- **The Rules tail (nesting, capped)**: keywords from the game's Rules catalog (conditions, abilities, attributes, classes — ToolTipControl's own dictionary) found in the ROOT tip text append ONE trailing section, "Rules"; elements = "Name: full description.", deduped, first-mention order. **Level-1 cap by construction** — keywords inside a keyword's description are heard as names, never expanded (the anti-recursion ruling). With section wrap, Rules is one Home press from the top. The mouse never chases word rects; resolution is a catalog lookup.
- **Live updates**: a changed capture is a NEW document — cursor resets to top. No re-anchoring across refreshes.
- Speech-friendly ordering is the composition itself; wordings provisional as always.

## 5. The K overlays: one creature, both worlds (owner ruling 2026-08-23)

The K instruments are overlays over a live world, not UI screens. The overland POI list is the proven shape; combat harmonizes to it:

- **Up/Down = entries, Left/Right = pages** (categories overland, the four tabs in combat). Combat row nav was already arrows; only the tabs move off WASD.
- **WASD released under the combat latch** — steps the character natively, exactly like walking overland with the POI list open. Deliberate movement while browsing is intent, not an accident.
- **Numbers + Space stay swallowed under the latch** (accidental-turn-spend guard, R12 MUST-FIX class). Z keeps close-silent-fall-through. Separable ruling if the owner ever wants them released.
- The staged combat document's section walk (was Left/Right under the latch) **re-homes to the review cluster** per §4.

## 6. Combat selector bars (owner carve-out 2026-08-23)

Spell/maneuver/item invocation bars are **elements, not sections**: **arrows navigate them while open**, fenced on the existing `GridActive()` flag (covers both invocation paths — Ctrl bar-browse and number keys). Mechanism: arrows drive the same native selection calls the stick feed drives today; **WASD force-false while a bar is open** (receipt-7 patch shape reused). The one context where WASD is inert in combat — a modal selector is not a walking context.

## 7. Settings family (ruling approved 2026-08-23)

- A/D re-homes to the section ring (§2). **The plus/minus chooser moves to Left/Right arrows**, driving the same native sideways calls (revises gate-E ruling R1a; arrows-adjust-within-focus is the arrow contract — the slider is a horizontal control). Funnel-edge refusals, rebind-capture inertness, rendered-header labels: unchanged.

## 8. Retirements

- The 2D section grammar (§2).
- The facet layer, everywhere (Left/Right facet browse, parked facets, InvFacetStep). Disclosure = §3 landings + §4 document. Carve-over: the verdict word on equipable landings (§3).
- K-latch WASD tab swap (§5).
- R11 full-composition rows as landings (§3; the composition survives as the document).

## 9. Riders (same package)

- **Tile validity trails tile data** in the combat line composition (ordering fix, owner note 2026-08-23).

## 10. Named next (NOT this package)

- **Speech-lane reform**: combat dup speech flagged by a second ride (2026-08-23) on top of the #077 measurements — the owner-visible symptom class. Should be the package after this one.

## 11. Parked (owner-parked 2026-08-23)

- **Out-of-combat spell casting is busted** — bug report from playtest; needs its own log-grounded investigation. Nothing in this revision touches it.
- **Out-of-combat vitals affordance** — no fast current-PC vitals check outside combat (sheet excursion only today). Note: "." PC-cycle already speaks vitals on switch; the gap is vitals-without-cycling.
- **Spatial comprehension** (the cave-loop problem) — connects to the parked terrain-noun/tile-art work ("cliff wall north" is most of the answer at street level).

## 12. Build shape

One package, commit-gated, Sonnet adversarial review before deploy (the standing cadence): ring engine change → inventory re-grid + landings → examine document + Rules tail at the capture/composition layer → K harmonization → selector-bar fence → settings re-home → retirements swept (no dead grammar left responding) → the §9 rider. Ride-attestation: everything here joins the deferred combined ride's scope.
