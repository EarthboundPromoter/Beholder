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
            bool sawMissAnchor = false;
            var damageLines = new List<DamageFact>();
            var tailLines = new List<string>();   // deaths, overruns — always last

            // Pre-scan: segment the bark stream into damage packets
            // ([Soak: -N] [Resistant:/Immune:/Vulnerable: X]* [N ... Damage] [Bloodied!]),
            // in order — the k-th packet belongs to the k-th damage event
            // (barks and log entries fire in the same synchronous sequence).
            var packets = SegmentDamagePackets(frame.Barks);
            int packetCursor = 0;

            foreach (string ev in frame.EventShorts)
            {
                if (ev.StartsWith("ROUND ", StringComparison.OrdinalIgnoreCase))
                    continue;   // round headers are log-home; the primaryHeader anchors rounds

                int tookIdx = ev.IndexOf(" took damage: ", StringComparison.Ordinal);
                if (tookIdx > 0)
                {
                    string bare = ev.Substring(0, tookIdx).Trim();
                    int amount = ParseTrailingInt(ev, tookIdx + " took damage: ".Length);
                    var packet = packetCursor < packets.Count ? packets[packetCursor++] : null;
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
                    string display = AttributeDeath(frame.Roster, bare) ?? bare;
                    tailLines.Add(display + (ko ? " is Knocked Out." : " is Dead."));
                    // Kill-overrun (owner wording): every melee kill moves the
                    // attacker onto the victim's tile — the game's own gate is
                    // !isWeaponRanged on the current attacker's kill.
                    if (!frame.AttackerIsRanged && frame.AttackerDisplay != null
                        && frame.TargetBare != null && bare == frame.TargetBare)
                        tailLines.Add(frame.AttackerDisplay + " moved to " + display + "'s tile.");
                    continue;
                }

                // Attack anchors: rebuild from the attributed attacker/target.
                string anchor = TryRebuildAnchor(ev, frame, ref sawMissAnchor);
                if (anchor != null) { outLines.Add(anchor); continue; }

                // Condition resist/immune log entries ("X resisted: Poison") —
                // attribute by name, absorb the matching bark.
                string condLine = TryComposeConditionLine(ev, frame);
                if (condLine != null) { outLines.Add(condLine); continue; }

                // Generic short: normalize casing via known names, letter where
                // unambiguous, speak as-is otherwise (the insurance path).
                // The fire-time "Targets:" segment is CUT — it lists the
                // geometric selection BEFORE legality filtering (survey
                // §4b.11, ruled: never present it as the affected list; the
                // per-target damage/save lines carry the affected truth).
                string generic = ev;
                int targetsIdx = generic.IndexOf("Targets:", StringComparison.OrdinalIgnoreCase);
                if (targetsIdx > 0) generic = generic.Substring(0, targetsIdx).TrimEnd(' ', '/', '-', ',');
                outLines.Add(EnsurePeriod(NormalizeNames(generic, frame.Roster)));
            }

            // The attacker's "Miss" bark is the same fact as a miss anchor.
            if (sawMissAnchor) frame.Barks.Remove("Miss");

            // Tactical flashes are their own facts (Cascade has NO other channel).
            foreach (string t in frame.Tactical)
                outLines.Add(EnsurePeriod(t));

            // Damage lines: identical outcomes coalesce with "each"; different
            // magnitudes stay singular (owner ruling).
            EmitDamageLines(damageLines, outLines);

            // Save barks ("Saved: <effect>" — the effect's name, not the
            // saver's): attribute by bark growth when exactly one member's own
            // bark control grew and wasn't already consumed by a damage packet.
            AttributeSaveBarks(frame, outLines);

            outLines.AddRange(tailLines);
            return CollapseRepeats(outLines);
        }

        // ---- Damage packets ----

        private sealed class Packet
        {
            public readonly List<string> Parts = new List<string>();
            public bool Bloodied;
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
                bool isLedger = b.StartsWith("Soak: ", StringComparison.Ordinal)
                    || b.StartsWith("Resistant: ", StringComparison.Ordinal)
                    || b.StartsWith("Immune: ", StringComparison.Ordinal)
                    || b.StartsWith("Vulnerable: ", StringComparison.Ordinal);
                bool isTerminal = b.EndsWith(" Damage", StringComparison.Ordinal) && StartsWithDigit(b);
                if (isLedger && !IsSaveFamily(b))
                {
                    if (current == null) current = new Packet();
                    current.Parts.Add(b);
                    consumed.Add(b);
                }
                else if (isTerminal)
                {
                    if (current == null) current = new Packet();
                    current.Parts.Add(b);
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
            // An unterminated ledger run (damage fully negated to nothing —
            // e.g. "Immune: Fire" with no damage bark) still forms a packet.
            if (current != null && current.Parts.Count > 0) packets.Add(current);
            foreach (string c in consumed) barks.Remove(c);
            return packets;
        }

        // "Immune: Criticals"/"Immune: Backstab" are crit-resist facts and
        // condition-immunity barks are "Immune: <Condition>" — only the
        // damage-type forms belong to packets. The heuristic: damage-type
        // immune/resist barks are always followed by (or stand in for) a
        // damage event in the same packet walk; the crit/backstab and
        // condition forms are attributed elsewhere. Keep Criticals/Backstab
        // out of packets explicitly.
        private static bool IsSaveFamily(string b)
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

        private static string AttributeDeath(List<Member> roster, string bare)
        {
            Member only = null; int candidates = 0;
            foreach (var m in roster)
            {
                if (m.Bare == bare && m.BecameDead) { candidates++; only = m; }
            }
            return candidates == 1 ? only.Display : null;
        }

        private static string TryRebuildAnchor(string ev, Frame frame, ref bool sawMissAnchor)
        {
            bool hit = ev.IndexOf(" hits ", StringComparison.OrdinalIgnoreCase) > 0
                || ev.IndexOf(" automatically hit ", StringComparison.OrdinalIgnoreCase) > 0;
            bool miss = ev.IndexOf(" misses ", StringComparison.OrdinalIgnoreCase) > 0;
            if (!hit && !miss) return null;
            if (miss) sawMissAnchor = true;
            // The anchor's names are render-uppercased; the objects are known
            // (attacker = current character, target = its target opponent).
            // Rebuild only when the raw names actually match the objects.
            if (frame.AttackerBare != null && frame.TargetBare != null
                && ev.IndexOf(frame.AttackerBare, StringComparison.OrdinalIgnoreCase) >= 0
                && ev.IndexOf(frame.TargetBare, StringComparison.OrdinalIgnoreCase) >= 0)
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

        private static void AttributeSaveBarks(Frame frame, List<string> outLines)
        {
            Member grower = null; int growers = 0;
            foreach (var m in frame.Roster)
                if (m.BarkGrowth > 0) { growers++; grower = m; }
            for (int i = frame.Barks.Count - 1; i >= 0; i--)
            {
                string b = frame.Barks[i];
                if (!b.StartsWith("Saved: ", StringComparison.Ordinal)) continue;
                frame.Barks.RemoveAt(i);
                outLines.Add(growers == 1 ? $"{grower.Display}: {b}." : EnsurePeriod(b));
            }
        }

        private static string UniqueDisplay(List<Member> roster, string bare)
        {
            Member only = null; int n = 0;
            foreach (var m in roster) if (m.Bare == bare) { n++; only = m; }
            return n == 1 ? only.Display : null;
        }

        /// <summary>Replace every known combatant name (the render's uppercase
        /// form included) with the display name where the name-group is
        /// unambiguous; ambiguous groups get the bare name — normalized
        /// casing either way (the transcode ruling).</summary>
        internal static string NormalizeNames(string text, List<Member> roster)
        {
            if (string.IsNullOrEmpty(text)) return text;
            var seen = new HashSet<string>();
            foreach (var m in roster)
            {
                if (m.Bare == null || !seen.Add(m.Bare)) continue;
                string replacement = UniqueDisplayOrBare(roster, m.Bare);
                string upper = m.Bare.ToUpperInvariant();
                if (upper != m.Bare) text = text.Replace(upper, replacement);
                if (replacement != m.Bare) text = text.Replace(m.Bare, replacement);
            }
            return text;
        }

        private static string UniqueDisplayOrBare(List<Member> roster, string bare)
            => UniqueDisplay(roster, bare) ?? bare;

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
