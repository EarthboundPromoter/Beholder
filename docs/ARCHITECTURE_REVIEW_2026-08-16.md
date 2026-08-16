# SKALD Accessibility — Revival Architecture Review

Date: 2026-08-16. Claude-authored, for the owner. Status: REVIEW — no code changed, nothing committed, all remediation pending owner rulings.

Evidence base: full read of `src\` plus the uncommitted working-tree diff, with every sampled reflection target verified against `decompiled_full\`; the Metaclaude 2026-07-05 audit (`Metaclaude\audit\profile_skald.md`); the matured-standards extraction from Citizen Speaker / Sleeptalker / Vantage (including the approved `OAG_Phase0\ARCHITECTURE_PROPOSAL.md`); and the Words of Power (RW3) combat-voicing extraction. The memory store for this project was rebuilt the same day (57 files, lineage-tip behavioral corpus + migrated foundered-era technical knowledge).

## Verdict

The foundation is worth reviving, and parts of it are already at the standard the later projects matured into. The mod on disk IS the Session-5 second-generation architecture the old records claim: event-driven content hooks, an index-based navigation cursor, and the SkaldIO input-enablement layer. The gap to the matured standard is concentrated, not diffuse: the speech *delivery* layer is poll-and-dedup throughout (the exact pattern the lineage later banned and replaced with the note-at-hook / read-at-end-of-frame timing spine), there are three genuine proxy determiners, the loader/speech stack diverges from the lineage norm on every axis (BepInEx 6 bleeding-edge vs 5, netstandard2.1 vs net472, direct NVDA P/Invoke vs Tolk), and process hygiene (commits, docs, versioning) never got the discipline the later projects run on. None of this requires a rewrite; it requires re-seating existing hooks onto the timing spine and porting scaffold code that now exists ready-made in Sleeptalker and Vantage.

## 1. What exists (state of the working tree)

Four layers compose the mod:

- **Input enablement** (`SkaldIOPatch.cs`, applied deferred from `Plugin.Update`): postfixes on `SkaldIO.getOptionSelectionButton*` OR-ing arrow keys into the controller-gated paths; Enter/RightShift mapped to virtual-mouse clicks; prefixes replacing `UICanvas.incrementCurrentSelectedButton`/`decrement...` with pure index math; re-implementations of `setMouseToClosestOptionAbove/Below` and popup mouse-set minus the `isControllerConnected()` gate. The design rationale — enable keyboard on the game's own navigation paths *without* spoofing controller mode (which would change combat behavior and glyphs) — is documented and sound.
- **Navigation speech** (`IndexNavigationPatch`, `PopupNavigationPatch` in the stale-named `PopupHoverPatch.cs`): per-frame `update()` postfixes compare a mod-side `NavigationCursor` index against last-spoken and speak the focused button's rendered `UITextBlock.content`.
- **Content speech** (`ContentSpeechPatch`: 11 targeted postfixes on `GUIControl` setters, `ToolTipPrinter.setToolTip`, `PopUpBase` setters; plus `PopupAnnouncePatch` on the `PopUpControl.addPopUp` funnel, `CombatLogPatch` on the `CombatLog.addEntry(string,string)` funnel, `BarkInterceptPatch` on the `Bark` constructor, `BookInterceptPatch` on `ItemBook.getContent`). `TextInterceptPatch` is confirmed stripped to a `CleanText` utility.
- **State tracking** (`StateTransitionPatch` — despite the name, a per-frame poller): reflection-reads `MainControl.gameControl.currentState`, classifies the class name by string matching into a 20-mode enum, announces transitions, clears dedup, gates the combat log.

Speech reaches NVDA by direct P/Invoke of `nvdaControllerClient64.dll` (`ScreenReaderOutput.cs`) — NVDA-only, no Tolk, no SAPI fallback, and the DLL is neither in the repo nor deployed by `build_and_deploy.sh`.

**The uncommitted diff** (sitting since 2026-03-18) is three files: a trivial log-level fix in SkaldIOPatch (finished); a genuinely good mid-flight generalization of IndexNavigationPatch — replacing the hardwired screen special-case with "process whatever `getControllerScrollableList()` returns", the game's own universal answer, plus an image-only feat-tree-node name fallback — still carrying seven active debug-log lines; and a mid-flight experiment in ContentSpeechPatch (`AttributeEditorPlusActive`) that key-sniffs arrow presses to guess the attribute editor's plus/minus column — a textbook proxy determiner, self-documented as wrong (its own comment names the correct hook, `EditorSheetEntry.controllerScrollSidewaysLeft/Right`, which was never implemented).

## 2. What already meets the matured standard

- The **funnel hooks**: `PopUpControl.addPopUp` (the funnel behind all `addPopUpOK/addPopUpName/...`), `CombatLog.addEntry(string,string)` (the overload funnel), and the `Bark` constructor are true causal joins — the lineage's "hook the funnel, not the family" doctrine, discovered here first.
- The **SkaldIO layer's** no-spoofing design and index-math replacement of increment/decrement — this is the "ride the game's own input actions" principle in an earlier vocabulary.
- The uncommitted `getControllerScrollableList()` generalization — port-first thinking (mechanism over instances), exactly the direction the lineage went.
- **Render-first speech source**: nearly all speech reads rendered `UITextBlock.content` — the right call for a bitmap-font engine, and the matured render-honesty rule by construction.
- **No tactic computing anywhere** — clean pass.
- **Reflection discipline** is mostly good: cached `FieldInfo`/`MethodInfo`, init-once with failure latches, and every sampled reflected name still resolves against the decompile.
- **Repo basics**: the .gitignore is correct (bin/obj/libs/decompiled all untracked — the July audit overstated this).

## 3. Where it falls short — the four concentrated gaps

### Gap 1: poll-and-dedup speech delivery (the architectural core of the revival)

The matured standard (approved OAG architecture; validated across CS1/CS2) is the **timing spine**: Harmony hooks record *what fired* and nothing else; all reads and composition happen once at end of frame; bursts coalesce (compress, don't curate); animations speak settled values. Dedup dictionaries, seen-text sets, init-suppression windows, and manual dedup-clearing are all classified as symptoms of hooking off-join.

The current mod is the "before" picture of exactly that rule: `ContentSpeechPatch` absorbs per-frame setter floods with a per-source `_lastSpoken` dictionary (its header comment "No suppression windows. No seen-text sets." is contradicted three lines later); navigation speech runs in three per-frame update postfixes with LastSpoken comparisons; `PopupNavigationPatch` carries a 30-frame init-suppression window; the slider patches carry a 3-frame window and a first-value swallow; `GameState` and the popup Enter handler manually call `ClearAll()` to un-jam the dedup — the tell that the joins are wrong. And state transitions are frame-polled even though the decompile plainly shows the event join: `MainControl+StateControl.setState(SkaldStates)` (MainControl.cs line 212).

The fix is not a rewrite. The hooks are mostly on the right methods already; they need to *note* instead of *speak*, with one end-of-frame drain doing composition — at which point the dedup dictionary, both suppression windows, the first-value swallow, and the ClearAll hacks become deletable. Deletability is the standard's own test that the joins are right. `Vantage\src\Pump.cs` is the reference implementation, including the static-callback drain (`Canvas.willRenderCanvases`, frame-guarded, exception-proofed).

### Gap 2: proxy determiners (three)

- `AttributeEditorPlusActive` key-sniffing (in the uncommitted diff) — announced add/remove can be the opposite of what Enter will do whenever the column changes by any path other than a same-frame keypress. Replace with the hook its own comment names.
- `NavigationCursor` is documented "authoritative" but is a mod-side mirror of the game's `UICanvas.currentSelectedButton`, kept in sync only because the mod owns the increment/decrement prefixes; anything else that moves selection desyncs it. The game's `getCurrentSelectedButtonIndex()` is already cached in SkaldIOPatches — read the game's flag; the cursor shrinks to a last-spoken record or disappears.
- `GameStateTracker.IsInCombat` (a string-classification of state names) gates the combat log, and `SliderHoverPatch.LastFocusedSliderButton` mirrors focus. Both should read game truth (the state machine's own current state; the game's hover field at the moment of use).

### Gap 3: loader and speech stack divergence

Current: BepInEx 6 bleeding-edge (`6.0.0-be.*`, floating — unreproducible restores), netstandard2.1, NuGet Unity refs plus duplicate `libs\` refs, Debug config deployed, direct NVDA-only P/Invoke, speech DLL not shipped by the deploy script. Lineage standard for exactly this class of game (Unity Mono): BepInEx 5.4.23 x64, net472, `<Private>false</Private>` references straight into the game's own `BepInEx\core` and `Managed` folders, Tolk (screen reader preferred, SAPI fallback, log-only degrade), one-zip release with loader and Tolk bundled, deploy-on-exit watcher that snapshots `LogOutput.log` before every relaunch. Migration is an owner decision gate (section 6), but staying on BepInEx 6 BE should at minimum become a deliberate, pinned choice.

### Gap 4: process and hygiene

One commit ever; three files uncommitted for five months; version frozen at 0.1.0; no README, no docs directory until this review, no `.gitattributes` (LF/CRLF churn on every touch); stale header comments (PopupNavigationPatch claims a buttonPressIndexLeft mechanism it no longer uses); misleading names (`TextInterceptPatch` intercepts nothing; `StateTransitionPatch` patches nothing); dead code (StateTransitionPatch "Strategy 1" and its 22-entry `MapGUIControlToState` table target a field that does not exist on MainControl; three unused cached FieldInfos; an unreachable init-failure branch). The lineage's process kit — git-log-as-changelog voice, verification checklists written before test rides, the gap ledger (every silent surface becomes a row the owner rules VOICE or JUSTIFIED-DROP), Timing.cs constants registry with provenance comments, input-model designed in a dedicated owner session against a documented input contract — all applies here and none of it existed yet in this project's era.

## 4. Defect register (concrete, beyond the architectural gaps)

1. **Poll kill-switch (high):** any transient exception in `StateTransitionPatch.PollState` permanently sets `_initialized = false` — one null during a loading frame silently disables state tracking, mode announcements, dedup clearing, combat-log gating, and (if it fires before first success) the entire SkaldIO keyboard layer, which is deferred behind a successful state read.
2. **Global Enter/RightShift click synthesis (high):** RightShift while typing a character name emits right-clicks; Enter clicks whatever the virtual mouse is over in any state, including ones the mod doesn't manage. No text-input-mode gate exists. (The lineage's field-editing idiom — diff-echo plus raising the game's own input lock — is the model fix.)
3. **Speech history self-corruption (medium):** `[`/`]` replay routes through `Speak()`, which re-adds the entry to history and resets the cursor — you can never reliably walk more than one step back. F1 repeat duplicates the same way.
4. **BookInterceptPatch mis-hook (medium):** `ItemBook.getContent()` is a data getter, not a render event — spoiler-risk if called off-display, and pages beyond the first can never be read. Needs re-hooking at the render join with page-turn support.
5. **Arrow-scroll hard-disable (medium):** `getButtonScrollUp/Down` return-false prefixes remove arrow-hold scrolling game-wide, for every surface, permanently.
6. **Per-frame reflective cost (medium):** three update-postfix pollers plus PollState every frame, on a software-rendered game — the lazy-polling pattern the standard bans; also one genuinely uncached reflective lookup per slider focus change.
7. **Numeric-button announcement race (low):** `AnnounceNumericButtons()` reads at the instant the poller notices a transition; a state that populates its buttons a frame later yields silence.
8. **Fragility surface (low):** dense private nested-type reflection strings (`UIFeatTree+FeatTreeCollection+Node`, `MainControl+StateControl`) — currently all verified, but a game update is a broad breakage surface. The lineage's seam-audit idiom (per-patch `Prepare()` existence checks plus a one-line spoken boot report: "N game hooks missing after an update") is the ready-made mitigation.

## 5. What the siblings supply ready-made

- **Sleeptalker `mod\src\Scaffold\SpeechService.cs`** — the speech tier to port: Immediate vs Queued priorities, paced queue pump against `Tolk_IsSpeaking`, explicit flush (never a side effect), source tags, modal-primacy gate with the latest-per-source pen, 200-entry history ring (fixing defect 3 by construction), central Clean with transcode-don't-strip. Designed as a shared-assembly candidate; nothing in it is game-specific.
- **Vantage `src\Pump.cs` + `Hooks.cs`** — the timing-spine and seam-audit reference implementations (gap 1 and defect 8).
- **CS1 `docs\input-model.md` + its InputManager** — the applicable input model for a mouse/virtual-mouse game (SKALD has no controller nav graph to force on, so Vantage's forced-controller approach mostly does not apply): one mode-ordered dispatch, KeyScope table shared with F1 help, document the game's real input contract first. SKALD-specific law already on record: Left/Right Ctrl are game-reserved in combat.
- **The review-cursor channel law** (CS1/Vantage): the mod's index cursor should *drive the game's own virtual-mouse/hover position* — the exact hover render a mouse would paint, so a sighted observer sees what speech says — with commit as exactly one native activation. The SkaldIO prefixes already put the mod in position to do this; it's a re-aim, not new machinery.
- **The debug bridge idiom** — separate gitignored TcpListener plugin, observation-first, `/watch` receipts as the live oracle for exactly the hook-verification work this revival needs.
- **RW3's combat kit, for when combat gets built** (Tier 2 in the old image-controls inventory, still unbuilt): the composed one-utterance-per-turn-boundary pipeline (crisis first, player-action digest, ambient orphans last); party generalization of the crisis tier (damage to any PC is immediate; downed PCs are individually extracted lines per causal-compression); the eyes/hand split (scans that move nothing plus one deliberate jump); exact-offset direction phrasing over coordinates; the AoE census pattern (ask the game's own targeting/impacted-tiles logic, problems first, "Within AoE: You, 1 ally, 2 enemies", You/allies/enemies order); trailing positional counters (rule now ported into this store); "trust the silence" as an explicit contract. `CombatLog.addEntry` is the confirmed text funnel; the semantic leaves that call it are where actor/target/amount still co-occur for grouping. The spatial framework is VALIDATED-1 everywhere — SKALD is precisely the second-game confirmation it has been waiting for, so expect boundaries to need re-ruling with the owner rather than silent inheritance.

## 6. Proposed remediation sequence (all pending owner rulings)

Phase A — hygiene (hours, no design decisions): disposition the five-month-old diff (log fix commit-ready; nav generalization commit after stripping the seven debug lines; the key-sniffing experiment reverted or WIP-quarantined pending the real hook); add `.gitattributes`; fix the poll kill-switch (log-and-continue); fix history replay (bypass Add); delete dead code and fix stale comments/names; make the deploy script ship the speech DLL and add a Release path.

Phase B — the timing spine (the real architectural work): port SpeechService and the Pump pattern; convert existing hooks to note-only; patch `StateControl.setState` for transitions (retiring PollState to a settled-moment truth-read); move navigation speech into the increment/decrement joins; retire the dedup/suppression apparatus and verify by deletion; kill the three proxy determiners; fold sliders into the same event path (deriving targets from the `UITextSliderButton` base, not three hardcoded names).

Phase C — input safety and scope: text-input-mode gating for Enter/RightShift/slash; scope arrow postfixes and the scroll suppression to states the mod actually navigates; an input-model session with the owner against SKALD's documented input contract.

Phase D — surface build-out under the new spine, per the old tier inventory (combat next per Tier 2, with the RW3 kit; inventory grid Tier 3), each surface through the lineage loop: decode, propose, owner ruling, build, bridge-receipt, live ride.

**Owner decision gates flagged, not assumed:** (1) migrate loader/speech stack to BepInEx 5 + net472 + Tolk vs pin-and-stay; (2) adopt the hover-drive review-cursor model for navigation; (3) whether to stand up a SKALD debug bridge; (4) keymap redesign session vs keep the current five hotkeys; (5) repo layout convergence (mod/bridge/tools/docs split); (6) commit cadence and whether this review's Phase A lands as the revival's first commits.
