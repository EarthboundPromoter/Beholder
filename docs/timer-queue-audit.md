# Timer and queue audit — 2026-09-02

Owner ruling that triggered it: no timers unless there is no other option; one player interaction produces one composed string; queued follow-on sequences only where they genuinely make sense. Trigger case: Shane's 0.5.5 log (docs/tile-art/Discord Feedback 9-1 to 9-2-26.txt plus his LogOutput), where the attribute editor spoke each row twice and a fast second press played the previous row's still-pending line after the new one.

Scope: every mechanism under src/ that decides WHEN or WHETHER a speech string is spoken. The full raw inventory (about 80 rows) was produced by a read-across pass; this document is the ruled digest. Sources named are the second argument to SpeechService.Say/SayQueued.

## Fixed in this pass (shipped 2026-09-02)

| Mechanism | Was | Now |
|---|---|---|
| SpeechService.Tick 0.15 s gap + Tolk_IsSpeaking gate (Scaffold/Timing.cs) | Queued lines dispatched one per 150 ms, mod-side; Say(Immediate) never flushed them, so pending lines outlived the interaction that produced them and played after a later interrupt. | Tick drains the whole frame's queue to the reader at the clock. The reader owns sequencing; an interrupt cancels everything it has not spoken. Timing.cs deleted. |
| SpeechService `_lastQueued` cross-frame dedup | Identical consecutive queued text dropped forever, not just within the frame. | Reset every Tick. Within-frame collapse kept; cross-frame repeats are the per-source diffs' call. |
| Pump.DrainEditorRow hover-landing join + its `> 1 frame gap` re-arm (Patches/EditorFlipPatch.cs row landings) | Spoke every attribute row a second time two frames after TableCursor's index landing; the gap re-arm re-spoke the row after every plus/minus click. | Deleted. TableCursor.ComposeSheetCell composes the same line at the press. Flip join and resync stand. |
| GUIControl.setMouseToClosestOptionAbove/Below native hover walk (Patches/SkaldIOPatch.cs FunnelStep) | Increment/decrement stepped from the HOVERED element; hover lost (mouse jitter, alt-tab, popup re-park) meant a silent no-op press. | Index walk on base UICanvas canvases: step the remembered index, snap. Overriding canvases (ability grid, inventory sheet) keep native. Edge branch untouched. |
| PopUpUIBase.setMouseToClosestButtonAbove/Below (Patches/PopupGridNavPatch.cs PlainStep) | Inventory popup already index-walked; every other popup ran the hover walk. | Same index walk for every base popup; clamp speaks "Top of list." / "Bottom of list." |

## Dead machinery (delete when convenient, zero behavioural risk)

- SpeechService modal pen: `BeginModal`, `EndModal`, `RegisterVolatile`, `Pen`, `VolatileSources`, `PenKeepLatest`. Zero call sites.
- SpeechService `FlushSource`. Zero call sites.
- SpeechService `_eventOverflow` coalescing and the 20-slot cap. Unreachable with a per-frame flush.
- SpeechService `ExtractSource`. One caller (Pump furniture hold) that already keeps its own dictionary.
- GateReceipts wall-clock throttles (1 s axis log, 0.5 s rapid-pair window). Log only.
- OverlandCursor `QueueDepth < 15` backoff inside the passive census accumulator. Depth is now zero at every read.

## Timers that still gate speech (owner call per row)

Ordered by how far the utterance is moved from its cause.

| Mechanism | File | What it delays or drops | Alternative |
|---|---|---|---|
| 45-frame echo window for held combat strip echoes | CombatSpine.cs EchoWindow | A strip line waits up to 45 frames to see if the composed stream covers it, then speaks late. | Fold the strip echo into the composed event line at compose time; unique echoes speak on the event frame. |
| 90-frame composed ledger + bark sweep | CombatSpine.cs LedgerWindow | Barks that word-match a recent composed line are dropped. | Same: compose bark facts into the event line; no ledger. |
| 30-frame NarrationActive activity window | CombatSpine.cs | Decides whether a strip line is an echo (held) or browse content (immediate). | Gate on the game's own resolve state instead of frame age. |
| 0.35 s passive census accumulator | OverlandCursor.cs | Merges arrivals across held-key steps into one line. | Accumulate while the movement key is held (game/Input truth), flush on release. |
| +6-frame echo sweep | OverlandCursor.cs | Second census sweep six frames after an interact, guessing when the game finished mutating. | Hook the mutation (the interact result) instead of guessing the frame. |
| 2-frame tooltip grace | Pump.cs DrainTooltipDismiss | Clears an auto-raised tooltip within two frames. | Clear at the raise choke itself, same frame. |
| 2 quiet frames refund settle | Pump.cs refund tally | Waits for the legality cascade to stop draining ranks. | Read the settled rank set after updateFeatsLegality returns (postfix on the cascade root). |
| 2-frame stealth wait-line swallow | Pump.cs | Drops "You wait a short while." near a stealth toggle. | Drop it at the source when DataControl.hide is the caller (prefix flag), no window. |
| 1-frame deferred landings | CombatCursor.cs, OverlandCursor.cs census/dirty sweep | Speak from the next frame so the game's re-hover has settled. | No other option class: the game mutates after the press. Keep. |
| 1 to 2 frame input eat tails | ReviewLayer, KeyTable, cursors | Keep the game blind to a closing press across the FixedUpdate straddle. | Input side, not speech. Keep. |
| Same-frame stamps (`_playerNavFrame`, `_popupSpokeThisFrame`, `ClaimedThisFrame`) | Pump.cs, CampZonePatch, CraftingZonePatch | Interrupt-versus-queue policy and same-frame handoffs. | Not timers; equality with the current frame. Keep, but each ClaimedThisFrame handoff disappears if the crossing composes one string. |

## Interactions that still produce more than one string

These are the composition candidates. A sanctioned sequence is one where the follow-on is optional body text the user may cut.

| Interaction | Strings today | Ruling needed |
|---|---|---|
| Attribute row landing | row line (interrupt) + full description (queued) | Sanctioned: description is the AutoReadBody follow-on and the next press cuts it. Keep. |
| Attribute plus/minus press | points line only (RULED 2026-09-02: the value change composes, the description is the row keys' business; the same-frame SheetDesc re-render stays a capture) | Done. Feat rank presses treated alike; tier unlock line and the FeatPoints trailer kept. |
| Feat rank press | rank line + tier line + refund line + pool line (up to 4) | Compose "Arms Mastery, rank 3 of 6. Unlocked Ranged Accuracy. 8 ranks to distribute." as one. |
| Popup open | title + up to three body slots, first interrupts, rest queued | Compose title + body as one string. |
| State entry | State line + NumericButtons line + AbsorbFocus workaround | Compose state + buttons; delete AbsorbFocus. |
| Party leader change | leader line + PCVitals held by text match on "is now leading the party" | Compose leader + vitals at DataControl.changePC; delete the text-match hold. |
| Review close | "Closed." + queued anchor | Compose "Closed. <anchor>". |
| Crafting feedback | result line + queued progress | Compose. |
| Combat event | composed line per event, identical runs coalesced ", N times" | Already the model case. Keep. |
| Turn boundary | turn line stealing the matching bark and log line | Already the model case. Keep. |

## Keep as is (settled-state diffs and game-flag gates)

Per-source content diff at the drain (`_pendingContent` / `_lastContent`), selection diff on (control, index), list-selection diff, canvas-switch note, edge observer pre/post compare, move receipt gated on `Character.moveAlongCombatPath`, combat snapshot diffs, once-per-encounter census latch, per-frame memoization caches. None of these move an utterance in time; they decide whether a settled value changed.

## Open defect surfaced by the same log

Loot popup "Selected: <first item>" interleaving every landing from LogOutput f127629 on, with no "N of 7" nav line. The list-selection join now logs the writing list's type on the Debug channel (`[ListSel]`), so the next log names the list. Ledger B18.
