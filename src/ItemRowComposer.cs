using System;
using System.Collections;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace SkaldAccessibility
{
    /// <summary>
    /// R11 full-composition item rows (table-ui-design §6.8/§6.10, gate D
    /// 2026-08-21). One row carries ALL the item's data in decision-weight
    /// order: identity + count + glyph transcodes → (trade: the offer facet)
    /// → type string → the stat core with R14 inline comparisons → value,
    /// weight → description prose.
    ///
    /// The stat core PARSES the game's own printComparativeStats(currentPC)
    /// block — the exact text the panel and right-click tooltip render, one
    /// parser over all nine stat-bearing subclasses (survey 2026-08-21).
    /// Identity comes from getName()/getCount() primitives (the block's
    /// header is uppercased) and prose from getDescription() directly (pure —
    /// no script re-execution; also sidesteps the consumable-effect tail's
    /// ambiguous parse boundary). Label/value joins are TAB
    /// (TextTools.formateNameValuePair — the gate-C finding); the verdict is
    /// which color tag opens the value, compared against the game's own
    /// GREEN_LIGHT_TAG/RED_LIGHT_TAG read live; the versus clause is
    /// "(Vs: x)" everywhere except the weapon/ammo damage line's "(Vs x)" —
    /// both accepted. Signs stay raw — the shipped R17 transcode at the
    /// speech choke turns them into words.
    /// </summary>
    internal static class ItemRowComposer
    {
        internal enum Mode { Plain, TradeParty, TradeMerchant }

        /// <summary>The row's ordered parts (identity first, prose last).
        /// Null when the item is unreadable. Join with spaces for the row
        /// line; each part is one lateral facet.</summary>
        internal static List<string> RowParts(object item, Mode mode)
        {
            if (item == null) return null;
            string identity = Identity(item, out bool isMoney, out int count);
            if (identity == null) return null;
            var parts = new List<string> { identity };

            if (mode != Mode.Plain)
            {
                string offer = OfferFacet(item, mode);
                if (offer != null) parts.Add(offer);
            }

            if (!isMoney)
            {
                AppendStatFacets(item, count, parts);
                string prose = Prose(item);
                if (prose != null) parts.Add(prose);
            }
            return parts;
        }

        // =====================================================================
        // Identity + glyphs
        // =====================================================================

        /// <summary>"Waraxe, 2, new, enchanted, can't equip." — name from
        /// getName() (already carries the enchantment affix), count on stacks,
        /// glyph transcodes in render order (count digit, new badge, magic
        /// outline, legality overlay — Item.cs:241-287, UIGridBase.cs:167-170).
        /// Money (a real rendered cell) is identity+count only — its value and
        /// weight getters are null-backed zeros.</summary>
        private static string Identity(object item, out bool isMoney, out int count)
        {
            isMoney = false;
            count = 1;
            string name = null;
            try { name = Seams.SkaldBaseObject_getName?.Invoke(item, null) as string; } catch { }
            if (string.IsNullOrWhiteSpace(name)) return null;
            name = Patches.TextCleaner.CleanText(name).Trim();
            if (name.Length == 0) return null;

            try { if (Seams.Item_getCount != null) count = (int)Seams.Item_getCount.Invoke(item, null); }
            catch { }
            isMoney = Seams.ItemMoneyType != null && Seams.ItemMoneyType.IsInstanceOfType(item);

            var tags = new List<string>();
            if (count > 1) tags.Add(count.ToString());
            if (!isMoney)
            {
                try
                {
                    if (Seams.Item_isNewAddition != null
                        && (bool)Seams.Item_isNewAddition.Invoke(item, null)) tags.Add("new");
                }
                catch { }
                try
                {
                    if (Seams.Item_isMagical != null
                        && (bool)Seams.Item_isMagical.Invoke(item, null)) tags.Add("enchanted");
                }
                catch { }
                if (CannotEquip(item)) tags.Add("can't equip");
            }
            return tags.Count == 0 ? $"{name}." : $"{name}, {string.Join(", ", tags)}.";
        }

        /// <summary>The can't-equip overlay's flag, read pure (addPopup:
        /// false). Only weapons, armor and tomes can ever be illegal
        /// (Character.cs:4088-4120) — everything else answers success.</summary>
        private static bool CannotEquip(object item)
        {
            try
            {
                object pc = CurrentPC();
                if (pc == null || Seams.Character_isItemLegalToEquip == null
                    || Seams.SkaldActionResult_wasSuccess == null) return false;
                object result = Seams.Character_isItemLegalToEquip.Invoke(pc, new[] { item, (object)false });
                if (result == null) return false;
                return !(bool)Seams.SkaldActionResult_wasSuccess.Invoke(result, null);
            }
            catch { return false; }
        }

        // =====================================================================
        // Trade offer facet (§6.10 — leads the row after identity)
        // =====================================================================

        /// <summary>"Sells for 3 gold." / "Stolen, vendor won't buy." /
        /// "Can't be traded." / "Costs 14 gold." Predicates and prices are the
        /// game's own (Store.cs:278-338); the price calls are private —
        /// invoke-only reflection per the §9 budget. Wording = the walked
        /// §6.10 drafts; refusal wording is the standing open calibration.</summary>
        private static string OfferFacet(object item, Mode mode)
        {
            try
            {
                object store = CurrentStore();
                if (store == null) return null;
                bool tradeable = Seams.Item_canBeTraded != null
                    && (bool)Seams.Item_canBeTraded.Invoke(item, null);
                if (!tradeable) return "Can't be traded.";

                if (mode == Mode.TradeParty)
                {
                    bool stolen = Seams.Item_isStolen != null
                        && (bool)Seams.Item_isStolen.Invoke(item, null);
                    bool fence = Seams.Store_fence != null
                        && (bool)Seams.Store_fence.GetValue(store);
                    if (stolen && !fence) return "Stolen, vendor won't buy.";
                    if (Seams.Store_getSalePrice == null) return null;
                    int sale = (int)Seams.Store_getSalePrice.Invoke(store, new[] { item });
                    return $"Sells for {sale} gold.";
                }

                if (Seams.Store_getBuyPrice == null) return null;
                int buy = (int)Seams.Store_getBuyPrice.Invoke(store, new[] { item });
                return $"Costs {buy} gold.";
            }
            catch { return null; }
        }

        // =====================================================================
        // The stat core — parsing the game's own comparative block
        // =====================================================================

        /// <summary>Append the type facet, the stat facets (verdict-first per
        /// stat, R14/§4b: "Damage, better, 2 to 7, versus 1 to 6.") and the
        /// combined value/weight facet ("12 gold each, 8 pounds." — value is
        /// per-unit, weight per-stack, survey 2026-08-21).</summary>
        private static void AppendStatFacets(object item, int count, List<string> parts)
        {
            string block = null;
            try
            {
                block = Seams.Item_printComparativeStats?.Invoke(item, new[] { CurrentPC() }) as string;
            }
            catch { }
            if (string.IsNullOrWhiteSpace(block)) return;

            string green = Seams.TagValue(Seams.C64_GreenLightTag);
            string red = Seams.TagValue(Seams.C64_RedLightTag);

            bool headerSkipped = false;
            string value = null, weight = null;
            foreach (string rawLine in block.Split('\n'))
            {
                string cleanedLine = Patches.TextCleaner.CleanText(rawLine).Trim();
                if (cleanedLine.Length == 0) continue;
                if (!headerSkipped) { headerSkipped = true; continue; } // uppercased name header

                if (cleanedLine.StartsWith("["))
                {
                    string type = TypeFacet(cleanedLine);
                    if (type != null) parts.Add(type);
                    continue;
                }

                int tab = rawLine.IndexOf('\t');
                if (tab < 0) continue; // prose tail reached (harvest stops; prose comes from getDescription)

                string label = Patches.TextCleaner.CleanText(rawLine.Substring(0, tab)).Trim().TrimEnd('.', ':');
                string rawValue = rawLine.Substring(tab + 1);

                if (label.Equals("Value", StringComparison.OrdinalIgnoreCase))
                {
                    value = ValueFacet(rawValue, count);
                    continue;
                }
                if (label.Equals("Weight", StringComparison.OrdinalIgnoreCase))
                {
                    weight = WeightFacet(rawValue);
                    break; // Value/Weight are always the block's final labels (Item.cs:730-733)
                }
                string stat = StatFacet(label, rawValue, green, red);
                if (stat != null) parts.Add(stat);
            }

            if (value != null && weight != null) parts.Add($"{value}, {weight}.");
            else if (value != null) parts.Add($"{value}.");
            else if (weight != null) parts.Add($"{weight}.");
        }

        /// <summary>"[Melee, Medium Axe] [Enchanted]" → "Melee, Medium Axe."
        /// — the Enchanted badge is dropped (the identity facet already
        /// carries it).</summary>
        private static string TypeFacet(string cleanedLine)
        {
            var groups = new List<string>();
            foreach (Match m in Regex.Matches(cleanedLine, @"\[([^\]]+)\]"))
            {
                string g = m.Groups[1].Value.Trim();
                if (g.Length == 0 || g.Equals("Enchanted", StringComparison.OrdinalIgnoreCase)) continue;
                groups.Add(g);
            }
            return groups.Count == 0 ? null : $"{string.Join(", ", groups)}.";
        }

        /// <summary>One stat line → "Label, verdict, value, versus other." —
        /// verdict from the game's own color tag on the RAW value (checked
        /// before cleaning strips the markup), versus from either "(Vs: x)"
        /// or the damage line's "(Vs x)". Equal values read plain, no clause
        /// (§4b). Labels expand the game's truncations; damage ranges speak
        /// "2 to 7"; crit's "x3" speaks "times 3". Signs stay raw for the R17
        /// choke.</summary>
        private static string StatFacet(string label, string rawValue, string green, string red)
        {
            // The verdict tag is NEVER first in the value: the game's
            // formateNameValuePair wraps every value in the white
            // ATTRIBUTE_VALUE_TAG before the caller's own green/red tag
            // (TextTools.cs:36-39) — an anchored StartsWith never matches
            // (adversarial review gate D MUST-FIX, the gate-C dead-code
            // class again). Search for the earliest verdict tag instead;
            // the versus tail is plain text, so no false positive exists.
            string verdict = null;
            string trimmed = rawValue.TrimStart();
            int gi = string.IsNullOrEmpty(green) ? -1 : trimmed.IndexOf(green, StringComparison.Ordinal);
            int ri = string.IsNullOrEmpty(red) ? -1 : trimmed.IndexOf(red, StringComparison.Ordinal);
            if (gi >= 0 && (ri < 0 || gi < ri)) verdict = "better";
            else if (ri >= 0 && (gi < 0 || ri < gi)) verdict = "worse";

            string cleaned = Patches.TextCleaner.CleanText(rawValue).Trim();
            if (cleaned.Length == 0) return null;

            string versus = null;
            var vs = Regex.Match(cleaned, @"\(Vs:?\s*([^)]*)\)");
            if (vs.Success)
            {
                versus = vs.Groups[1].Value.Trim();
                cleaned = cleaned.Remove(vs.Index, vs.Length).Trim();
            }

            string labelSpoken = ExpandLabel(label);
            string valueSpoken = SpeakStatValue(cleaned);
            var bits = new List<string> { labelSpoken };
            if (verdict != null) bits.Add(verdict);
            bits.Add(valueSpoken);
            if (!string.IsNullOrEmpty(versus)) bits.Add($"versus {SpeakStatValue(versus)}");
            return string.Join(", ", bits) + ".";
        }

        private static string ExpandLabel(string label)
        {
            if (label.Equals("Enc", StringComparison.OrdinalIgnoreCase)) return "Encumbrance";
            if (label.Equals("Food Val", StringComparison.OrdinalIgnoreCase)) return "Food value";
            if (label.Equals("Crit", StringComparison.OrdinalIgnoreCase)) return "Crit";
            return label;
        }

        /// <summary>"2-7" → "2 to 7" (the calibrated damage-range wording,
        /// §4b); "x3" → "times 3" (the crit multiplier's rendered prefix).</summary>
        private static string SpeakStatValue(string v)
        {
            v = Regex.Replace(v, @"(\d)\s*-\s*(\d)", "$1 to $2");
            v = Regex.Replace(v, @"(?<![A-Za-z0-9])x(\d)", "times $1");
            return v;
        }

        /// <summary>"12 GP" → "12 gold" / "12 gold each" on stacks (value is
        /// per-unit — survey 2026-08-21).</summary>
        private static string ValueFacet(string rawValue, int count)
        {
            string v = Patches.TextCleaner.CleanText(rawValue).Trim();
            v = Regex.Replace(v, @"\s*GP\b", " gold");
            v = v.Trim();
            if (v.Length == 0) return null;
            return count > 1 ? $"{v} each" : v;
        }

        /// <summary>"8.00 lbs" → "8 pounds"; "0.10 lb" → "0.1 pounds" (weight
        /// is per-stack — spoken as rendered).</summary>
        private static string WeightFacet(string rawValue)
        {
            string v = Patches.TextCleaner.CleanText(rawValue).Trim();
            v = Regex.Replace(v, @"\s*lbs?\b", "").Trim();
            v = TrimNumber(v);
            return v.Length == 0 ? null : $"{v} pounds";
        }

        private static string TrimNumber(string s)
        {
            if (s.Contains("."))
            {
                s = s.TrimEnd('0');
                s = s.TrimEnd('.');
            }
            return s;
        }

        // =====================================================================
        // Prose
        // =====================================================================

        /// <summary>The item's whole prose, direct from getDescription() —
        /// pure (no processString anywhere in the item chain, survey
        /// 2026-08-21), separable from the stat block by construction.</summary>
        private static string Prose(object item)
        {
            try
            {
                string desc = Seams.Item_getDescription?.Invoke(item, null) as string;
                if (string.IsNullOrWhiteSpace(desc)) return null;
                string cleaned = Patches.TextCleaner.CleanText(desc.Replace("\n", " ")).Trim();
                if (cleaned.Length == 0) return null;
                return cleaned.EndsWith(".") || cleaned.EndsWith("!") || cleaned.EndsWith("?")
                    ? cleaned : cleaned + ".";
            }
            catch { return null; }
        }

        // =====================================================================
        // Plumbing
        // =====================================================================

        internal static object DC()
        {
            try { return Seams.MainControl_getDataControl?.Invoke(null, null); }
            catch { return null; }
        }

        internal static object CurrentPC()
        {
            try
            {
                object dc = DC();
                return dc == null ? null : Seams.DataControl_getCurrentPC?.Invoke(dc, null);
            }
            catch { return null; }
        }

        internal static object CurrentStore()
        {
            try
            {
                object dc = DC();
                return dc == null ? null : Seams.DataControl_currentStore?.GetValue(dc);
            }
            catch { return null; }
        }
    }
}
