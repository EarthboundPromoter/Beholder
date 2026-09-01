# Beholder

**Version 0.5.5 — beta**

A screen-reader mod for **SKALD: Against the Black Priory**: it speaks the
game's story, exploration, character sheets, and turn-based combat through NVDA
or JAWS (via [Tolk](https://github.com/dkager/tolk)), with a Windows SAPI voice
as fallback, so the game can be played without sight, entirely by keyboard.
Sibling project to
[Words of Power](https://github.com/EarthboundPromoter/Words-of-Power-II)
(Rift Wizard 3),
[Citizen Speaker](https://github.com/EarthboundPromoter/Citizen-Speaker), and
[Sleeptalker](https://github.com/EarthboundPromoter/Sleeptalker)
(Citizen Sleeper 1 and 2).

Beta — the whole game surface is built and the core loop has been played live,
but a full campaign hasn't been completed with the mod yet. See "What's tested
and what isn't" below. Report problems on Discord or in the Audiogames forum
thread.

## About SKALD: Against the Black Priory

From the [Steam store page](https://store.steampowered.com/app/1069160/SKALD_Against_the_Black_Priory/):

> You drag yourself from the black tides, across the corpses of drowned men,
> and onto the unwelcoming, craggy shoreline. Gulls cry overhead and the stink
> of seaweed fills your nose. By some miracle you have made it to Idra.
>
> It will take all your skill to survive and unravel the eldritch mysteries of
> the Black Priory.
>
> SKALD: Against the Black Priory is inspired by roleplaying games of
> yesteryear, combining modern design and compelling storytelling with
> authentic 8-bit looks and charms — a dark fantasy world full of deadly
> creatures, tragic heroes, and lovecraftian horror, rich in exploration and
> tactical, turn-based combat.

## Requirements

- **SKALD: Against the Black Priory** (Steam, Windows).
- A screen reader — **NVDA** or **JAWS** (or any other reader supported by
  [Tolk](https://github.com/dkager/tolk)). With no reader running, the mod
  speaks through a Windows SAPI voice instead.
- Everything else — the [BepInEx 5](https://github.com/BepInEx/BepInEx) mod
  loader and the speech DLLs — is bundled in the release zip.
- Start your screen reader *before* launching the game.

## Installing

1. Download the latest zip from the
   [Releases page](https://github.com/EarthboundPromoter/Beholder/releases/latest).
2. Extract it into the SKALD game folder (the one containing
   `SKALD Against the Black Priory.exe`), merging folders if asked.
3. Launch the game. You'll hear "Beholder 0.5.5 loaded." To update, extract the
   newer zip the same way.

One thing to know: on launch the mod **silently rebinds two of the game's
keys** — Next Character to **period** and Inventory to **comma** — because
their defaults (Q and E) collide with the mod's shoulder-button keys, and it
keeps them there if a reset or game update undoes it. `Input.AutoRebind` in the
config turns this off if you'd rather manage those two bindings yourself.

## How the mod works

SKALD's controller mode has a real focus model; the mod switches the game into
it and drives it from the keyboard — it feeds the game's own navigation,
follows the focus, and speaks what it lands on. Dialogue reads automatically
with numbered choices, popups speak, and menus talk as you move. Speech is
interruptible: slash stops it, brackets walk the history.

On any UI screen, W and S (and A and D) move between the screen's
**sections** — each landing names the section and counts what's in it ("Party
Inventory, 12 items, 340 gold") — and the arrows move within the current
section: rows in lists, cells in grids. Item rows are composed in full — name,
stats with a comparison verdict against what you have equipped, value and
weight — and X reads the item's description. The review cluster (Home, End,
PgUp, PgDn) walks the full examine document for the focused thing, section by
section, with definitions of the rule keywords it mentions appended at the end.

**F1 opens a key table for whatever you're looking at** — every screen,
overlay, and popup has its own: Up and Down walk the key rows, Left and Right
narrow to just the keys or just the functions, F1 closes. Screen-specific
behavior is noted on the key it belongs to.

On the **overland map**, WASD walks the party while the arrows browse the
tiles around it — each tile speaks what's on it, the terrain under it by name
(shore, cobbles, cliffs...), its map coordinates, and whether you can actually
walk there ("out of reach" when you can't). V speaks the walking course to the
browsed tile — "North 4, east 2, arrive." H, N, B, O, and M scan
hostiles, neutrals, loot, objects, and exits; K opens a persistent list of the
map's points of interest; and passive awareness announces things coming into
view as you move, plus a census of what's around when you enter a map. Weather
and time-of-day changes are spoken, cutscene text and title cards are read,
toggling your lantern says lit or doused, entering or leaving stealth is
announced, and cycling characters reports the new leader's health and any
active conditions.

**Combat** is narrated as it resolves — turns, action costs, damage, and
effects. The arrows browse the battlefield with the same tile speech, I and U
walk the initiative order, P centers on the active unit, and K opens a
four-tab overview of one combatant: Up and Down walk its entries, Left and
Right change tabs. Your own movement is narrated: each WASD step speaks as you
take it, and a clicked course reports where the walk stopped. The placement
phase before a fight speaks each tile's validity, and selecting, placing, and
swapping characters is announced.

## Keys

### Everywhere

| Key | Function |
|-----|----------|
| **Z** | The game's left click — select or activate, depending on the screen (on ability bars it commits: a consumable is used, an ability enters targeting). |
| **X** | The game's right click — on most item and spell surfaces, reads the full description. Exceptions: on the feat tree X refunds a staged rank, and on worn equipment and the world map it does nothing. |
| **1–9** | Pick an option row (dialogue choices, popup buttons). |
| **Escape** | Back / cancel. |
| **Q / E** | The game's shoulder buttons — cycle party members or pages where a screen offers it. |
| **F1** | Contextual key table — the keys for the current screen; F1 closes. |
| **R** | Review mode — arrows walk the captured screen text; R again closes. |
| **Home / End** | Previous / next section of the current document. |
| **PgUp / PgDn** | Previous / next element of the current document. |
| **/** | Stop speech. |
| **[** / **]** | Speech history back / forward. |

### UI screens

| Key | Function |
|-----|----------|
| **W / S** (also A / D) | Previous / next section — wraps, and speaks the section's name and census. |
| **Arrows** | Rows and grid cells within the section. In trade and container screens, Left and Right at a grid's edge cross into the neighboring grid. |

### Overland

| Key | Function |
|-----|----------|
| **WASD** | Walk. |
| **Arrows** | Tile cursor — browse the map around the party. |
| **H / N / B / O / M** | Scans: hostiles, neutrals, loot, objects, exits. |
| **P** | Return the cursor to the party. |
| **K** | Points-of-interest list. Z activates the current entry; the list stays open. |
| **V** | Speak the walking course to the browsed tile — works from the POI list too. |
| **T** | Light or douse your lantern (spoken). |
| **.** | Next character — cycles the party, reporting health and conditions. |
| **,** | Inventory. |

### Combat

| Key | Function |
|-----|----------|
| **WASD** | Step the active character (the game's own movement). |
| **Arrows** | Battlefield cursor. |
| **I / U** | Initiative order forward / back. |
| **P** | Center on the active unit. |
| **K** | Combatant overview — Up/Down entries, Left/Right tabs; K or Escape closes. |
| **H / N** | Scan hostiles / friendlies. |
| **T** | Light or douse your lantern. |
| **Left/Right Ctrl** | The game's own ability-bar paging — untouched by the mod. |

## Configuration

After the first run, `BepInEx/config/SkaldAccessibility.cfg` in the game
folder holds the mod's options — combat narration detail, passive awareness,
terrain names, tile coordinates, automatic panel reading, the key table, the
auto-rebind, and more. Every entry carries its own description; edit with the
game closed.

## What's tested and what isn't

The whole surface is built. The core loop — overland exploration, dialogue,
combat, inventory and trade, the character sheets — has been played live and
the problems that surfaced have been fixed. What hasn't had that exposure:

- **Terrain names are unaudited.** The tile vocabulary (new in this release)
  was derived from the game's data and shipped ahead of its visual audit —
  some tiles may be named wrongly. Combat deliberately keeps the plain
  Water / Blocked / Open words.
- **Character creation, party management, the settings screens, and the
  level-up/feat screens** were built from the game's decompiled source and
  code-reviewed, but have only been lightly exercised in play.
- **The newest features** — the reachability verdicts, the V path readout, the
  combat movement and placement narration, and the reworked combat speech
  scheduling — are fresh; their wording has had only a first listen.
- **No full campaign has been completed with the mod yet.** Later-game content
  has had the least exposure.

Known issues:

- A reported problem with casting spells outside combat is under
  investigation.
- Two player reports are under diagnosis: the "Walking X tiles" travel line
  occasionally not speaking, and looted containers not reporting ", empty"
  while out of view.

## Planned additions

On the roadmap, in no particular order and with no dates attached:

- **Dialogue topic reading** — browsing a conversation's highlighted lore
  topics and hearing their definitions.
- **Combat log browsing** — keyboard access to the game's combat log screen.
- **On-demand vitals** — a health readout without cycling characters.
- **Wayfinding aids** — building on this release's reachability and path
  readout: conveying map shape and connectivity, beyond point-by-point
  browsing.

## Reporting issues

Include what you were doing, what you heard (or didn't), and
`BepInEx/LogOutput.log` from the game folder.

- **Discord:** reach the author directly.
- **GitHub:** open an issue at
  [Beholder issues](https://github.com/EarthboundPromoter/Beholder/issues).

## License and credits

This mod is released under the [MIT License](LICENSE). It contains no game
assets or game content.

- **SKALD: Against the Black Priory** by High North Studios AS, published by
  [Raw Fury](https://rawfury.com/). Buy the game on
  [Steam](https://store.steampowered.com/app/1069160/SKALD_Against_the_Black_Priory/).
- [BepInEx](https://github.com/BepInEx/BepInEx) by the BepInEx team
  ([license](BEPINEX_LICENSE.txt), LGPL-2.1).
- [Tolk](https://github.com/dkager/tolk) by Davy Kager
  ([license](TOLK_LICENSE.txt)).
- [NVDA](https://www.nvaccess.org/) by NV Access
  ([controller client license](NVDA_LICENSE.txt)).
