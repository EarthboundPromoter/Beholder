# Overland world-model survey — SKALD decompiled source

Date: 2026-08-16. Basis: full-decomp sweep (four parallel surveys + spine reads) of `decompiled_full\` (414 types, game unpatched since 2026-03-07). Purpose: the comprehensive model the overland-navigation proposal designs against. Every member name verbatim from source; file:line cited where load-bearing. Companion to the coverage annex (build-plan §6) and `reference_controller_coverage.md`.

## 1. Coordinate and rendering model

- Virtual screen **480 × 270**, y-up (`SkaldIO.getMousePosition` SkaldIO.cs:645; `getGlobalMousePosition` :659). Tiles are **16 × 16 px** (`SkaldWorldObject.WorldPosition.setTilePosition`: `pixelX = tileX * 16`).
- World coordinates: **y increases NORTH** (`DataControl.moveOverland` maps `+y` → facing 0 = north; `Map.testExitByEdge` maps `y >= height` → `northernEdgeMapId`). Screen rows draw top-down with decreasing y.
- Viewport: **23 × 17 tiles** + 2-tile rim (`MapIllustrator.ScreenDimensions`, MapIllustrator.cs:613-671: width 23, height 17, half 11/8, withRim 25/19). Viewport position: `MapTileGrid.getViewportX()/getViewportY()`; party position `MapTileGrid.getXPos()/getYPos()` (== leader tile; `Map.getXPos()/getYPos()` mirror).
- Screen→tile conversion (`MapTileGrid.getTileAtRelativeLocalPos`, MapTileGrid.cs:578), from a 0..1 map-canvas position: `tileX = (viewportX - 12) + floor(25 * pos.x)`, `tileY = (viewportY - 9) + floor(19 * pos.y)`. The right ~5% of the canvas is dead (`pos.x > 0.95` → null); exact-0 components → null. The map canvas rect comes from `MapIllustrator.applyMapOverlay` → `UIMap.updateTexture(12 - scrollX, -scrollY, …)`, 400 × 304 px.
- Smooth scroll: `MapIllustrator.ScrollControl.scrollX/scrollY` (0/16/32), `SCROLL_SPEED = 2`. Movement pacing rides it: overland steps are gated on `isScrollReady()`.
- Hover-highlight is **one frame stale** by construction: `OverlandState.update()` bakes the map (`setGUIData`, :79) before reading the mouse (`setMouseInput`, :85).

## 2. The tile model (MapTile : SkaldBaseObject)

One class carries the whole semantic surface. Core private fields (MapTile.cs:245-305): `spotted, visited, spottedOnce` (visibility), `impassable, concealment, overlay, NPCBlocked, noVisibility, water, voidTile` (terrain flags), `encounterChance + randomEncounterMapIdList`, `travelTime` (dead — never consulted), `inventory` (ground items; **lazily allocated by `getInventory()` — never call speculatively**), `vehicle` (public field), `party` (living occupant), `deadParty` (corpses), `prop`, `light`, `lightLevelControl`, `worldPosition`.

**Identity:** terrain is baked at construction (`applyTerrain`, :691): only designer-labelled **info tiles** write `setId/setName/setDescription` — plain ground/wall tiles have `getName() == ""`. `getDescription()` (:998) prefers the prop's description.

**Occupancy accessors:** `getProp()` :902, `getPropOrGuestProp()` :438 (**always use this** — multi-tile props register guest containers on covered tiles; `getPropRootTile()` :784), `getCharacter()` :585 (**side effects**: de-spawns marooned NPCs), `getLiveCharacter()` :638 (safe read), `getParty()` :841 (**side effects**: runs `updateDeadPartyStatus` + `getCharacter`), `getDeadParty()` :686, `hasVehicle()` :943, `getInventory()` :907.

**Walkability:** `isPassable()` :1230 (false on void, tile-impassable, or prop `impassable()`), `isTileOpen()` :605 (no live character), `isTileOpenAndPassable()` :1256, `isTilePassableToNPC(Party)` :1030 (adds NPCBlocked + water + occupancy), `isWater()` :812, `wading()` :1044, `isVoidTile()` :569.

**Rendered inspect text (the game's own composition, spoiler-gated):**
- `getInspectDescription()` :1279 — **returns `""` unless `isSpotted()`**. Priority: party (`Party.getInspectDescription()` → full character stat block, itself gated "It's too dark to make out anything!" when the character isn't spotted) → vehicle name → prop `getInspectDescription()` → tile `getName()`. Wraps as `"You see: X."`; appends Position (`x/y`), Light Lvl., Stealth lines only when `GlobalSettings.getGamePlaySettings().showStealthInfo()`.
- `getShortInspectDescription()` :1265 — `"You see: " + prop.getName()` (used on first bump).
- `getVerb()` :1089 — the prop's verb, `""` on empty tiles.

**Stealth/light:** `getStealthBonus()` :574 = `-clamp(round(lightLevel*7)-1, 0, 7) + (concealment ? +3 : 0)`; `isIlluminated()` :1007 (`lightLevel > 0.5`), `getLightLevel()`, `getLightRadius()` :966 (max of prop light, tile light, party light).

**Triggers (relayed to the prop):** `processTryEnterTrigger` :1158 (fires on EVERY attempted step, passable or not), `processVerbTrigger` :1198 (→ `prop.interactWithProp()`), `setVisited()` :520 (first visit fires firstTime+enter, later visits enter only), `processLeaveTrigger`, `processCombatLaunchTrigger`, `testRandomEncounter()` :770.

## 3. The prop family (18 classes)

Chain: `Prop : SkaldPhysicalObject : SkaldWorldObject : SkaldInstanceObject : SkaldBaseObject`. State in `protected PropSaveData dynamicData` (`verb, activated, locked, hidden, impassable, noVisiblity` [sic], `light, hitPoints, trap, faction, dug`); prefer the public accessors.

| Class | Chain (below Prop) | Verb | Notes |
|---|---|---|---|
| PropDoor | Lockable→Activatable | "Open"/"Close" by `isActive()` | **active = CLOSED** = impassable + opaque; deactivate = open (runs `tryLock` first) |
| PropCont | Lockable→Activatable | data verb | `isContainer()` true; loot lives on the TILE inventory; open runs lock check |
| PropWarp | Lockable→Activatable | data verb, else Ascend/Descend/Exit/Enter/Use | `getNestedMapId()` = destination; interact calls ascend/descend/mountMap |
| PropLightSource | Activatable | "Douse"/"Light" by `isActive()` | active = LIT; `getLight()` 0 when unlit |
| PropBed | Activatable | "Rest" | interact → `makeCampWithBed()` |
| PropWorkBench | Activatable | data verb | inspect suffix `[Workbench]`; interact → `mountWorkBench` (no hidden guard) |
| PropInspectable | Activatable | data verb | interact → `mountSimpleScene(getName(), getDescription())` — full-screen text |
| PropPickup | Activatable | data verb | pickup → bark `inventory.getPickedUpBark()`, prop removed |
| PropTest | Activatable | data verb | skill-check popup; `getOptionList()` = literal button labels |
| PropTrap / PropInteractable | Activatable | data | stubs — behavior all in Activatable |
| PropTrigger / PropDecorative / PropBeacon / PropSpawner | direct Prop children | **""** (never seeded) | trigger fires on movement; decorative inert; beacon invisible marker; spawner driven externally |

**Base accessors:** `isActive()` (universal open/lit/on flag), `isLocked()` (Lockable), `isHidden()` (hidden props STILL RENDER — only highlight + interaction respect it; revealed by an awareness contest inside `MapTile.setLightLevel`), `impassable()` (false once `shouldBeRemovedFromGame()`), `noVisibility()`, `isContainer()`, `getFaction()`. Inspect text: `Prop.getInspectDescription()` = name + `[Owned: faction]` when illegal; `PropLockable` appends `[Locked]`.

**Registry:** `GameData.getPropsByMap(mapId)` → all live props on a map (elements `SkaldWorldObject`, cast to `Prop`); `getFirstPropByIdOnMap`. Each subclass shadows `getRawData()` with `new` — reflection must target the declaring type.

## 4. Occupants and ground contents

- **A tile's occupant is a `Party`** (even solo NPCs — one Character inside). The player party is the same class: `Map.playerParty` (public field); the whole party occupies ONE tile overland (deployed only in combat).
- Hostility: `Character.isHostile()` (faction-derived), `Party.isHostile()` (current character), `Party.isPC()` (any member). Names: `Character.getName()`, `getClassName()`, `getLevel()`; `Party.getName()`.
- Awareness: `Character/Party.isSpotted()`, `isAlert()`, `isHidden()` + `getHiddenDegree()`; `Party.getMoveMode()` (Roam/Home/Flee/Patrol…).
- Corpses: `deadParty`; rendered as a separate layer.
- Ground items: tile `inventory`; **rendered as an icon when non-empty and passable**; surfaced textually only when standing on the tile (`DataControl.getTerrainDesc()` → "Items in tile:" + `printCountList()`; `getTileVerb()` → "GET ITEMS"). Never in inspect.
- Vehicles: on the tile (`MapTile.vehicle`) or held by the party (`Party.hasVehicle()`); boarding/landfall/ship-interior transitions in `setOverland` :2814-2841 with rendered lines ("You board a ship!", "You make landfall!", "Water blocks your path!").

## 5. The visibility contract (what may be spoken)

Rendered states, from `MapIllustrator`:
- **Tile fogged** (`!isSpotted()`): opaque fog stamp. Nothing about the tile is on screen. `getInspectDescription()` already returns `""`. **Nothing may be spoken.**
- **Tile spotted**: terrain + props render. Mouse-shadows require spotted. Prop highlight requires a spotted covered tile.
- **NPC on a spotted tile**: full sprite iff tile `isIlluminated()` OR party `isSpotted()` OR PC; else, if the tile isn't concealment, an **"unseen icon"** (a something-is-here marker with no identity); else nothing. Character inspect text self-gates: "It's too dark to make out anything!" when unspotted.
- **`isSpottedOnce()` (explored-but-fogged) has NO rendered presentation** — the fog stamp is identical to never-seen. (The only consumer, MiniMap, is dead code — never instantiated.) Speaking remembered geometry is therefore *beyond* what the screen shows — a design decision to make explicitly, not silently.
- Fog regrowth: outdoor/`fogRegrows` maps re-fog on every step (`clearFogAndLight`); indoor non-regrow maps keep `spottedOnce` tiles spotted. Viewshed: radius `viewDistance = 5.99`, LOS via `MapTileGrid.testLineOfSight` (blocked by `!getSeeThrough()`). Lightmap: ambient (day/night via Calendar) + emitters, light LOS effectively never blocks (inert-flag decompile artifact, testLineOfSightLight :1162).
- Stealth surfaces: current-PC "%N Stealth" drawn while hidden; `getTerrainDesc()` prints SNEAKING/Status/Stealth block while hidden; inspect prints Position/Light/Stealth when the game setting `showStealthInfo()` is on.

## 6. The movement contract

Chain: `OverlandState.move(x,y)` → `DataControl.moveOverland` (sets facing; **cardinal only, no diagonals anywhere**; no run modifier) → `setOverland(targetX, targetY, bumpTile)` (DataControl.cs:2693) — the authoritative rules engine, validation order:

1. Scroll not ready → silently refused (pacing gate, 8 physics ticks/step).
2. Dead PC → bark "Unconscious" + description.
3. `(-1,-1)` = pass-time-in-place (used by wait/hide/verbTrigger).
4. Invalid target → edge exit (`testExitByEdge` → `mountMapEdgePrompt`) or **"You can't go that way!"**
5. `processTryEnterTrigger()` fires on EVERY attempt.
6. **Blocked tile → the two-press bump model:** first bump sets targetTile + appends `getShortInspectDescription()` ("You see: X"); second bump plays the bump sound, spends time, and — if interactive — fires `verbTrigger()`. Nested-map tiles mount the map instead. NPCs get a free tick on blocked bumps.
7. Occupied tile → hostile: `launchCombat()`; friendly: `mountInteractParty` (contact triggers, dialogue).
8. Water → board vehicle / "Water blocks your path!" / "You encounter another ship!".
9. Encumbered → popup + refuse.
10. **Commit:** `setPosition` (party moves, viewport scrolls, NPCs move, viewshed/lightmap update), `setVisited()` (enter triggers), time (+15 min overland / +6 s dungeon — blocked bumps cost the same), move sound (water/vegetation/stealth/normal — an audible terrain class), `setDescription(getTerrainDesc())`, then `triggerEvent()` (dynamic events, then random encounters — successful steps only).

**Auto-move (click-to-path):** left-click → `findPathToMouseTile` (A*, Manhattan, 4-way, whole map — including fogged tiles; occupied tiles block except the destination, which is force-passable — that's click-to-attack/interact). One node per eligible tick; suspended while any key is held; direction keys/left-click/right-click all CANCEL; newly-appearing enemies cancel (rising edge only). Edge-exit click buffers the destination map and mounts it on arrival.

**Input:** overland consumes held reads `getDownUp/Down/Left/RightKey` (the WP9 suppression set), `anyKeyDown()` gates auto-move, Space = `getPressedMainInteractKey` → contextual interact, right-click = inspect tooltip + course cancel, left-click = path.

## 7. Interaction and inspection

- **The verb has no visual surface overland.** `DataControl.getTileVerb()` (the "OPEN"/"GET ITEMS" formatter) has zero compile-time call sites — reachable only from script strings. `setContextualButton` is never called in OverlandState. Space fires `verbTrigger()` blind against the **targetTile** (last bumped), not the mouse tile.
- `verbTrigger()` (DataControl.cs:3123): containers/ground items → loot popup (also falls through — missing else); non-interactive → `wait()`; else `processVerbTrigger()` + pass time.
- **Right-click inspect** is the only textual inspection: `Map.getMouseTileDescriptionToolTip()` → `mouseTile.getInspectDescription()` → `ToolTipPrinter.setToolTipWithRules` — **flows through our existing Tooltip hook** (content join + review-layer panel).
- Movement/interaction text sinks: `DataControl.setDescription/appendDesc` → read back by `OverlandState.setSecondaryDescription()` → `guiControl.setSecondaryDescription` — **flows through our existing SecondaryDesc hook**. This is how "You see: X", "You can't go that way!", terrain/items text, and vehicle lines already reach speech. Barks (`addVocalBark`) flow through the bark join. Map name → `setPrimaryHeader` every frame (PrimaryHeader hook).

## 8. Transitions and map identity

- Map edges: `northern/eastern/western/southernEdgeMapId` + `containerMapId`; walking off an edge (invalid target) or click-buffered edge exit → `mountMapEdgePrompt` (despite the name: no prompt, direct mount).
- Warps: `PropWarp` → ascend/descend/`mountMap`; `mapAboveId/mapBelowId` layers; nested maps on tiles (`getNestedMapId()`) mount on bump, with an optional native enter-prompt scene (`map.enterPrompt` → SceneNode with the map's name/description and Enter/Leave options).
- Map identity: `Map.getName()` (can be ""), flags `overland, wilderness, indoors, city, dynamicEnc, canFightHere`; `getTravelTimeInSeconds()` = 900 overland / 6 else. Weather: `printWeatherDescription()`.

## 9. NPC awareness and combat range

`Map.nearByEnemies / nearByFriendlies / potentiallyAlertEnemies` (public list fields) rebuilt ONLY inside `getAllVisibleNPCs` (on player move / combat entry — **stale between moves**). Membership: 4-way flood fill from the party over passable non-water tiles bounded by the viewport (`getAccessibleTilesFromParty`), tile must be `isSpottedOnce()`; hostile → enemies. **`nearByEnemies` ignores line of sight** — button-enabled "nearby" ≠ visible. LOS filters only `potentiallyAlertEnemies`, whose `isAlert()` members force `launchCombat` (the automatic gate checked before movement every update). Manual attack (button 0) requires `areOpponentsNearBy()` else barks "I don't see any enemies!".

## 10. What already speaks through existing joins (no new work)

Via SecondaryDesc: step terrain descriptions, "You see: X" first-bumps, refusals ("You can't go that way!", water, encumbrance), vehicle lines, wait/hide lines, items-in-tile lists, stealth block while hidden. Via Tooltip (+ review panel): right-click tile inspect, character stat blocks. Via Bark: "Unconscious", "Locked!", "A trap!", "Unlocked", pickup barks, "I spotted something!" (hidden-prop reveal), "I don't see any enemies!". Via PrimaryHeader: map name. Via state clock: combat launch. Popups: locks, encumbrance, skill-check props, loot.

## 11. Interface implications (the design surface for the proposal)

1. **The game's own examine machinery is drivable.** `MapTileGrid` has mouse/examine/target tile slots; `setExamineTile(x,y)` exists and highlights; `getInspectDescription()` is the composed, spoiler-gated readout. A mod tile cursor can be two ints + the game's own inspect call — no mirror, no cached text (the WP10 lazy-cursor idiom).
2. **The visibility contract is enforceable with two native reads:** speak a tile iff `isSpotted()`; identify an occupant iff illuminated/spotted (the unseen-icon state should speak as "something unseen" — that IS rendered). `isSpottedOnce()` disclosure is an explicit owner ruling (the screen shows nothing).
3. **Bump-first is native.** The existing two-press bump model is already an accessible idiom (collide → hear → confirm) flowing through hooked sinks. Enrichment beats replacement.
4. **The silent-step gap:** plain tiles with no info-tile name produce no per-step text; position/facing/edge awareness has no native surface. This is where mod-side step composition (coords, terrain class from flags/move-sound taxonomy, adjacent-notables) would add over the native sinks.
5. **The verb gap is game-wide:** even sighted players never see the verb overland. Speaking `targetTile.getVerb()` on bump (and cursor tile verb on demand) is pure addition.
6. **Scanning primitives are ready:** `getTile(x,y)` + validity + the flag set supports RW3-style sweeps (nearest prop/NPC/exit by class), all filterable by the visibility contract; `GameData.getPropsByMap` enumerates candidates without touching tiles (no side-effecting getters).
7. **Side-effect discipline:** never call `getParty()`/`getCharacter()`/`getInventory()`/`setSpotted()` from readers — use `getLiveCharacter()`, `getPropOrGuestProp()`, flag reads.
8. **Movement-key modality:** the WP9 grid suppression set already owns the overland held-movement accessors — a cursor mode wanting WASD reuses that exact choke pattern.
9. **Path/auto-move awareness:** course lives in `NavigationCourse.course` (List of Points, shared instance across members); destination, length, and cancellation are all readable for "walking, N tiles to X / interrupted" speech.
10. **MiniMap's dead taxonomy** (point-of-interest > water > blocked > concealment > floor) is a ready-made, game-authored priority order for summarizing a tile in one word.
