# JOB — Tile-art classification (terrain vocabulary for the geography layer)

**Status: FIRED by owner 2026-08-24** (parked 2026-08-21 → launched with three owner-approved amendments, §Amendments below). Runs fully offline (decomp + extracted assets); no game session needed.

## Purpose

Recover the designer-authored identity behind tile art so geography can be spoken richly ("cliff", "stone wall", "surf") without inventing a Z-axis or any fact the art doesn't carry. The art conveys meaning (e.g. the Idra shore cliffs read as height in a game with no Z-level); the terrain data behind the art names that meaning. This job builds the spoken vocabulary: a transcode table from terrain identity to nouns, extended with per-sprite labels where one sheet holds many distinct drawings.

Consumer: the passive awareness / geography layer (in design as of 2026-08-21). The layer ships first speaking the flag taxonomy + terrain-id nouns; this job's table slots in later as pure vocabulary enrichment, zero structural change.

## Ground truth (decomp-verified 2026-08-21)

- Every tile is built per-layer (ground layers + wall layers) from `(terrain id, subImage index)` pairs baked in map save data: `MapSaveDataContainer.TerrainLayer.TerrainLoadData` (MapSaveDataContainer.cs:187) → `MapTile.applyTerrainLayer` (MapTile.cs:331) resolves ids via `GameData.getTerrainById` and stores `imageLayers[] = {path, subImage, sheetWidth, sheetPadding}` (MapTile.cs:56-73, 207-218).
- `TerrainTile` data objects (SKALDProjectData.cs:2401) carry `id`, `title`, `description`, `modelPath` (sprite sheet), flags, sheet geometry — for EVERY terrain, but `applyTerrain` (MapTile.cs:691) only copies name onto the tile for `infoTile` entries. The identity exists in data even where the game never surfaces it.
- **`autoTile` is editor-only — zero references in game code.** SubImage choice was baked at design time; runtime never recomputes. The saved index is authoritative and stable.
- **Light is a tint, never a sprite swap** (MapIllustrator.cs:456-461 `colorInTile` multiply; light props draw glow overlay models, drawLightProps:1131). Confirmed 2026-08-21 at owner request: lighting cannot invalidate a label; classify from raw sheet art. Darkness affects spotting, which existing visibility gates already honor.
- Terrain quick-pass census (2026-08-21, from the R11-extracted `SkaldProject.json`): **149 entries.** Titles (21) are region info-tiles ("Idran Forests", "Perilous Shores") — region identity, not tile nouns (pocketed separately as a possible "where am I" answer). IDs are clean CamelCase designer keys: TER_Cliffs, TER_Shore, TER_Shallows, TER_Surf{8 directions}, TER_Ocean, TER_Mud, TER_Wetlands, TER_Grass, TER_Forest, TER_Vegetation(Tall), TER_Stairs, TER_Doors, TER_Fences, TER_Windows, TER_Wall{Stone,Timbered,Plaster,…}, floors, pillars, arches, roofs, plus Decorations*/Clutter*/Containers*/Structures* sheet families and technical tiles (TER_Blocked*, TER_NPCBlocked, TER_Concealment, TER_Overlay* — mechanics paint, mapped to the flag taxonomy, not nouns).

## Data locations

- Extracted project data: `C:\Users\IATPFNJ624\AppData\Local\Temp\claude\c--Users-IATPFNJ624-SkaldAccessibility\02432f60-4f79-4f7a-806e-f482cbc798b7\scratchpad\SkaldProject.json` (27MB; `"terrainContainers"` at offset ~7,257,218). **Temp-volatile** — if cleaned, re-extract from `resources.assets` (precedented: R11 item census).
- Sprite sheets: textures in `resources.assets` under the game root, named by `modelPath` (e.g. "Arches", "DecorationsCave"). Texture export is a different asset path than the JSON extract — tooling gate applies (see constraints).
- Decomp: `c:\Users\IATPFNJ624\SkaldAccessibility\decompiled_full\`.
- Map terrain layers (for the usage census) are in the same project JSON's map save data.

## Owner parameters (rulings 2026-08-21)

1. **≤3 syllables per label, prefer 1–2.**
2. **Labels must be heard as data and scene-setting simultaneously** — concrete nouns carry both; no mood adjectives.
3. **Label ALL tiles.** Skip only what is provably unused: a `(id, subImage)` pair absent from every shipped map's terrain layers is definitively unplaced — skip with a receipt (the usage census is the proof).
4. Light confirmed a non-factor (see ground truth) — classify from raw sheet art.
5. Deliverable reviewed by owner + a sighted reviewer the owner will enlist.

## Constraints (spec'd with owner 2026-08-21)

1. **Controlled lexicon.** One shared vocabulary across all sheets; the same noun always denotes the same thing. No per-sheet creativity.
2. **Animation discipline.** Sheets carry animation frames (`animation`, `modelFrame`; surf/water animate). All frames of one sprite = one label; detect frame runs, never label frames separately.
3. **Autotile sheets label at sheet level.** SubImage there encodes connectivity shape (which corner/edge piece), not identity — one noun per geography sheet. Per-subimage labels only for manually-placed sheets (Decorations*, Clutter*, Containers*, Structures*, Doors, Stairs, Windows, …).
4. **Usage census FIRST.** Enumerate used `(id, subImage)` pairs from all shipped maps before labeling. Bounds the work; produces the skip proofs; yields real map coordinates for context viewing.
5. **Context viewing for ambiguous sprites.** 16px art viewed upscaled (nearest-neighbor). Anything ambiguous in isolation gets composited in situ at a real placement from the usage census.
6. **Uncertainty is marked, never smoothed over.** Low-confidence labels flagged so reviewer time concentrates there.
7. **Deliverable: one local HTML review page.** Grouped by sheet: upscaled sprite image, proposed label, `(id, subImage)`, usage count, confidence flag. Label doubles as the image alt text — the same page is the sighted infographic AND NVDA-navigable. No external hosting.
8. **Tooling gate at job start.** Verify the texture-export tool is present before starting; stop if missing (standing owner rule — never improvise tooling).

## Amendments (owner-approved 2026-08-24, at launch)

9. **Interaction-discrepancy census rides the usage census.** While walking every shipped map's data, also harvest every entity carrying interaction wiring (info tiles, script triggers, props with interaction data) and compute what the mod's current classification path would speak for it. Anything answering with a bare flag word ("blocked", "floor") while the game treats it as an interactable goes into a **discrepancy table** — its own section on the review page. Motivating case: the refugee-camp footprints spoke as "blocked" (impassable flag fall-through; the POI categories don't recognize the class). This finds the rest of that class in one sweep.
10. **Non-descriptive-interactives findings column — record only, no remediation.** The same harvest records which interactables lack a designer title/description. Deciding what they *should* say is speech-side design with its own rulings — explicitly out of scope for this pass.
11. **Layer-role tags on every label** (ground / wall / decoration / overlay). Tiles compose from stacked layers; the eventual precedence rule for which noun wins is consumer-side design, not decided here — the tag is cheap metadata so any later rule works without a re-pass.

## Execution shape

Fable agent (owner-specified), background-capable. Sequence: tooling gate → (re-)extract if needed → usage census + interaction-discrepancy census (amendment 9, same walk) → sheet-level table (geography ids → nouns) → per-subimage labeling of manually-placed sheets with context viewing → HTML review page (labels + layer-role tags + discrepancy table + findings column) → owner + sighted review → only then does anything enter speech.

Note for the classifier: this table is the first place in the design where the agent's reading of the art becomes the source of truth rather than a designer string. It is transcription of what is drawn — interpretive in a way the id table is not. Nothing from it speaks until the review pass.
