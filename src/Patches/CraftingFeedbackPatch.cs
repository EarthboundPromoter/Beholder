using HarmonyLib;
using System.Reflection;

namespace SkaldAccessibility.Patches
{
    /// <summary>
    /// Ingredient-transfer and craft-outcome feedback (owner rulings
    /// 2026-08-19; ground truth in the crafting survey). The game never
    /// reads bench or pot contents back to any surface: a blind Z moved a
    /// whole stack with zero feedback, recipe validity is an EXACT-SET test
    /// (one stray item fails every recipe — and the bench is the map tile's
    /// inventory, so anything already lying there counts), and the failure
    /// line is the same flat string regardless of cause.
    ///
    /// Everything spoken here is composed from game-authored strings:
    /// Inventory.printCountList ("Name (n)" per distinct name),
    /// Item.getNameAndAmount, CraftingControl.getCurrentRecipeFullDescription
    /// (whose "N x Title" ingredient lines this parses), and
    /// ItemFood.getFoodValue.
    ///
    ///  - Manual bench moves speak the stack and the selected recipe's bench
    ///    progress: "Rowan Leaf (3), to workstation. 2 of 3 ingredients for
    ///    Lesser Tonic."
    ///  - Every bench mutation (manual, recipe-click bulk fill, Clear, exit)
    ///    settles to a diffed census line: "Workstation: Rowan Leaf (3),
    ///    Spring Water (1)." / "Workstation empty."
    ///  - A failed craft (CraftingControl.craft returns null) queues a
    ///    diagnosis behind the game's own popup line: what the bench holds,
    ///    which ingredient counts are short, and which bench items the
    ///    selected recipe does not use.
    ///  - Camp pot moves are single items and speak their food value:
    ///    "Dried Meat, to the pot. 4 food." — the meal-label diff line
    ///    ("Food 14 of 30. Partial recovery.") follows through CampZonePatch.
    /// Seam-gated (WP8).
    /// </summary>
    public static class CraftingFeedbackPatch
    {
        // ---- Manual bench transfers (whole stacks) ----

        [HarmonyPatch]
        public static class BenchAddHook
        {
            [HarmonyPrepare]
            static bool Prepare() => Seams.CraftingState_interactMain != null
                && Seams.InvBaseState_getMainInventory != null;

            [HarmonyTargetMethod]
            static MethodBase TargetMethod() => Seams.CraftingState_interactMain;

            [HarmonyPrefix]
            static void Prefix(object __instance, ref string __state)
                => __state = CurrentItemLabel(__instance, Seams.InvBaseState_getMainInventory);

            [HarmonyPostfix]
            static void Postfix(object __instance, string __state)
                => SpeakBenchMove(__instance, __state, toBench: true);
        }

        [HarmonyPatch]
        public static class BenchReturnHook
        {
            [HarmonyPrepare]
            static bool Prepare() => Seams.CraftingState_interactSecondary != null
                && Seams.InvBaseState_getSecondaryInventory != null;

            [HarmonyTargetMethod]
            static MethodBase TargetMethod() => Seams.CraftingState_interactSecondary;

            [HarmonyPrefix]
            static void Prefix(object __instance, ref string __state)
                => __state = CurrentItemLabel(__instance, Seams.InvBaseState_getSecondaryInventory);

            [HarmonyPostfix]
            static void Postfix(object __instance, string __state)
                => SpeakBenchMove(__instance, __state, toBench: false);
        }

        // ---- Bulk fill (recipe click) and clears settle to the census ----

        [HarmonyPatch]
        public static class RecipeFillHook
        {
            [HarmonyPrepare]
            static bool Prepare() => Seams.CraftingControl_transferItemsFromPartyToWorkbench != null;

            [HarmonyTargetMethod]
            static MethodBase TargetMethod() => Seams.CraftingControl_transferItemsFromPartyToWorkbench;

            [HarmonyPostfix]
            static void Postfix(object __0)
            {
                NoteBenchCensus(__0);
                string progress = RecipeProgress(__0);
                if (progress != null) Scaffold.SpeechService.SayQueued(progress + ".", "Nav");
            }
        }

        [HarmonyPatch]
        public static class BenchClearHook
        {
            [HarmonyPrepare]
            static bool Prepare() => Seams.CraftingState_clearWorkbench != null
                && Seams.CraftingState_workbenchInventory != null;

            [HarmonyTargetMethod]
            static MethodBase TargetMethod() => Seams.CraftingState_clearWorkbench;

            [HarmonyPostfix]
            static void Postfix(object __instance)
            {
                // The recipe-click path clears then refills in the same frame;
                // the census note is latest-wins per frame, so only the
                // settled truth speaks.
                try { NoteBenchCensus(Seams.CraftingState_workbenchInventory.GetValue(__instance)); }
                catch { }
            }
        }

        // ---- The failure diagnosis (craft returns null = no recipe matched) ----

        [HarmonyPatch]
        public static class CraftOutcomeHook
        {
            [HarmonyPrepare]
            static bool Prepare() => Seams.CraftingControl_craft != null
                && Seams.Inventory_printCountList != null;

            [HarmonyTargetMethod]
            static MethodBase TargetMethod() => Seams.CraftingControl_craft;

            [HarmonyPostfix]
            static void Postfix(object __0, object __1, object __result)
            {
                if (__result != null) return; // success — the game's own yield feedback owns it
                try
                {
                    string line = ComposeFailureDiagnosis(__0, __1);
                    if (line != null) Scaffold.SpeechService.SayQueued(line, "Nav");
                }
                catch { }
            }
        }

        // ---- Camp pot transfers (single items, food value spoken) ----

        [HarmonyPatch]
        public static class PotAddHook
        {
            [HarmonyPrepare]
            static bool Prepare() => Seams.CampState_interactMain != null
                && Seams.InvBaseState_getMainInventory != null;

            [HarmonyTargetMethod]
            static MethodBase TargetMethod() => Seams.CampState_interactMain;

            [HarmonyPrefix]
            static void Prefix(object __instance, ref string __state)
                => __state = CurrentFoodLabel(__instance, Seams.InvBaseState_getMainInventory);

            [HarmonyPostfix]
            static void Postfix(string __state)
            {
                if (__state != null)
                    Scaffold.SpeechService.Say($"{__state}, to the pot.", "Nav");
            }
        }

        [HarmonyPatch]
        public static class PotRemoveHook
        {
            [HarmonyPrepare]
            static bool Prepare() => Seams.CampState_interactSecondary != null
                && Seams.InvBaseState_getSecondaryInventory != null;

            [HarmonyTargetMethod]
            static MethodBase TargetMethod() => Seams.CampState_interactSecondary;

            [HarmonyPrefix]
            static void Prefix(object __instance, ref string __state)
                => __state = CurrentFoodLabel(__instance, Seams.InvBaseState_getSecondaryInventory);

            [HarmonyPostfix]
            static void Postfix(string __state)
            {
                if (__state != null)
                    Scaffold.SpeechService.Say($"{__state}, out of the pot.", "Nav");
            }
        }

        // ---- Composition helpers ----

        /// <summary>"Name (n)" of the source inventory's current item (the
        /// whole stack moves in crafting). Null when nothing is current.</summary>
        private static string CurrentItemLabel(object state, MethodInfo inventoryGetter)
        {
            try
            {
                object inv = inventoryGetter.Invoke(state, null);
                if (inv == null || Seams.Inventory_getCurrentItemNameAndAmount == null) return null;
                string raw = Seams.Inventory_getCurrentItemNameAndAmount.Invoke(inv, null) as string;
                string cleaned = TextCleaner.CleanText(raw ?? "");
                return string.IsNullOrWhiteSpace(cleaned) ? null : cleaned;
            }
            catch { return null; }
        }

        /// <summary>Bare name plus food value for the camp pot's single-item
        /// moves: "Dried Meat. 4 food" (value from the game's own getter).</summary>
        private static string CurrentFoodLabel(object state, MethodInfo inventoryGetter)
        {
            try
            {
                object inv = inventoryGetter.Invoke(state, null);
                object item = inv != null && Seams.Inventory_getCurrentObject != null
                    ? Seams.Inventory_getCurrentObject.Invoke(inv, null) : null;
                if (item == null) return null;
                string name = TextCleaner.CleanText(
                    Seams.SkaldBaseObject_getName?.Invoke(item, null) as string ?? "");
                if (string.IsNullOrWhiteSpace(name)) return null;
                if (Seams.ItemFoodType != null && Seams.ItemFoodType.IsInstanceOfType(item)
                    && Seams.ItemFood_getFoodValue != null)
                {
                    int v = (int)Seams.ItemFood_getFoodValue.Invoke(item, null);
                    return $"{name}, {v} food";
                }
                return name;
            }
            catch { return null; }
        }

        private static void SpeakBenchMove(object state, string label, bool toBench)
        {
            try
            {
                object bench = Seams.CraftingState_workbenchInventory?.GetValue(state);
                NoteBenchCensus(bench);
                if (label == null) return; // nothing was current — nothing moved
                string line = toBench ? $"{label}, to workstation." : $"{label}, back to party.";
                Scaffold.SpeechService.Say(line, "Nav");
                string progress = RecipeProgress(bench);
                if (progress != null) Scaffold.SpeechService.SayQueued(progress + ".", "Nav");
            }
            catch { }
        }

        /// <summary>The diffed what's-there-at-all line, settled at the drain:
        /// "Workstation: Rowan Leaf (3), Spring Water (1)." / "Workstation
        /// empty." Latest-wins per frame absorbs clear-then-refill.</summary>
        private static void NoteBenchCensus(object bench)
        {
            try
            {
                var items = BenchContents(bench);
                Pump.NoteContent("Workbench",
                    items.Count == 0 ? "Workstation empty."
                        : "Workstation: " + string.Join(", ", items.ToArray()) + ".",
                    interrupt: false);
            }
            catch { }
        }

        /// <summary>Cleaned "Name (n)" lines from the game's own
        /// printCountList; empty list when the inventory is empty.</summary>
        private static System.Collections.Generic.List<string> BenchContents(object inventory)
        {
            var list = new System.Collections.Generic.List<string>();
            if (inventory == null || Seams.Inventory_printCountList == null) return list;
            string raw = Seams.Inventory_printCountList.Invoke(inventory, null) as string;
            if (string.IsNullOrWhiteSpace(raw)) return list;
            foreach (string line in raw.Split('\n'))
            {
                string cleaned = TextCleaner.CleanText(line).Trim();
                if (string.IsNullOrWhiteSpace(cleaned)) continue;
                if (cleaned.Equals("Empty", System.StringComparison.OrdinalIgnoreCase)) continue;
                list.Add(cleaned);
            }
            return list;
        }

        /// <summary>"N of M ingredients for <Recipe>" — bench-side counts
        /// against the SELECTED recipe's own "N x Title" ingredient lines.
        /// Null when no recipe is selected or the block doesn't parse.</summary>
        private static string RecipeProgress(object bench)
        {
            try
            {
                var recipe = ParseSelectedRecipe(bench, out var ingredients);
                if (recipe == null || ingredients.Count == 0) return null;
                var have = BenchCounts(bench);
                int met = 0;
                foreach (var ing in ingredients)
                    if (have.TryGetValue(ing.Key, out int n) && n >= ing.Value) met++;
                return $"{met} of {ingredients.Count} ingredients for {recipe}";
            }
            catch { return null; }
        }

        /// <summary>The failure story, from game strings: contents, shortfalls
        /// against the selected recipe, and bench items the recipe doesn't
        /// use (the exact-set rule the game never explains).</summary>
        private static string ComposeFailureDiagnosis(object bench, object pc)
        {
            var contents = BenchContents(bench);
            string holds = contents.Count == 0 ? "Workstation empty."
                : "Workstation holds " + string.Join(", ", contents.ToArray()) + ".";

            string recipe = ParseSelectedRecipe(bench, out var ingredients);
            if (recipe == null || ingredients.Count == 0) return holds;

            var have = BenchCounts(bench);
            var parts = new System.Collections.Generic.List<string> { holds };
            foreach (var ing in ingredients)
            {
                have.TryGetValue(ing.Key, out int n);
                if (n < ing.Value)
                    parts.Add($"{recipe} needs {ing.Value} x {ing.Key}.");
            }
            foreach (var kv in have)
                if (!ingredients.ContainsKey(kv.Key))
                    parts.Add($"{kv.Key} is not in this recipe.");
            return string.Join(" ", parts.ToArray());
        }

        /// <summary>Bench contents as name → count (parsed from the census
        /// lines' "Name (n)"; bare lines count 1).</summary>
        private static System.Collections.Generic.Dictionary<string, int> BenchCounts(object bench)
        {
            var dict = new System.Collections.Generic.Dictionary<string, int>();
            foreach (string entry in BenchContents(bench))
            {
                var m = System.Text.RegularExpressions.Regex.Match(entry, @"^(.*?)\s*\((\d+)\)$");
                if (m.Success) dict[m.Groups[1].Value.Trim()] = int.Parse(m.Groups[2].Value);
                else dict[entry] = 1;
            }
            return dict;
        }

        /// <summary>The selected recipe's name and its "N x Title" ingredient
        /// lines, parsed from the game's own printRecipe block (title-cased
        /// item names match printCountList's names).</summary>
        private static string ParseSelectedRecipe(object bench,
            out System.Collections.Generic.Dictionary<string, int> ingredients)
        {
            ingredients = new System.Collections.Generic.Dictionary<string, int>();
            try
            {
                if (Seams.DataControl_getCraftingControl == null
                    || Seams.CraftingControl_getCurrentRecipeFullDescription == null
                    || Seams.MainControl_getDataControl == null
                    || Seams.DataControl_getCurrentPC == null) return null;
                object dc = Seams.MainControl_getDataControl.Invoke(null, null);
                object cc = dc != null ? Seams.DataControl_getCraftingControl.Invoke(dc, null) : null;
                object pc = dc != null ? Seams.DataControl_getCurrentPC.Invoke(dc, null) : null;
                if (cc == null || pc == null) return null;
                string block = Seams.CraftingControl_getCurrentRecipeFullDescription
                    .Invoke(cc, new object[] { bench, pc }) as string;
                if (string.IsNullOrWhiteSpace(block)) return null;

                string name = null;
                foreach (string line in block.Split('\n'))
                {
                    string cleaned = TextCleaner.CleanText(line).Trim();
                    if (string.IsNullOrWhiteSpace(cleaned)) continue;
                    if (name == null)
                    {
                        // First non-empty line is the "<Name> RECIPE" header.
                        name = cleaned.EndsWith(" RECIPE", System.StringComparison.OrdinalIgnoreCase)
                            ? cleaned.Substring(0, cleaned.Length - 7).Trim() : cleaned;
                        continue;
                    }
                    var m = System.Text.RegularExpressions.Regex.Match(cleaned, @"^(\d+)\s*x\s*(.+)$");
                    if (m.Success)
                        ingredients[m.Groups[2].Value.Trim()] = int.Parse(m.Groups[1].Value);
                }
                return string.IsNullOrWhiteSpace(name) ? null : name;
            }
            catch { return null; }
        }
    }
}
