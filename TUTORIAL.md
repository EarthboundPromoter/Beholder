# Playing SKALD with Beholder — a tutorial

This is a guided introduction for new players: how SKALD itself works, including
the parts of its interface that are genuinely unusual, and how the mod presents
the game, including the parts of *it* that may feel unfamiliar at first. It
assumes you've installed the mod ([README](README.md)) and heard "Beholder
loaded" at the title screen.

If you take only one habit from this page: **when you're unsure what a screen
wants from you, press F1.** Every screen, overlay, and popup has its own key
table — Up and Down walk the rows, F1 closes it. The tables carry the
screen-specific quirks on the keys they belong to, so most of what this
tutorial explains is also discoverable in place.

## What kind of game this is

SKALD is a party-based roleplaying game in the style of late-80s CRPGs: a
prose-heavy story told through dialogue scenes and popups, an overland map you
walk tile by tile, towns and dungeons on the same tile grid, and turn-based
combat on that grid. You control a party of up to six characters. Most of your
time is spent in three modes — walking the map, talking, and fighting — plus
the usual screens: inventory, character sheets, trade, level-up.

Death is normal and saving is cheap. **F5 quick-saves, F9 quick-loads** — these
are the game's own keys and they work everywhere outside combat. Use them
freely.

## The one idea that explains the mod: everything is a mouse click

SKALD is a mouse-driven game with a controller mode bolted on. The mod switches
the game into controller mode and then drives the game's own **virtual mouse**
from the keyboard. When the mod says you've landed on something — a tile, an
inventory row, a dialogue option — the game's mouse pointer is now physically
parked on that thing.

That's why the two action keys are what they are:

- **Z is the left mouse button.** Not "confirm" in the menu-UI sense — an
  actual click on whatever you're parked on. What a click *does* depends on
  the thing: on the overland map it walks the party there (or attacks, or
  interacts); on an inventory row it selects the item; on a dialogue option it
  speaks it.
- **X is the right mouse button.** On most item and spell surfaces that means
  the full description or comparison tooltip. A few surfaces give it other
  jobs (the feat tree uses it to refund), and a few give it nothing at all.

Two consequences worth internalizing early:

1. **Selection and activation are separate clicks.** In the inventory, the
   first Z selects the item; a second Z on the same item uses or equips it.
   That's the game's own click-on-selected behavior, and it's why the mod
   speaks "Selected" before anything happens.
2. **Z acts wherever you're parked, even under an overlay.** The
   points-of-interest list and the combat overview are the mod's own overlays,
   but the highlighted entry corresponds to a real tile — and Z still clicks
   that tile. This is a feature: find a door in the POI list, press Z, and the
   party walks to it without you closing anything.

There is no universal "press Enter to confirm" in this game. Enter and Space
exist natively and act in a few places, but they are not reliable across
screens. **Z is the do-it key.**

## The game's own keyboard, and its oddities

The game keeps most of its native keyboard shortcuts under the mod, and some
of them are strange enough to deserve a warning.

**Ctrl is a game action, not a modifier.** It has three unrelated jobs
depending on where you are. On the overland map, **Left Ctrl toggles
stealth** — the mod announces "Entered stealth" or "Left stealth," and note
that toggling passes a short amount of game time, like waiting. In combat,
**Left and Right Ctrl move through the ability bar**. In the inventory,
**Ctrl swaps the filter tabs**. Don't hold Ctrl expecting keyboard-shortcut
behavior; nothing in this game works that way.

**Q and E are the shoulder buttons, and they're your main character keys.**
**Q switches characters** — anywhere the notion applies, from the world or
inside a screen. **E opens the character sheet** to the first page for the
current character, and from there **the Alt keys tab through the sequence of
character screens** (sheet, inventory, spellbook, and so on) — that's the
game's screen carousel, and Alt is its native key. Escape backs out.

**Number keys are choosers.** In dialogue and on popup buttons, pressing a
number picks that option — the mod always announces options with their
numbers, in spoken order. In combat, the numbers select ability-bar slots
directly, which is faster than paging with Ctrl once you've internalized
which number does what.

**Single letters open screens.** From the world: **C** character sheet, **G**
spellbook, **F** feats, **J** quest log, **L** level up, **T** light or douse
your lantern (the mod names the item: "Lantern lit"). These are the game's
own bindings and the mod narrates their results.

**One housekeeping note:** the game's defaults for Next Character and
Inventory are Q and E — the very keys the mod uses as shoulder buttons — so
on every launch the mod silently rebinds those two to period and comma and
keeps them there. You can mostly ignore this: Q covers character switching
and comma opens the inventory. (Config `Input.AutoRebind` turns the
rebinding off.)

**Keys to avoid:** **Tab** opens the game's developer console — if the game
stops responding to normal keys, you may be in it; Escape gets you out. **Left
Shift** is a purely visual highlight. **F2** opens the game's feedback form.

## Speech and review

- **Slash** stops speech immediately.
- **Left and right brackets** walk the speech history backward and forward —
  anything you missed is retrievable.
- **R** toggles review mode: the arrows then walk the captured text of the
  current screen. R again returns to normal.
- **Home, End, PgUp, PgDn** — the review cluster — read the full "examine
  document" of whatever you're focused on: Home and End move between its
  sections, PgUp and PgDn move within one. For items this is the composed
  stat block plus description, with definitions of any rule keywords appended
  at the end. It's the deep-reading tool; X is the quick one.

## UI screens: sections and rows

Every full-screen UI (inventory, character sheet, journal, settings, trade,
and so on) is presented the same way, so one habit covers all of them:

- **W and S** (A and D work too) move between the screen's **sections**, in a
  ring that wraps. Each landing names the section and what's in it: "Party
  Inventory, 12 items, 340 gold."
- **The arrows** move within the current section: rows in a list, cells in a
  grid.
- **Q** switches which party member you're looking at, on screens that are
  per-character — the normal way to check everyone's sheet or bags in a row.
  **The Alt keys** tab through the sequence of character screens.
- **Escape** backs out.

Item rows are spoken fully composed: name, stats with a comparison verdict
against what the character has equipped, value and weight. **X reads the full
description; Z selects, and a second Z uses or equips.**

In **trade and container screens** there are two inventories side by side;
Left and Right at the edge of one grid cross into the other. The section
census tells you whose side you're on.

In **settings**, rows change their values with Left and Right — that's the
game's own plus/minus chooser.

The **level-up feat tree** is the one screen where X has a bigger job: Z
highlights, a second Z buys a rank; X highlights, a second X refunds a rank
you've staged this visit. The mod announces ranks, tier unlocks, and your
remaining points, including "N points unspent" if you leave with points on
the table.

## The overland map

Walking and looking are two different sets of keys, active at the same time:

- **WASD walks the party**, one tile per press. Bumping into something
  impassable gets the game's own "You can't go that way."
- **The arrows move a tile cursor** around the party without moving anyone.
  Each tile speaks what's on it, whether it's passable ("Blocked" / "Open"),
  the terrain by name (cobbles, birch trees, shore...), its map coordinates —
  and "out of reach" if there's no walkable route to it from where the party
  stands. **P** snaps the cursor back to the party.
- **V reads the walking route** to the cursor tile: "North 4, east 2,
  arrive." That's the exact course a Z-click would take.
- **Z clicks the cursor tile**: the party paths to it, attacks what's there,
  or interacts with it — whatever the game's own click would do.

**The scan keys** jump the cursor through everything interesting in view, one
category per key: **H** hostiles, **N** neutrals, **B** loot, **O** objects,
**M** exits. Press one repeatedly to cycle through that category; each stop
is a real cursor landing, so Z and V work on it.

**K opens the points-of-interest list**, the overland tool you'll use most: a
persistent, live list of everything notable on the map, grouped into category
pages. Up and Down walk the entries, Left and Right change pages. It stays
open while you walk — WASD still moves the party, and the list keeps itself
current. Z acts on the highlighted entry (the game's click on its tile), V
reads the path to it. K or Escape closes it.

**Passive awareness** speaks on its own as you travel: things coming into
view are announced as brief count lines, and entering a map speaks a census
of what's around. Weather and time-of-day changes, lantern state, stealth,
and leadership changes are all narrated as they happen. If the chatter is
more than you want, it's configurable — see the config section of the
README.

## Combat

Combat is turn-based on the same tile grid. The broad shape: a **placement
phase** where you position your party, then rounds in **initiative order**
where each combatant spends action points to move and act.

**Placement:** Z on a party member selects them ("Embla selected"), and
**WASD then moves the selected unit one step at a time** — the mod speaks
each landing, and stepping onto an ally swaps their positions ("Swapped with
Embla"). Alternatively, park the arrows' cursor on a tile (validity is
spoken) and Z places the selected unit there directly. When everyone's
placed, confirm and the fight starts.

**Orientation:** the arrows browse the battlefield exactly like the overland
cursor — occupant, Blocked or Open, coordinates. **I and U walk the
initiative order** forward and back, each entry prefixed with its position;
this is how you learn who's in the fight and when they act. **P** centers the
cursor on whoever is currently acting. **H and N** scan hostiles and
friendlies. **K** opens a four-tab overview of one combatant — Up and Down
walk its entries, Left and Right change tabs — and it stays up while you
step your own character with WASD.

**Your turn:** WASD steps your character one tile per press, and each step is
spoken as you take it. For anything else, park the arrows' cursor where you
want to act and press Z — the game's click moves you there, attacks the
target, or interacts. A clicked multi-tile walk reports where it ended.

**Abilities:** your character's abilities and usable items live on the bar at
the bottom of the screen. **Left and Right Ctrl move through the bar**, and
**the number keys select its slots directly** — once you know that 1 is your
attack and 3 is your heal, the numbers are the fast path. **Z commits** the
selected slot (a consumable is used, an ability enters targeting), and **X
reads the tooltip**. With an ability targeting, the battlefield cursor plus Z
picks the target.

Everything that resolves — turns, action costs, damage, effects, deaths — is
narrated from the game's combat log as it happens.

## Dialogue and popups

Dialogue scenes read themselves: the story prose speaks, then the numbered
options. **The arrows move through everything here** — down and up through
the options, and from the top option up into the story text to re-read it
sentence by sentence. Choose by **number**, or press Z on the highlighted
option. WASD does nothing in dialogue. Highlighted lore topics are mentioned
at the end of the prose.

Popups follow the same convention: the popup announces itself, arrows move
between its buttons, numbers or Z choose, Escape cancels.

## Habits that help

- **F1 first.** Every screen documents itself.
- **Quick-save often** (F5). SKALD expects it of you.
- **Let Q be your status check.** Switching characters reports the new
  leader's health and conditions; cycling through everyone is a fast party
  checkup.
- **Use the POI list as your map.** K plus V — what's here, and how far —
  answers most "where am I" questions without a single step taken.
- **Trust "out of reach."** If a tile says it, there is no walking route from
  where you stand — look for a door, a key, or another way around rather
  than clicking at it.
- **Remember the double Z.** If an item was "selected" and nothing else
  happened, the second press is the one that acts.
- **The config file is yours.** `BepInEx/config/SkaldAccessibility.cfg` (game
  closed) tunes narration detail, passive awareness, coordinates, and more —
  every entry is described in the file.
