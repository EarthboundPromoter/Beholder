using HarmonyLib;
using System.Collections.Generic;
using System.Reflection;
using System.Text.RegularExpressions;

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

        /// <summary>The pressed feat, its pre-handler rank, and its
        /// pre-handler achieved-tier snapshot (the pressed statics are set by
        /// updateMouseInteraction earlier this frame; null on the
        /// overwhelmingly common no-press frames).</summary>
        private static object[] CaptureRank(FieldInfo pressedField)
        {
            try
            {
                object feat = pressedField.GetValue(null);
                if (feat == null) return null;
                return new object[] { feat, Seams.Feat_getRank.Invoke(feat, null), TierSnapshot(feat) };
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
                if (after == before) return;
                Pump.NoteFeatRank(feat);
                // Tier crossings (owner ruling 2026-08-21): the game's own
                // achieved marker is the GREEN TAG it wraps around a tier
                // block once rank >= threshold (FeatContainer.cs:227-235) —
                // diff which blocks are green across the press and speak the
                // grants: "Unlocked Cleave." / "Lost Cleave." Queued behind
                // the rank line; the pool line trails after.
                var beforeTiers = state[2] as Dictionary<int, TierInfo>;
                var afterTiers = TierSnapshot(feat);
                if (beforeTiers == null || afterTiers == null) return;
                var lines = new List<string>();
                foreach (var kv in afterTiers)
                    if (kv.Value.Achieved
                        && (!beforeTiers.TryGetValue(kv.Key, out var was) || !was.Achieved))
                        lines.Add($"Unlocked {kv.Value.Grants ?? $"tier at {kv.Key} ranks"}.");
                foreach (var kv in beforeTiers)
                    if (kv.Value.Achieved
                        && (!afterTiers.TryGetValue(kv.Key, out var now) || !now.Achieved))
                        lines.Add($"Lost {kv.Value.Grants ?? $"tier at {kv.Key} ranks"}.");
                if (lines.Count > 0) Pump.NoteFeatTierLine(string.Join(" ", lines));
            }
            catch { }
        }

        private sealed class TierInfo
        {
            public bool Achieved;
            public string Grants;
        }

        /// <summary>Parse the feat's own rendered description into its tier
        /// blocks: "\n\n"-separated, each opening "N RANKS" with "~ <grant>"
        /// lines, the whole block green-wrapped when achieved
        /// (FeatContainer.cs:51-71, :213-238). The spell-list tail a tier may
        /// embed splits into its own fragment, which matches nothing here.
        /// (The description pane re-renders this same composed string every
        /// frame natively — the extra press-frame read is native-equivalent.)</summary>
        private static Dictionary<int, TierInfo> TierSnapshot(object feat)
        {
            try
            {
                string desc = Seams.SkaldBaseObject_getFullDescription?.Invoke(feat, null) as string;
                if (string.IsNullOrEmpty(desc)) return null;
                string green = Seams.TagValue(Seams.C64_GreenLightTag);
                var tiers = new Dictionary<int, TierInfo>();
                foreach (string rawBlock in desc.Split(new[] { "\n\n" }, System.StringSplitOptions.None))
                {
                    string block = rawBlock.TrimStart();
                    bool achieved = !string.IsNullOrEmpty(green)
                        && block.StartsWith(green, System.StringComparison.Ordinal);
                    string cleaned = TextCleaner.CleanText(block).TrimStart();
                    var m = Regex.Match(cleaned, @"^(\d+) RANKS\b");
                    if (!m.Success) continue;
                    int threshold = int.Parse(m.Groups[1].Value);
                    var names = new List<string>();
                    foreach (string line in cleaned.Split('\n'))
                    {
                        string t = line.Trim();
                        if (t.StartsWith("~ ")) names.Add(t.Substring(2).TrimEnd(':').Trim());
                    }
                    tiers[threshold] = new TierInfo
                    {
                        Achieved = achieved,
                        Grants = names.Count > 0 ? string.Join(", ", names) : null,
                    };
                }
                return tiers.Count > 0 ? tiers : null;
            }
            catch { return null; }
        }
    }
}
