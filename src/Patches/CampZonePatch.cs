using HarmonyLib;
using System.Reflection;

namespace SkaldAccessibility.Patches
{
    /// <summary>
    /// Camp zone chain + food-state feedback (owner rulings 2026-08-19;
    /// ground truth in the camp-flow survey). The camping sheet's native
    /// controller chain is Activities ↔ Party's Food only — the cook pot
    /// ("Tonight's Meal") and its Clear button are mouse islands, so food
    /// added by mistake could not be taken back out by keyboard (Exit and
    /// Begin Rest both dump the pot).
    ///
    /// The chain becomes: Activities ⇄ Party's Food ⇄ Tonight's Meal ⇄
    /// Clear (A/D crossings, all reversible — the CraftingZonePatch shape:
    /// zone truth is the sheet's own currentControllerSurface; delegation
    /// makes everything downstream native — the funnel walks pot rows, pot
    /// cells compose from the pot segment's own cookingPot inventory, and
    /// Z is the native click: second-Z on a pot cell returns the food, Z on
    /// Clear empties the pot). Crossings speak the game's own rendered
    /// labels; the pot crossing carries the LIVE meal readout ("Tonight's
    /// Meal. Food: 12/30.").
    ///
    /// Inside Activities the game's own sideways flip (minus ↔ plus) stays
    /// native — this patch only takes the press when the flip is exhausted:
    /// D at plus crosses to Party's Food (natively that crossing is
    /// SILENT), A at minus speaks "Edge." (natively a silent dead end).
    ///
    /// Food-state feedback rides the game's own per-frame meal-label write
    /// (setMealLabel — the one composer of "Food: n/m"): the diffed line
    /// speaks on every change with the hidden recovery tier appended
    /// (full / partial / below-half-nobody-works — thresholds the UI never
    /// states, beginRest's own math). On camp entry the other invisible
    /// gate speaks once: members whom any wound blocks from working, in the
    /// rest summary's own wording. Seam-gated (WP8).
    /// </summary>
    public static class CampZonePatch
    {
        private static bool Ready =>
            Seams.UIInventorySheetCampingFoodType != null
            && Seams.InvSheetCamp_activities != null
            && Seams.InvSheetCamp_buttons != null
            && Seams.InvSheet_secondaryInventoryGrid != null
            && Seams.InvSheet_currentControllerSurface != null
            && Seams.InvSheet_mainInventoryGrid != null
            && Seams.InvSegment_column != null
            && Seams.InvSegment_gridWidth != null
            && Seams.GUIControl_getControllerScrollableList != null
            && Seams.GUIControl_setMouseToUIElement != null
            && Seams.UICanvas_setCurrentSelectedButton != null;

        private static int _claimFrame = -1;
        private static bool _entryFactsSpoken;

        /// <summary>Consulted by InventorySegmentPatch — a crossing owns the
        /// utterance, never the unmoved-column edge line.</summary>
        internal static bool ClaimedThisFrame
            => UnityEngine.Time.frameCount == _claimFrame;

        /// <summary>From the Pump's state clock: entry facts re-arm per camp
        /// screen entry.</summary>
        public static void OnStateTransition()
        {
            _entryFactsSpoken = false;
        }

        [HarmonyPatch]
        public static class SidewaysLeftClaim
        {
            [HarmonyPrepare]
            static bool Prepare() => Ready && Seams.GUIControl_controllerScrollSidewaysLeft != null;

            [HarmonyTargetMethod]
            static MethodBase TargetMethod() => Seams.GUIControl_controllerScrollSidewaysLeft;

            [HarmonyPrefix]
            static bool Prefix(object __instance) => Sideways(__instance, -1);
        }

        [HarmonyPatch]
        public static class SidewaysRightClaim
        {
            [HarmonyPrepare]
            static bool Prepare() => Ready && Seams.GUIControl_controllerScrollSidewaysRight != null;

            [HarmonyTargetMethod]
            static MethodBase TargetMethod() => Seams.GUIControl_controllerScrollSidewaysRight;

            [HarmonyPrefix]
            static bool Prefix(object __instance) => Sideways(__instance, +1);
        }

        /// <summary>The per-frame "Food: n/m" writer — the one composer of
        /// the meal readout. Speaks (diffed) with the recovery tier appended;
        /// first fire per camp entry also queues the wounded-member facts.</summary>
        [HarmonyPatch]
        public static class MealLabelHook
        {
            [HarmonyPrepare]
            static bool Prepare() => Seams.InvSheetCamp_setMealLabel != null;

            [HarmonyTargetMethod]
            static MethodBase TargetMethod() => Seams.InvSheetCamp_setMealLabel;

            [HarmonyPostfix]
            static void Postfix(string __0)
            {
                try
                {
                    if (!_entryFactsSpoken)
                    {
                        _entryFactsSpoken = true;
                        SpeakWoundedFacts();
                    }
                    Pump.NoteContent("CampFood", ComposeFoodLine(__0), interrupt: false);
                }
                catch { }
            }
        }

        private static string ComposeFoodLine(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return raw;
            bool disabled = raw.Contains("[Disabled]");
            var m = System.Text.RegularExpressions.Regex.Match(raw, @"Food:\s*(\d+)\s*/\s*(\d+)");
            if (!m.Success) return TextCleaner.CleanText(raw);
            int cur = int.Parse(m.Groups[1].Value);
            int req = int.Parse(m.Groups[2].Value);
            string line = $"Food {cur} of {req}";
            if (disabled || req <= 0) return line + ", not required.";
            // beginRest's own thresholds (CampActivityState.cs:185-234) —
            // stated nowhere in the UI.
            float ratio = (float)cur / req;
            if (ratio >= 1f) return line + ". Full recovery.";
            if (ratio >= 0.5f) return line + ". Partial recovery.";
            return line + ". Below half: the party will be too hungry to work.";
        }

        /// <summary>Any wound at all blocks a member's camp activity
        /// (isBloodied = current wounds below max) — the game only reveals
        /// this AFTER the rest is spent. Queue the rest summary's own
        /// wording per blocked live member, once per camp entry.</summary>
        private static void SpeakWoundedFacts()
        {
            try
            {
                if (Seams.Character_isBloodied == null || Seams.MainControl_getDataControl == null
                    || Seams.DataControl_currentMap == null || Seams.Map_playerParty == null
                    || Seams.Party_getObjectList == null) return;
                object dc = Seams.MainControl_getDataControl.Invoke(null, null);
                object map = dc != null ? Seams.DataControl_currentMap.GetValue(dc) : null;
                object party = map != null ? Seams.Map_playerParty.GetValue(map) : null;
                var members = party != null
                    ? Seams.Party_getObjectList.Invoke(party, null) as System.Collections.IEnumerable
                    : null;
                if (members == null) return;
                foreach (object m in members)
                {
                    if (m == null) continue;
                    try
                    {
                        if (Seams.Character_isDead != null && (bool)Seams.Character_isDead.Invoke(m, null)) continue;
                        if (!(bool)Seams.Character_isBloodied.Invoke(m, null)) continue;
                        string name = TextCleaner.CleanText(
                            Seams.SkaldBaseObject_getName?.Invoke(m, null) as string ?? "");
                        if (!string.IsNullOrWhiteSpace(name))
                            Scaffold.SpeechService.SayQueued($"{name} is too heavily wounded to work.", "Camp");
                    }
                    catch { }
                }
            }
            catch { }
        }

        // ---- The chain ----

        private static bool Sideways(object gui, int dir)
        {
            if (!Ready) return true;
            try
            {
                object sheet = Seams.GUIControl_getControllerScrollableList.Invoke(gui, null);
                if (sheet == null || !Seams.UIInventorySheetCampingFoodType.IsInstanceOfType(sheet))
                    return true;

                object activities = Seams.InvSheetCamp_activities.GetValue(sheet);
                object pot = Seams.InvSheet_secondaryInventoryGrid.GetValue(sheet);
                object buttons = Seams.InvSheetCamp_buttons.GetValue(sheet);
                object party = Seams.InvSheet_mainInventoryGrid.GetValue(sheet);
                object surface = Seams.InvSheet_currentControllerSurface.GetValue(sheet) ?? party;
                if (activities == null || pot == null || buttons == null || party == null)
                    return true;

                if (ReferenceEquals(surface, activities))
                {
                    if (dir > 0)
                    {
                        // Native flip minus→plus first; at plus, cross (the
                        // native crossing exists but is silent).
                        if (CanFlip(activities, Seams.TextSlider_canScrollRight)) return true;
                        EnterGrid(gui, sheet, party, column: 0, "Party's Food");
                        return false;
                    }
                    if (CanFlip(activities, Seams.TextSlider_canScrollLeft)) return true;
                    Claim();
                    Scaffold.SpeechService.Say("Edge.", "Nav"); // natively a silent dead end
                    return false;
                }

                if (ReferenceEquals(surface, party))
                {
                    int col = Column(party);
                    if (dir < 0 && col <= 0)
                    {
                        SetSurface(sheet, activities);
                        Claim();
                        SetIndex(sheet, 0);
                        Snap(gui, sheet);
                        Pump.NoteZoneLabel("Activities"); // one utterance with the landing
                        Pump.NoteSelection(sheet); // composes "Name: Activity" via the slider path
                        return false;
                    }
                    if (dir > 0 && col >= Width(party, 5) - 1)
                    {
                        EnterPot(gui, sheet, pot, column: 0);
                        return false;
                    }
                    return true; // internal column move — native + segment patch
                }

                if (ReferenceEquals(surface, pot))
                {
                    int col = Column(pot);
                    if (dir < 0 && col <= 0)
                    {
                        EnterGrid(gui, sheet, party, column: Width(party, 5) - 1, "Party's Food");
                        return false;
                    }
                    if (dir > 0 && col >= Width(pot, 5) - 1)
                    {
                        // → Clear (single technical button; it self-labels
                        // through the selection join, crafting precedent).
                        SetSurface(sheet, buttons);
                        Claim();
                        SetIndex(sheet, 0);
                        Snap(gui, sheet);
                        Pump.NoteSelection(sheet);
                        return false;
                    }
                    return true;
                }

                if (ReferenceEquals(surface, buttons))
                {
                    if (dir < 0)
                    {
                        EnterPot(gui, sheet, pot, column: Width(pot, 5) - 1);
                        return false;
                    }
                    Claim();
                    Scaffold.SpeechService.Say("Edge.", "Nav");
                    return false;
                }
            }
            catch { }
            return true;
        }

        private static void EnterPot(object gui, object sheet, object pot, int column)
        {
            SetSurface(sheet, pot);
            Claim();
            try { Seams.InvSegment_column.SetValue(pot, column); } catch { }
            SetIndex(sheet, 0);
            Snap(gui, sheet);
            // The crossing carries the live meal readout — the game's own
            // label — riding the landed cell's line as one utterance.
            string meal = null;
            try
            {
                object label = Seams.InvSheetCamp_mealLabel?.GetValue(sheet);
                string raw = label != null ? Seams.UITextBlock_content?.GetValue(label) as string : null;
                if (!string.IsNullOrWhiteSpace(raw)) meal = TextCleaner.CleanText(raw);
            }
            catch { }
            Pump.NoteZoneLabel(string.IsNullOrWhiteSpace(meal)
                ? "Tonight's Meal" : $"Tonight's Meal. {meal}");
            Pump.NoteSelection(sheet);
        }

        private static void EnterGrid(object gui, object sheet, object grid, int column, string label)
        {
            SetSurface(sheet, grid);
            Claim();
            try { Seams.InvSegment_column.SetValue(grid, column); } catch { }
            SetIndex(sheet, 0);
            Snap(gui, sheet);
            Pump.NoteZoneLabel(label);
            Pump.NoteSelection(sheet);
        }

        private static bool CanFlip(object slider, MethodInfo probe)
        {
            try { return probe != null && (bool)probe.Invoke(slider, null); }
            catch { return false; }
        }

        private static void Claim() => _claimFrame = UnityEngine.Time.frameCount;

        private static void SetSurface(object sheet, object surface)
        {
            try { Seams.InvSheet_currentControllerSurface.SetValue(sheet, surface); } catch { }
        }

        private static void SetIndex(object sheet, int index)
        {
            try { Seams.UICanvas_setCurrentSelectedButton.Invoke(sheet, new object[] { index }); }
            catch { }
        }

        private static int Column(object segment)
        {
            try { return (int)Seams.InvSegment_column.GetValue(segment); }
            catch { return 0; }
        }

        private static int Width(object segment, int fallback)
        {
            try { return (int)Seams.InvSegment_gridWidth.GetValue(segment); }
            catch { return fallback; }
        }

        private static void Snap(object gui, object sheet)
        {
            try { Seams.GUIControl_setMouseToUIElement.Invoke(gui, new object[] { sheet, 8, -8 }); }
            catch { }
        }
    }
}
