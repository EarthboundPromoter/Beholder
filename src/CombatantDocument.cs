using System;
using System.Collections.Generic;

namespace SkaldAccessibility
{
    /// <summary>
    /// Combat Layer 1 (table-ui-design §6.18): the combatant landing payload
    /// and the staged drilldown document.
    ///
    /// The 2026-08-21 unit-panel survey's headline: the game has NO
    /// hover-driven unit panel — combatant facts render textually in exactly
    /// two places, the target's four-stat panel block and the right-click
    /// inspect tooltip (Character.getInspectDescription, the full block).
    /// So:
    ///  - The LANDING PAYLOAD speaks the owner-ruled top-level slice — name
    ///    (the tile label), vitality, wounds, moves/attacks, condition count —
    ///    from the same public getters the inspect block renders
    ///    (render-first; moves/attacks are the block's own "Move/Atc"
    ///    REMAINING values — the game renders remaining for everyone, not
    ///    static caps). PCs add the portrait-bar transcodes: "X of Y
    ///    vitality" and attunement (the bars render the ratios; the overland
    ///    PC-cycle ruling already speaks them as numbers).
    ///  - The DOCUMENT is the game's own inspect render PARSED AND RE-SORTED
    ///    into the ruled section order (Vitals / Economy / Conditions /
    ///    Offense / Defense / Modifiers / Abilities / Identity) — "the unit
    ///    panel re-sorted, no invented columns". Condition names are
    ///    enriched to "name, description" pairs from the game's own
    ///    encyclopedia catalog (ToolTipControl rules tooltips — the same
    ///    compose a sheet keyword click would render). Facts the block
    ///    doesn't render don't appear.
    ///
    /// Hazards honored (survey §e): getComponentList runs once in the payload
    /// (the inspect compose calls it again internally — twice per landing
    /// keypress total, never per frame); the catalog builds on
    /// first use (warmed at the first landing, a deliberate keypress);
    /// getToolTip logs a game-side error on a miss, so keywords are probed
    /// first; isSpotted mutates for PCs, so isPC short-circuits ahead of it
    /// at the caller.
    /// </summary>
    internal static class CombatantDocument
    {
        // ---- The landing payload ----

        /// <summary>The slim top-level slice, as a sentence tail appended to
        /// the standard landing line (empty string on any failure — the
        /// landing line then degrades to the plain label grammar).</summary>
        internal static string LandingPayload(object ch)
        {
            try
            {
                int vit = I(Seams.Character_getVitality, ch);
                int wounds = I(Seams.Character_getWounds, ch);
                if (vit < 0 || wounds < 0) return "";
                int moves = I(Seams.Character_getRemainingCombatMoves, ch);
                int atks = I(Seams.Character_getRemainingAttacks, ch);
                bool isPC = B(Seams.Character_isPC, ch);

                string vitals;
                if (isPC)
                {
                    int maxVit = I(Seams.Character_getMaxVitality, ch);
                    vitals = maxVit > 0
                        ? $" {vit} of {maxVit} vitality, {N(wounds, "wound")}"
                        : $" {vit} vitality, {N(wounds, "wound")}";
                    int att = I(Seams.Character_getAttunement, ch);
                    int maxAtt = I(Seams.Character_getMaxAttunement, ch);
                    if (maxAtt > 0) vitals += $", attunement {att} of {maxAtt}";
                }
                else
                {
                    vitals = $" {vit} vitality, {N(wounds, "wound")}";
                }

                string econ = moves >= 0 && atks >= 0
                    ? $" {N(moves, "move")}, {N(atks, "attack")}."
                    : "";

                int conditions = ConditionCount(ch);
                string cond = conditions > 0 ? $" {N(conditions, "condition")}." : "";

                return $"{vitals}.{econ}{cond}";
            }
            catch { return ""; }
        }

        internal static int ConditionCount(object ch)
        {
            try
            {
                if (Seams.Character_getConditionContainer == null
                    || Seams.ConditionContainer_getComponentList == null) return 0;
                object container = Seams.Character_getConditionContainer.Invoke(ch, null);
                var list = container == null ? null
                    : Seams.ConditionContainer_getComponentList.Invoke(container, null) as System.Collections.IList;
                return list?.Count ?? 0;
            }
            catch { return 0; }
        }

        // ---- The document ----

        private static readonly HashSet<string> ListHeaders = new HashSet<string>(StringComparer.Ordinal)
        {
            "CONDITIONS", "IMMUNITIES", "RESISTANCES", "VULNERABILITIES", "ABILITIES", "SPELLS"
        };

        /// <summary>Parse the game's own inspect block and re-sort it into the
        /// ruled sections. Null when the block is unavailable (unspotted,
        /// seam missing) — the caller then leaves the plain tile-inspect
        /// panel capture in place.</summary>
        internal static List<Composer.PanelSection> Compose(object ch)
        {
            string raw;
            try
            {
                if (Seams.Character_getInspectDescription == null) return null;
                raw = Seams.Character_getInspectDescription.Invoke(ch, null) as string;
            }
            catch { return null; }
            if (string.IsNullOrWhiteSpace(raw)) return null;
            if (raw.StartsWith("It's too dark", StringComparison.Ordinal)) return null;

            var vitals = NewSection("Vitals");
            var economy = NewSection("Economy");
            var conditions = NewSection("Conditions");
            var offense = NewSection("Offense");
            var defense = NewSection("Defense");
            var modifiers = NewSection("Modifiers");
            var abilities = NewSection("Abilities");
            var identity = NewSection("Identity");

            string[] lines = raw.Split('\n');
            string pendingList = null;   // a ListHeaders name awaiting its content line
            bool sawHeader = false;      // the first non-empty line is the name header

            foreach (string rawLine in lines)
            {
                // Split on the render tab BEFORE cleaning (cleaning collapses it).
                string namePart = null, valuePart = null;
                int tab = rawLine.IndexOf('\t');
                if (tab >= 0)
                {
                    namePart = Patches.TextCleaner.CleanText(rawLine.Substring(0, tab));
                    valuePart = Patches.TextCleaner.CleanText(rawLine.Substring(tab + 1));
                }
                string clean = Patches.TextCleaner.CleanText(rawLine);
                if (string.IsNullOrWhiteSpace(clean)) continue;

                if (!sawHeader) { sawHeader = true; continue; }   // the NAME header — landing carries identity

                if (pendingList != null)
                {
                    RouteList(pendingList, clean, conditions, modifiers, abilities);
                    pendingList = null;
                    continue;
                }
                if (ListHeaders.Contains(clean)) { pendingList = clean; continue; }

                if (namePart != null && valuePart != null)
                {
                    RoutePair(namePart, valuePart, vitals, economy, offense, defense, identity);
                    continue;
                }

                // Identity prose: "Lvl. N Class (Race)", the faction line,
                // "Status: X".
                if (clean.StartsWith("Lvl.", StringComparison.Ordinal))
                    clean = "Level" + clean.Substring(4);
                identity.Elements.Add(clean);
            }

            var doc = new List<Composer.PanelSection>();
            AddIfPopulated(doc, vitals);
            AddIfPopulated(doc, economy);
            AddIfPopulated(doc, conditions);
            AddIfPopulated(doc, offense);
            AddIfPopulated(doc, defense);
            AddIfPopulated(doc, modifiers);
            AddIfPopulated(doc, abilities);
            AddIfPopulated(doc, identity);
            return doc.Count == 0 ? null : doc;
        }

        private static void RoutePair(string name, string value,
            Composer.PanelSection vitals, Composer.PanelSection economy,
            Composer.PanelSection offense, Composer.PanelSection defense,
            Composer.PanelSection identity)
        {
            switch (name)
            {
                case "Vitality": vitals.Elements.Add($"Vitality: {value}"); break;
                case "Wounds": vitals.Elements.Add($"Wounds: {value}"); break;
                case "Move/Atc": economy.Elements.Add(TranscodeMoveAtc(value)); break;
                case "Cooldown": economy.Elements.Add($"Cooldown: {value}"); break;
                case "To Hit": offense.Elements.Add($"To hit: {value}"); break;
                case "Damage": offense.Elements.Add($"Damage: {value}"); break;
                case "Soak": defense.Elements.Add($"Soak: {value}"); break;
                case "Dodge": defense.Elements.Add($"Dodge: {value}"); break;
                case "Will.": defense.Elements.Add($"Will: {value}"); break;
                case "Tough.": defense.Elements.Add($"Toughness: {value}"); break;
                default:
                    // Never drop a rendered fact (compress, don't curate).
                    identity.Elements.Add($"{name}: {value}");
                    break;
            }
        }

        /// <summary>"3/1" → "3 moves, 1 attack remaining" (the block's own
        /// remaining values — the game renders no static caps as text).</summary>
        private static string TranscodeMoveAtc(string value)
        {
            var parts = value.Split('/');
            if (parts.Length == 2
                && int.TryParse(parts[0].Trim(), out int m)
                && int.TryParse(parts[1].Trim(), out int a))
                return $"{N(m, "move")}, {N(a, "attack")} remaining";
            return $"Moves and attacks: {value}";
        }

        private static void RouteList(string header, string namesLine,
            Composer.PanelSection conditions, Composer.PanelSection modifiers,
            Composer.PanelSection abilities)
        {
            switch (header)
            {
                case "CONDITIONS":
                    foreach (string name in SplitNames(namesLine))
                    {
                        string desc = ConditionDescription(name);
                        conditions.Elements.Add(desc == null ? name : $"{name}, {desc}");
                    }
                    break;
                case "IMMUNITIES": modifiers.Elements.Add($"Immunities: {namesLine}"); break;
                case "RESISTANCES": modifiers.Elements.Add($"Resistances: {namesLine}"); break;
                case "VULNERABILITIES": modifiers.Elements.Add($"Vulnerabilities: {namesLine}"); break;
                case "ABILITIES": abilities.Elements.Add($"Abilities: {namesLine}"); break;
                case "SPELLS": abilities.Elements.Add($"Spells: {namesLine}"); break;
            }
        }

        private static string[] SplitNames(string line)
            => line.Split(new[] { ", " }, StringSplitOptions.RemoveEmptyEntries);

        /// <summary>The encyclopedia pull (dwell-rule depth): the condition's
        /// description from the game's own rules catalog. getToolTip logs a
        /// game-side error on a miss, so the keyword list is probed first.
        /// Null when unavailable — the caller speaks the name alone.</summary>
        private static string ConditionDescription(string name)
        {
            try
            {
                if (Seams.ToolTipControl_getRulesToolTips == null
                    || Seams.ToolTipCategory_getToolTip == null) return null;
                object catalog = Seams.ToolTipControl_getRulesToolTips.Invoke(null, null);
                if (catalog == null) return null;
                if (Seams.ToolTipCategory_getKeywords != null)
                {
                    var keywords = Seams.ToolTipCategory_getKeywords.Invoke(catalog, null)
                        as System.Collections.Generic.List<string>;
                    if (keywords != null && !keywords.Contains(name)) return null;
                }
                object tip = Seams.ToolTipCategory_getToolTip.Invoke(catalog, new object[] { name });
                if (tip == null || Seams.ToolTip_getDescription == null) return null;
                string desc = Seams.ToolTip_getDescription.Invoke(tip, null) as string;
                if (string.IsNullOrWhiteSpace(desc)) return null;
                return Patches.TextCleaner.CleanText(desc);
            }
            catch { return null; }
        }

        // ---- Helpers ----

        private static Composer.PanelSection NewSection(string title)
            => new Composer.PanelSection { Title = title };

        /// <summary>Empty sections are skipped silently (R4). Section landing
        /// lines: short stat sections read their content; list-shaped
        /// sections read a census, elements carry the depth.</summary>
        private static void AddIfPopulated(List<Composer.PanelSection> doc, Composer.PanelSection s)
        {
            if (s.Elements.Count == 0) return;
            bool census = s.Title == "Conditions" || s.Title == "Abilities" || s.Title == "Identity";
            s.FullText = census
                ? $"{s.Title}, {s.Elements.Count}"
                : $"{s.Title}, " + string.Join(", ", s.Elements.ToArray());
            doc.Add(s);
        }

        private static int I(System.Reflection.MethodInfo m, object target)
        {
            try { return m == null || target == null ? -1 : (int)m.Invoke(target, null); }
            catch { return -1; }
        }

        private static bool B(System.Reflection.MethodInfo m, object target)
        {
            try { return m != null && target != null && (bool)m.Invoke(target, null); }
            catch { return false; }
        }

        private static string N(int n, string singular)
            => n == 1 ? $"1 {singular}" : $"{n} {singular}s";
    }
}
