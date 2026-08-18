using System;
using System.Collections.Generic;

namespace SkaldAccessibility
{
    /// <summary>
    /// CP2 (combat-spec §2/§6): the string composer's inaugural build — the
    /// factory all call-site string building eventually migrates into. PURE:
    /// plain data in, utterances out; no game reads, no reflection, no state
    /// beyond the frame handed to it. The pump/spine owns WHEN and hands
    /// attributed facts; this module owns THE WORDS (per the stub: clean per
    /// part before assembly, never mutate across joins; verbosity and edge
    /// vocabulary live here).
    ///
    /// Combat narration contract (owner rulings 2026-08-18):
    ///  - Log shorts are FACT SOURCES, never speakable text: every line that
    ///    names a combatant is REBUILT with the display name (the lettered
    ///    identifier), because the game's own strings carry no identifier.
    ///    Unattributable lines degrade to normalized bare text — insurance,
    ///    not a path.
    ///  - Casing normalizes ("Bjorn hits Goblin B.", never "BJORN hits
    ///    GOBLIN") — screen-reader transcoding.
    ///  - Damage lines always carry the target's name; soak/resist parts stay
    ///    BEFORE the damage part (the game's render order); the number is the
    ///    real damage taken.
    ///  - Identical outcomes coalesce with a trailing "each"; different
    ///    magnitudes speak individually — genuinely different outcomes need
    ///    singular readout.
    ///  - There is no verbose tier: the ledgers stay in the game's combat log.
    ///
    /// 2026-08-18 adversarial-review hardening (Sonnet pass): condition-bark
    /// absorption hoisted BEFORE packet segmentation (find 3); packets pair
    /// to damage events by AMOUNT, not a running cursor (find 6 — the
    /// retribution-immune ledger-only packet); name matching is whole-word
    /// (find 4); miss barks steal per anchor, counted (find 5); deaths get an
    /// order-correlation fallback and the overrun trailer gates on the
    /// TARGET's identity (find 8); orphan-bark attribution is conservative —
    /// single-grower with exact count match, else bare (find 10); the
    /// "Targets:" cut covers whole-line matches (find 11).
    /// </summary>
    internal static class Composer
    {
        /// <summary>One frame's attributed combat facts, assembled by the
        /// spine from game reads; everything here is plain data.</summary>
        internal sealed class Frame
        {
            public readonly List<string> EventShorts = new List<string>();   // cleaned log shorts, in order
            public readonly List<string> Barks = new List<string>();         // cleaned barks, in order (consumed items are removed)
            public readonly List<string> Tactical = new List<string>();      // new flash texts this frame
            public readonly List<Member> Roster = new List<Member>();        // every combatant ever seen this encounter
            public string AttackerDisplay;      // current character (null when unknown)
            public string AttackerBare;
            public string TargetDisplay;        // current character's target opponent
            public string TargetBare;
            public int TargetRosterIndex = -1;  // the target's row in Roster (identity link)
            public bool AttackerIsRanged;
        }

        internal sealed class Member
        {
            public string Display;      // "Goblin B" / "Bjorn"
            public string Bare;         // "Goblin"
            public int HpDelta;         // (vitality+wounds) change this frame; negative = damage taken
            public bool BecameDead;
            public int BarkGrowth;      // barks added to this member's own control this frame
            public bool IsPC;
        }

        /// <summary>Compose the frame's narration. Consumes matched barks out
        /// of frame.Barks (the caller speaks the remainder through the normal
        /// bark path). Returns the ordered utterance list.</summary>
        internal static List<string> ComposeCombatFrame(Frame frame)
        {
            var outLines = new List<string>();
            var condLines = new List<string>();
            var tailLines = new List<string>();   // deaths, overruns — always last
            var damageLines = new List<DamageFact>();
            int missAnchors = 0;

            // PRE-PASS (find 3): condition resist/immune lines absorb their
            // barks BEFORE packet segmentation, so a condition "Immune: X"
            // bark can never be swept into a damage packet.
            var consumedEvents = new HashSet<int>();
            for (int i = 0; i < frame.EventShorts.Count; i++)
            {
                string line = TryComposeConditionLine(frame.EventShorts[i], frame);
                if (line != null) { condLines.Add(line); consumedEvents.Add(i); }
            }

            var packets = SegmentDamagePackets(frame.Barks);
            var deathCursor = new Dictionary<string, int>();  // per-name order-correlation (find 8)
            bool overrunSpoken = false;

            for (int i = 0; i < frame.EventShorts.Count; i++)
            {
                if (consumedEvents.Contains(i)) continue;
                string ev = frame.EventShorts[i];

                if (ev.StartsWith("ROUND ", StringComparison.OrdinalIgnoreCase))
                    continue;   // round headers are log-home; the primaryHeader anchors rounds

                int tookIdx = ev.IndexOf(" took damage: ", StringComparison.Ordinal);
                if (tookIdx > 0)
                {
                    string bare = ev.Substring(0, tookIdx).Trim();
                    int amount = ParseTrailingInt(ev, tookIdx + " took damage: ".Length);
                    var packet = ClaimPacket(packets, amount);   // amount-paired (find 6)
                    string display = AttributeDamage(frame.Roster, bare, amount) ?? bare;
                    damageLines.Add(new DamageFact
                    {
                        Display = display,
                        Bare = bare,
                        Payload = packet != null && packet.Parts.Count > 0
                            ? string.Join(", ", packet.Parts.ToArray())
                            : (amount >= 0 ? amount + " Damage" : "took damage"),
                        Bloodied = packet != null && packet.Bloodied,
                    });
                    continue;
                }

                if (ev.EndsWith(" is Dead", StringComparison.Ordinal)
                    || ev.EndsWith(" is Knocked Out", StringComparison.Ordinal))
                {
                    bool ko = ev.EndsWith(" is Knocked Out", StringComparison.Ordinal);
                    string bare = ev.Substring(0, ev.Length - (ko ? " is Knocked Out" : " is Dead").Length).Trim();
                    int memberIdx = AttributeDeath(frame.Roster, bare, deathCursor);
                    string display = memberIdx >= 0 ? frame.Roster[memberIdx].Display : bare;
                    tailLines.Add(display + (ko ? " is Knocked Out." : " is Dead."));
                    // Kill-overrun trailer: identity-gated (find 8) — only for
                    // THE target's own death, once, on a melee kill.
                    if (!overrunSpoken && !frame.AttackerIsRanged && frame.AttackerDisplay != null
                        && frame.TargetRosterIndex >= 0 && memberIdx == frame.TargetRosterIndex
                        && frame.Roster[memberIdx].BecameDead)
                    {
                        overrunSpoken = true;
                        tailLines.Add(frame.AttackerDisplay + " moved to " + display + "'s tile.");
                    }
                    continue;
                }

                // Attack anchors: rebuild from the attributed attacker/target.
                string anchor = TryRebuildAnchor(ev, frame, ref missAnchors);
                if (anchor != null) { outLines.Add(anchor); continue; }

                // Generic short: normalize casing via known names, letter where
                // unambiguous, speak as-is otherwise (the insurance path).
                // The fire-time "Targets:" segment is CUT — it lists the
                // geometric selection BEFORE legality filtering (survey
                // §4b.11, ruled: never present it as the affected list; the
                // per-target damage/save lines carry the affected truth).
                string generic = ev;
                int targetsIdx = generic.IndexOf("Targets:", StringComparison.OrdinalIgnoreCase);
                if (targetsIdx >= 0) generic = generic.Substring(0, targetsIdx).TrimEnd(' ', '/', '-', ',');
                if (generic.Length == 0) continue;
                outLines.Add(EnsurePeriod(NormalizeNames(generic, frame.Roster)));
            }

            // The attacker's "Miss" bark is the same fact as a miss anchor —
            // one steal per anchor (find 5: two misses in a frame absorb two).
            for (int k = 0; k < missAnchors; k++)
                if (!frame.Barks.Remove("Miss")) break;

            // Tactical flashes are their own facts (Cascade has NO other channel).
            foreach (string t in frame.Tactical)
                outLines.Add(EnsurePeriod(t));

            // Damage lines: identical outcomes coalesce with "each"; different
            // magnitudes stay singular (owner ruling).
            EmitDamageLines(damageLines, outLines);

            outLines.AddRange(condLines);

            // Orphan barks: conservative attribution (find 10) — prefix with
            // the single grower's name only when exactly one member's own bark
            // control grew AND its growth covers the orphan count; "Saved:"
            // barks always move to the event path (bare when ambiguous), the
            // rest stay residual for the normal bark stream.
            AttributeOrphanBarks(frame, outLines);

            outLines.AddRange(tailLines);
            return CollapseRepeats(outLines);
        }

        // ---- Damage packets ----

        private sealed class Packet
        {
            public readonly List<string> Parts = new List<string>();
            public bool Bloodied;
            public int TerminalAmount = -1;   // leading digits of the "N ... Damage" bark; -1 = ledger-only
            public bool Claimed;
        }

        private sealed class DamageFact
        {
            public string Display, Bare, Payload;
            public bool Bloodied;
        }

        private static List<Packet> SegmentDamagePackets(List<string> barks)
        {
            var packets = new List<Packet>();
            var consumed = new List<string>();
            Packet current = null;
            Packet lastClosed = null;
            foreach (string b in barks)
            {
                bool isLedger = (b.StartsWith("Soak: ", StringComparison.Ordinal)
                    || b.StartsWith("Resistant: ", StringComparison.Ordinal)
                    || b.StartsWith("Immune: ", StringComparison.Ordinal)
                    || b.StartsWith("Vulnerable: ", StringComparison.Ordinal))
                    && !IsCritFamily(b);
                bool isTerminal = b.EndsWith(" Damage", StringComparison.Ordinal) && StartsWithDigit(b);
                if (isLedger)
                {
                    if (current == null) current = new Packet();
                    current.Parts.Add(b);
                    consumed.Add(b);
                }
                else if (isTerminal)
                {
                    if (current == null) current = new Packet();
                    current.Parts.Add(b);
                    current.TerminalAmount = ParseLeadingInt(b);
                    consumed.Add(b);
                    packets.Add(current);
                    lastClosed = current;
                    current = null;
                }
                else if (b == "Bloodied!" && lastClosed != null && !lastClosed.Bloodied)
                {
                    lastClosed.Bloodied = true;
                    consumed.Add(b);
                }
            }
            // An unterminated ledger run (damage fully negated — e.g.
            // "Immune: Piercing" retribution with no damage bark) is a
            // ledger-only packet; ClaimPacket pairs it with its zero-damage
            // log entry by amount (find 6).
            if (current != null && current.Parts.Count > 0) packets.Add(current);
            foreach (string c in consumed) barks.Remove(c);
            return packets;
        }

        /// <summary>Pair a damage log entry to its bark packet by AMOUNT
        /// (find 6): first unclaimed packet whose terminal number matches;
        /// zero/negated damage claims a ledger-only packet; order-based
        /// first-unclaimed as the last resort.</summary>
        private static Packet ClaimPacket(List<Packet> packets, int amount)
        {
            foreach (var p in packets)
                if (!p.Claimed && p.TerminalAmount == amount && amount >= 0)
                { p.Claimed = true; return p; }
            if (amount <= 0)
                foreach (var p in packets)
                    if (!p.Claimed && p.TerminalAmount < 0)
                    { p.Claimed = true; return p; }
            foreach (var p in packets)
                if (!p.Claimed) { p.Claimed = true; return p; }
            return null;
        }

        // "Immune: Criticals"/"Immune: Backstab" are crit-resist facts, never
        // damage-type ledger parts. (Condition "Immune: X" barks are absorbed
        // by the condition-line pre-pass before segmentation ever runs.)
        private static bool IsCritFamily(string b)
            => b == "Immune: Criticals" || b == "Immune: Backstab";

        private static void EmitDamageLines(List<DamageFact> facts, List<string> outLines)
        {
            var emitted = new bool[facts.Count];
            for (int i = 0; i < facts.Count; i++)
            {
                if (emitted[i]) continue;
                var group = new List<int> { i };
                for (int j = i + 1; j < facts.Count; j++)
                {
                    if (!emitted[j] && facts[j].Bare == facts[i].Bare
                        && facts[j].Payload == facts[i].Payload
                        && facts[j].Bloodied == facts[i].Bloodied)
                        group.Add(j);
                }
                if (group.Count > 1)
                {
                    foreach (int g in group) emitted[g] = true;
                    outLines.Add($"{CountWord(group.Count)} {Pluralize(facts[i].Bare)}: {facts[i].Payload} each.");
                    if (facts[i].Bloodied)
                        outLines.Add($"{CountWord(group.Count)} {Pluralize(facts[i].Bare)}: Bloodied!");
                }
                else
                {
                    emitted[i] = true;
                    outLines.Add($"{facts[i].Display}: {facts[i].Payload}.");
                    if (facts[i].Bloodied) outLines.Add($"{facts[i].Display}: Bloodied!");
                }
            }
        }

        // ---- Attribution (plain-data matching; the spine did the game reads) ----

        private static string AttributeDamage(List<Member> roster, string bare, int amount)
        {
            Member only = null; int candidates = 0;
            Member amountMatch = null; int amountMatches = 0;
            foreach (var m in roster)
            {
                if (m.Bare != bare || m.HpDelta >= 0) continue;
                candidates++; only = m;
                if (amount >= 0 && -m.HpDelta == amount) { amountMatches++; amountMatch = m; }
            }
            if (candidates == 1) return only.Display;
            if (amountMatches == 1) return amountMatch.Display;
            return null;
        }

        /// <summary>Death attribution: unique BecameDead candidate first, then
        /// order-correlation (find 8: the k-th death line of a name maps to
        /// the k-th newly-dead member of that name in roster/entry order).
        /// Returns the roster index, or -1 (bare fallback).</summary>
        private static int AttributeDeath(List<Member> roster, string bare, Dictionary<string, int> deathCursor)
        {
            int only = -1; int candidates = 0;
            for (int i = 0; i < roster.Count; i++)
            {
                if (roster[i].Bare == bare && roster[i].BecameDead) { candidates++; only = i; }
            }
            if (candidates == 1) return only;
            if (candidates > 1)
            {
                int cursor;
                deathCursor.TryGetValue(bare, out cursor);
                int seen = 0;
                for (int i = 0; i < roster.Count; i++)
                {
                    if (roster[i].Bare != bare || !roster[i].BecameDead) continue;
                    if (seen == cursor) { deathCursor[bare] = cursor + 1; return i; }
                    seen++;
                }
            }
            return -1;
        }

        private static string TryRebuildAnchor(string ev, Frame frame, ref int missAnchors)
        {
            bool hit = ev.IndexOf(" hits ", StringComparison.OrdinalIgnoreCase) > 0
                || ev.IndexOf(" automatically hit ", StringComparison.OrdinalIgnoreCase) > 0;
            bool miss = ev.IndexOf(" misses ", StringComparison.OrdinalIgnoreCase) > 0;
            if (!hit && !miss) return null;
            if (miss) missAnchors++;
            // The anchor's names are render-uppercased; the objects are known
            // (attacker = current character, target = its target opponent).
            // Rebuild only when both names WHOLE-WORD match the objects
            // (find 4: substring containment cross-matches "Wolf"/"Dire Wolf").
            if (frame.AttackerBare != null && frame.TargetBare != null
                && ContainsWord(ev, frame.AttackerBare)
                && ContainsWord(ev, frame.TargetBare))
            {
                return $"{frame.AttackerDisplay} {(miss ? "misses" : "hits")} {frame.TargetDisplay}.";
            }
            return EnsurePeriod(NormalizeNames(ev, frame.Roster));
        }

        private static string TryComposeConditionLine(string ev, Frame frame)
        {
            int idx = ev.IndexOf(" resisted: ", StringComparison.Ordinal);
            string verb = " resisted: ";
            if (idx < 0) { idx = ev.IndexOf(" immune to: ", StringComparison.Ordinal); verb = " immune to: "; }
            if (idx <= 0) return null;
            string bare = ev.Substring(0, idx).Trim();
            string what = ev.Substring(idx + verb.Length).Trim();
            string display = UniqueDisplay(frame.Roster, bare) ?? bare;
            // Absorb the positional bark carrying the same fact.
            frame.Barks.Remove("Resisted: " + what);
            frame.Barks.Remove("Immune: " + what);
            return $"{display}{verb}{what}.".Replace(" resisted: ", " resisted ").Replace(" immune to: ", " immune to ");
        }

        private static void AttributeOrphanBarks(Frame frame, List<string> outLines)
        {
            Member grower = null; int growers = 0;
            foreach (var m in frame.Roster)
                if (m.BarkGrowth > 0) { growers++; grower = m; }
            bool confident = growers == 1 && grower.BarkGrowth >= CountOrphans(frame.Barks);
            for (int i = frame.Barks.Count - 1; i >= 0; i--)
            {
                string b = frame.Barks[i];
                bool isSave = b.StartsWith("Saved: ", StringComparison.Ordinal);
                bool attributable = isSave || b.StartsWith("Phalanx: ", StringComparison.Ordinal);
                if (!attributable) continue;
                if (confident)
                {
                    frame.Barks.RemoveAt(i);
                    outLines.Add($"{grower.Display}: {EnsurePeriod(b)}");
                }
                else if (isSave)
                {
                    // Saves are combat events even unattributed — the event
                    // path (no dedup second-guessing) is their right home.
                    frame.Barks.RemoveAt(i);
                    outLines.Add(EnsurePeriod(b));
                }
                // Non-save attributables without confidence stay residual.
            }
        }

        private static int CountOrphans(List<string> barks)
        {
            int n = 0;
            foreach (string b in barks)
                if (b.StartsWith("Saved: ", StringComparison.Ordinal)
                    || b.StartsWith("Phalanx: ", StringComparison.Ordinal)) n++;
            return n;
        }

        private static string UniqueDisplay(List<Member> roster, string bare)
        {
            Member only = null; int n = 0;
            foreach (var m in roster) if (m.Bare == bare) { n++; only = m; }
            return n == 1 ? only.Display : null;
        }

        /// <summary>Whole-word containment (find 4) — "Wolf" never matches
        /// inside "Dire Wolf". Case-insensitive.</summary>
        internal static bool ContainsWord(string text, string word)
        {
            if (string.IsNullOrEmpty(text) || string.IsNullOrEmpty(word)) return false;
            int start = 0;
            while (true)
            {
                int idx = text.IndexOf(word, start, StringComparison.OrdinalIgnoreCase);
                if (idx < 0) return false;
                bool leftOk = idx == 0 || !char.IsLetterOrDigit(text[idx - 1]);
                int after = idx + word.Length;
                bool rightOk = after >= text.Length || !char.IsLetterOrDigit(text[after]);
                if (leftOk && rightOk) return true;
                start = idx + 1;
            }
        }

        /// <summary>Replace every known combatant name (the render's uppercase
        /// form included) with normalized casing, whole-word only (find 4).
        /// Letters never enter here — string-only attribution cannot pick a
        /// letter; the attributed paths carry them.</summary>
        internal static string NormalizeNames(string text, List<Member> roster)
        {
            if (string.IsNullOrEmpty(text)) return text;
            var seen = new HashSet<string>();
            foreach (var m in roster)
            {
                if (m.Bare == null || !seen.Add(m.Bare)) continue;
                string upper = m.Bare.ToUpperInvariant();
                if (upper != m.Bare) text = ReplaceWholeWord(text, upper, m.Bare);
            }
            return text;
        }

        private static string ReplaceWholeWord(string text, string word, string replacement)
        {
            int start = 0;
            while (true)
            {
                int idx = text.IndexOf(word, start, StringComparison.Ordinal);
                if (idx < 0) return text;
                bool leftOk = idx == 0 || !char.IsLetterOrDigit(text[idx - 1]);
                int after = idx + word.Length;
                bool rightOk = after >= text.Length || !char.IsLetterOrDigit(text[after]);
                if (leftOk && rightOk)
                {
                    text = text.Substring(0, idx) + replacement + text.Substring(after);
                    start = idx + replacement.Length;
                }
                else start = idx + 1;
            }
        }

        // ---- Vocabulary ----

        private static readonly string[] CountWords =
            { "Zero", "One", "Two", "Three", "Four", "Five", "Six", "Seven", "Eight", "Nine" };

        internal static string CountWord(int n)
            => n >= 0 && n < CountWords.Length ? CountWords[n] : n.ToString();

        internal static string Pluralize(string name)
        {
            if (string.IsNullOrEmpty(name)) return name;
            string lower = name.ToLowerInvariant();
            if (lower.EndsWith("s") || lower.EndsWith("x") || lower.EndsWith("sh") || lower.EndsWith("ch"))
                return lower + "es";
            if (lower.EndsWith("fe")) return lower.Substring(0, lower.Length - 2) + "ves";
            if (lower.EndsWith("f")) return lower.Substring(0, lower.Length - 1) + "ves";
            if (lower.EndsWith("y") && lower.Length > 1 && !"aeiou".Contains(lower[lower.Length - 2].ToString()))
                return lower.Substring(0, lower.Length - 1) + "ies";
            return lower + "s";
        }

        internal static string EnsurePeriod(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return s;
            s = s.Trim();
            char last = s[s.Length - 1];
            return (last == '.' || last == '!' || last == '?') ? s : s + ".";
        }

        private static bool StartsWithDigit(string s) => s.Length > 0 && char.IsDigit(s[0]);

        // =====================================================================
        // Panel sectioning — the review buffer's document model (TP1, the
        // factory's second consumer). PURE: raw markup strings in, sections
        // out. Tag VALUES are injected once via PanelTags (PanelPolicy reads
        // them through the Seams handles — the composer itself never reflects).
        //
        // Two-tier rubric (owner-ruled 2026-08-18): provenance picks the map,
        // markup structures within a capture. The generic map below encodes
        // the audited composer shapes (text-surface-audit §7):
        //  - a <Header> line OPENS a section and OWNS following contiguous
        //    content (the SNEAKING block, item identity+type, inspect
        //    identity) — headers are titles, not orphan sections;
        //  - a known list label (CONDITIONS/IMMUNITIES/RESISTANCES/
        //    VULNERABILITIES/ABILITIES/SPELLS — Character.getInspectDescription's
        //    literals) opens a section whose elements are the list entries;
        //  - contiguous pair lines form a stats section (one pair per element,
        //    green/red comparison transcoded per pair); consecutive stats
        //    sections separated only by blank lines MERGE (the inspect ladder
        //    reads as one combat-stats section);
        //  - anything else is prose: blank-line stanzas are sections,
        //    sentences are elements.
        // Pair matching accepts the ATTRIBUTE_VALUE span alone — the game's
        // colored-name variants (Yellow/Soft pairs) are first-class pairs by
        // design here, not by parser accident (audit flag 4).
        // =====================================================================

        internal sealed class PanelSection
        {
            public string Title;                 // header/label text, null for anonymous sections
            public string FullText;              // what a section-level landing speaks
            public readonly List<string> Elements = new List<string>();
        }

        /// <summary>Markup tag values, injected once (PanelPolicy.EnsureTags).
        /// Null members simply disable their rule — sectioning degrades to
        /// paragraphs, never throws.</summary>
        internal static class PanelTags
        {
            public static string Header, AttrName, AttrValue, Green, Red;
        }

        private static readonly string[] ListLabels =
            { "CONDITIONS", "IMMUNITIES", "RESISTANCES", "VULNERABILITIES", "ABILITIES", "SPELLS" };

        /// <summary>Section a captured panel. <paramref name="source"/> is the
        /// provenance tag (recorded for future per-source maps; the current map
        /// is shape-complete for every audited writer).</summary>
        internal static List<PanelSection> SectionPanel(string source, string raw)
        {
            var sections = new List<PanelSection>();
            if (string.IsNullOrWhiteSpace(raw)) return sections;

            PanelSection current = null;
            string kind = null;            // "header" | "label" | "stats" | "prose"
            bool blankGap = false;         // a blank line closed the previous run
            var prose = new System.Text.StringBuilder();

            void CloseProse()
            {
                if (kind == "prose" && current != null)
                {
                    current.FullText = prose.ToString().Trim();
                    foreach (var s in SplitSentences(current.FullText)) current.Elements.Add(s);
                    if (current.Elements.Count > 0) sections.Add(current);
                }
                prose.Length = 0;
            }

            void Close()
            {
                if (current == null) { kind = null; return; }
                if (kind == "prose") { CloseProse(); }
                else if (current.Elements.Count > 0 || !string.IsNullOrEmpty(current.Title))
                {
                    if (current.FullText == null)
                        current.FullText = current.Title == null
                            ? string.Join(", ", current.Elements.ToArray())
                            : (current.Elements.Count == 0
                                ? current.Title
                                : current.Title + ", " + string.Join(", ", current.Elements.ToArray()));
                    sections.Add(current);
                }
                current = null; kind = null;
            }

            foreach (string rawLine in raw.Split('\n'))
            {
                string cleaned = Patches.TextCleaner.CleanText(rawLine);
                if (string.IsNullOrWhiteSpace(cleaned))
                {
                    // Blank: header/label blocks and prose close outright; a
                    // stats run closes SOFTLY (an immediately following pair
                    // reopens it — the merge rule).
                    if (kind == "stats") { blankGap = true; continue; }
                    Close();
                    blankGap = false;
                    continue;
                }

                bool header = PanelTags.Header != null && rawLine.Contains(PanelTags.Header);
                bool pair = !header
                    && ((PanelTags.AttrName != null && rawLine.Contains(PanelTags.AttrName))
                        || (PanelTags.AttrValue != null && rawLine.Contains(PanelTags.AttrValue)));
                bool label = !header && !pair && IsListLabel(cleaned);

                if (header)
                {
                    Close(); blankGap = false;
                    current = new PanelSection { Title = cleaned.Trim() };
                    kind = "header";
                }
                else if (label)
                {
                    Close(); blankGap = false;
                    current = new PanelSection { Title = TitleCaseLabel(cleaned.Trim()) };
                    kind = "label";
                }
                else if (kind == "header")
                {
                    // The header owns everything until the blank line.
                    current.Elements.Add(Transcode(rawLine, cleaned.Trim()));
                }
                else if (kind == "label")
                {
                    foreach (var item in cleaned.Split(','))
                    {
                        string t = item.Trim();
                        if (t.Length > 0) current.Elements.Add(t);
                    }
                }
                else if (pair)
                {
                    if (kind != "stats")
                    {
                        Close();
                        current = new PanelSection();
                        kind = "stats";
                    }
                    // blankGap with kind=="stats" falls through here: merge.
                    blankGap = false;
                    current.Elements.Add(Transcode(rawLine, cleaned.Trim()));
                }
                else
                {
                    if (kind == "stats" && blankGap) { Close(); blankGap = false; }
                    else if (kind == "stats") { Close(); }
                    if (kind != "prose")
                    {
                        Close();
                        current = new PanelSection();
                        kind = "prose";
                    }
                    if (prose.Length > 0) prose.Append(' ');
                    prose.Append(cleaned.Trim());
                }
            }
            Close();
            return sections;
        }

        private static bool IsListLabel(string cleaned)
        {
            string t = cleaned.Trim().TrimEnd(':');
            for (int i = 0; i < ListLabels.Length; i++)
                if (string.Equals(t, ListLabels[i], StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        private static string TitleCaseLabel(string label)
        {
            string t = label.TrimEnd(':').Trim();
            if (t.Length == 0) return t;
            return char.ToUpperInvariant(t[0]) + t.Substring(1).ToLowerInvariant();
        }

        /// <summary>Per-pair comparison transcode: the game's green/red value
        /// coloring is its only carrier of better/worse-than-equipped — turn
        /// it into words, per stat only (aggregate verdicts stay forbidden).</summary>
        private static string Transcode(string rawLine, string cleaned)
        {
            if (PanelTags.Green != null && rawLine.Contains(PanelTags.Green)) return cleaned + ", better";
            if (PanelTags.Red != null && rawLine.Contains(PanelTags.Red)) return cleaned + ", worse";
            return cleaned;
        }

        /// <summary>The identity line — what speaks at populate when the
        /// AutoReadBody config is off: the first section, title-first.</summary>
        internal static string IdentityLine(List<PanelSection> sections)
        {
            if (sections == null || sections.Count == 0) return null;
            var first = sections[0];
            if (!string.IsNullOrEmpty(first.Title))
                return first.Elements.Count > 0
                    ? first.Title + ", " + first.Elements[0]
                    : first.Title;
            return first.FullText;
        }

        internal static IEnumerable<string> SplitSentences(string text)
        {
            int start = 0;
            for (int i = 0; i < text.Length; i++)
            {
                char c = text[i];
                bool ender = c == '.' || c == '!' || c == '?';
                if (ender && (i + 1 >= text.Length || text[i + 1] == ' '))
                {
                    string sentence = text.Substring(start, i - start + 1).Trim();
                    if (sentence.Length > 0) yield return sentence;
                    start = i + 1;
                }
            }
            if (start < text.Length)
            {
                string tail = text.Substring(start).Trim();
                if (tail.Length > 0) yield return tail;
            }
        }

        // ---- The status strip (DataControl.getBuffer's product) ----

        internal sealed class StripFacts
        {
            public string Time, Day, X, Y, Weather, Phase;
            public bool Valid;
        }

        /// <summary>Decompose the game's own strip composition into its four
        /// logical facts. The caller obtained <paramref name="stripRaw"/> from
        /// DataControl.getBuffer itself, so the line shape is authoritative:
        /// Time/Day/X Pos./Y Pos. pairs, then weather prose, then the bare
        /// phase word (audit §3). Label-keyed, order-tolerant; unmatched
        /// non-empty lines fold into Weather with the final line as Phase.</summary>
        internal static StripFacts ParseStrip(string stripRaw)
        {
            var f = new StripFacts();
            if (string.IsNullOrWhiteSpace(stripRaw)) return f;
            var loose = new List<string>();
            foreach (string rawLine in stripRaw.Split('\n'))
            {
                string c = Patches.TextCleaner.CleanText(rawLine);
                if (string.IsNullOrWhiteSpace(c)) continue;
                c = c.Trim();
                if (c.StartsWith("Time:")) f.Time = c;
                else if (c.StartsWith("Day:")) f.Day = c;
                else if (c.StartsWith("X Pos.:")) f.X = c;
                else if (c.StartsWith("Y Pos.:")) f.Y = c;
                else loose.Add(c);
            }
            if (loose.Count > 0)
            {
                f.Phase = loose[loose.Count - 1];
                loose.RemoveAt(loose.Count - 1);
                if (loose.Count > 0) f.Weather = string.Join(" ", loose.ToArray());
            }
            f.Valid = f.Time != null || f.X != null || f.Phase != null;
            return f;
        }

        private static int ParseLeadingInt(string s)
        {
            int end = 0;
            while (end < s.Length && char.IsDigit(s[end])) end++;
            if (end == 0) return -1;
            int val;
            return int.TryParse(s.Substring(0, end), out val) ? val : -1;
        }

        private static int ParseTrailingInt(string s, int from)
        {
            int end = from;
            while (end < s.Length && char.IsDigit(s[end])) end++;
            if (end == from) return -1;
            int val;
            return int.TryParse(s.Substring(from, end - from), out val) ? val : -1;
        }

        /// <summary>Identical lines within one frame collapse to ", N times"
        /// (compress, don't curate) — across frames the event path never
        /// second-guesses (provenance ruling).</summary>
        private static List<string> CollapseRepeats(List<string> lines)
        {
            var result = new List<string>();
            int i = 0;
            while (i < lines.Count)
            {
                int run = 1;
                while (i + run < lines.Count && lines[i + run] == lines[i]) run++;
                result.Add(run > 1 ? $"{lines[i]}, {run} times" : lines[i]);
                i += run;
            }
            return result;
        }
    }
}
