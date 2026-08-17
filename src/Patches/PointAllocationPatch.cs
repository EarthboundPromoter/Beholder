using HarmonyLib;
using System.Reflection;

namespace SkaldAccessibility.Patches
{
    /// <summary>
    /// Character-creation point allocation speech (CC report build 2026-08-16).
    ///
    /// The three points-remaining counters render through bespoke text blocks
    /// that bypass every hooked content funnel (EditorSheetEntry.setPointValue
    /// renders a bare unlabeled integer; UIFeatTree.setPointsText renders the
    /// game's own "Ranks to Distribute: N"), and none of them is focusable —
    /// so these joins are the only route the values have to speech.
    ///
    /// Hooks (all note-only, drained by Pump.DrainPoints / the content diff):
    ///  - UIAttributeEditorSheet.setAttributePoints / setSkillPoints — the
    ///    labeled pool wrappers (one shared setPointValue body underneath
    ///    can't tell the two pools apart; the wrappers can). Fire every frame
    ///    with the settled value; the drain diffs.
    ///  - UIAttributeEditorSheet.get*PlusObject / get*MinusObject — the game's
    ///    own pressed-row accessors, polled once per frame by the owning
    ///    state; non-null exactly on a press frame, PRE-mutation. The drain
    ///    reads post-mutation truth and speaks success or the rejection edge.
    ///  - UIFeatTree.setPointsText — the self-labelling feat pool line
    ///    (covers the level-up FeatBuyState through the same seam).
    ///  - UIFeatTree.updatePressedLeftFeat / updatePressedRightFeat — rank
    ///    diffed across the game's own buy/refund handlers; only an actual
    ///    rank change notes (a first-press select or a rejected buy doesn't —
    ///    the rejection speaks through the game's own legality popup).
    /// Seam-gated (WP8).
    /// </summary>
    public static class PointAllocationPatch
    {
        // ---- Pool renders ----

        [HarmonyPatch]
        public static class AttributePoolHook
        {
            [HarmonyPrepare]
            static bool Prepare() => Seams.AttrSheet_setAttributePoints != null;

            [HarmonyTargetMethod]
            static MethodBase TargetMethod() => Seams.AttrSheet_setAttributePoints;

            [HarmonyPostfix]
            static void Postfix(int __0) => Pump.NotePointPool(isAttribute: true, __0);
        }

        [HarmonyPatch]
        public static class SkillPoolHook
        {
            [HarmonyPrepare]
            static bool Prepare() => Seams.AttrSheet_setSkillPoints != null;

            [HarmonyTargetMethod]
            static MethodBase TargetMethod() => Seams.AttrSheet_setSkillPoints;

            [HarmonyPostfix]
            static void Postfix(int __0) => Pump.NotePointPool(isAttribute: false, __0);
        }

        // ---- Plus/minus presses ----

        [HarmonyPatch]
        public static class AttributePlusPressHook
        {
            [HarmonyPrepare]
            static bool Prepare() => Seams.AttrSheet_getAttributePlusObject != null;

            [HarmonyTargetMethod]
            static MethodBase TargetMethod() => Seams.AttrSheet_getAttributePlusObject;

            [HarmonyPostfix]
            static void Postfix(object __result)
            {
                if (__result != null) Pump.NotePointPress(isAttribute: true, isPlus: true, __result);
            }
        }

        [HarmonyPatch]
        public static class AttributeMinusPressHook
        {
            [HarmonyPrepare]
            static bool Prepare() => Seams.AttrSheet_getAttributeMinusObject != null;

            [HarmonyTargetMethod]
            static MethodBase TargetMethod() => Seams.AttrSheet_getAttributeMinusObject;

            [HarmonyPostfix]
            static void Postfix(object __result)
            {
                if (__result != null) Pump.NotePointPress(isAttribute: true, isPlus: false, __result);
            }
        }

        [HarmonyPatch]
        public static class SkillPlusPressHook
        {
            [HarmonyPrepare]
            static bool Prepare() => Seams.AttrSheet_getSkillPlusObject != null;

            [HarmonyTargetMethod]
            static MethodBase TargetMethod() => Seams.AttrSheet_getSkillPlusObject;

            [HarmonyPostfix]
            static void Postfix(object __result)
            {
                if (__result != null) Pump.NotePointPress(isAttribute: false, isPlus: true, __result);
            }
        }

        [HarmonyPatch]
        public static class SkillMinusPressHook
        {
            [HarmonyPrepare]
            static bool Prepare() => Seams.AttrSheet_getSkillMinusObject != null;

            [HarmonyTargetMethod]
            static MethodBase TargetMethod() => Seams.AttrSheet_getSkillMinusObject;

            [HarmonyPostfix]
            static void Postfix(object __result)
            {
                if (__result != null) Pump.NotePointPress(isAttribute: false, isPlus: false, __result);
            }
        }

        // ---- Feat pool + rank changes ----

        [HarmonyPatch]
        public static class FeatPointsTextHook
        {
            [HarmonyPrepare]
            static bool Prepare() => Seams.UIFeatTree_setPointsText != null;

            [HarmonyTargetMethod]
            static MethodBase TargetMethod() => Seams.UIFeatTree_setPointsText;

            [HarmonyPostfix]
            static void Postfix(string __0)
            {
                Pump.NoteContent("FeatPoints", __0, interrupt: false);
            }
        }

        [HarmonyPatch]
        public static class FeatBuyHook
        {
            [HarmonyPrepare]
            static bool Prepare() =>
                Seams.UIFeatTree_updatePressedLeftFeat != null
                && Seams.UIFeatTree_pressedLeftFeat != null
                && Seams.Feat_getRank != null;

            [HarmonyTargetMethod]
            static MethodBase TargetMethod() => Seams.UIFeatTree_updatePressedLeftFeat;

            [HarmonyPrefix]
            static void Prefix(out object[] __state)
                => __state = CaptureRank(Seams.UIFeatTree_pressedLeftFeat);

            [HarmonyPostfix]
            static void Postfix(object[] __state) => NoteIfRankChanged(__state);
        }

        [HarmonyPatch]
        public static class FeatRefundHook
        {
            [HarmonyPrepare]
            static bool Prepare() =>
                Seams.UIFeatTree_updatePressedRightFeat != null
                && Seams.UIFeatTree_pressedRightFeat != null
                && Seams.Feat_getRank != null;

            [HarmonyTargetMethod]
            static MethodBase TargetMethod() => Seams.UIFeatTree_updatePressedRightFeat;

            [HarmonyPrefix]
            static void Prefix(out object[] __state)
            {
                _inDirectRefund = true;
                __state = CaptureRank(Seams.UIFeatTree_pressedRightFeat);
            }

            [HarmonyPostfix]
            static void Postfix(object[] __state)
            {
                _inDirectRefund = false;
                NoteIfRankChanged(__state);
            }

            /// <summary>Runs even when the original throws — the flag can
            /// never stick and silence the cascade hook (review F5).</summary>
            [HarmonyFinalizer]
            static void Finalizer() => _inDirectRefund = false;
        }

        /// <summary>True while the game's own right-click refund handler runs —
        /// its subtract is voiced by the rank line above, not the cascade line.</summary>
        private static bool _inDirectRefund;

        /// <summary>Cascade refunds (owner ruling 2026-08-17): dropping a root
        /// feat makes its dependents illegal, and updateFeatsLegality silently
        /// drains their staged ranks via subtractPossibleRank. Note every
        /// successful subtract outside the direct-refund handler; the drain
        /// composes "Point removed from X." with the remaining-points line
        /// trailing through the FeatPoints source.</summary>
        [HarmonyPatch]
        public static class FeatCascadeRefundHook
        {
            [HarmonyPrepare]
            static bool Prepare() => Seams.Feat_subtractPossibleRank != null;

            [HarmonyTargetMethod]
            static MethodBase TargetMethod() => Seams.Feat_subtractPossibleRank;

            [HarmonyPostfix]
            static void Postfix(object __instance, int __result)
            {
                if (__result > 0 && !_inDirectRefund) Pump.NoteFeatRefund(__instance);
            }
        }

        /// <summary>The pressed feat and its pre-handler rank (the pressed
        /// statics are set by updateMouseInteraction earlier this frame; null
        /// on the overwhelmingly common no-press frames).</summary>
        private static object[] CaptureRank(FieldInfo pressedField)
        {
            try
            {
                object feat = pressedField.GetValue(null);
                if (feat == null) return null;
                return new[] { feat, Seams.Feat_getRank.Invoke(feat, null) };
            }
            catch { return null; }
        }

        private static void NoteIfRankChanged(object[] state)
        {
            if (state == null) return;
            try
            {
                object feat = state[0];
                int before = (int)state[1];
                int after = (int)Seams.Feat_getRank.Invoke(feat, null);
                if (after != before) Pump.NoteFeatRank(feat);
            }
            catch { }
        }
    }
}
