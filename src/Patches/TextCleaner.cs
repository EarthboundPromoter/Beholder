using System.Text.RegularExpressions;

namespace SkaldAccessibility.Patches
{
    /// <summary>
    /// Text cleaning utility. Strips markup, color codes, scripting syntax,
    /// and formatting from raw game text, leaving clean readable strings.
    ///
    /// Previously a Harmony patch on UITextBlock.setContent(). Now a utility
    /// class only — speech is handled by ContentSpeechPatch (targeted content
    /// hooks) and the selection join (SelectionJoinPatch + Pump composition).
    /// </summary>
    public static class TextCleaner
    {
        private static readonly Regex TagRegex = new Regex(@"</?tag>", RegexOptions.Compiled);
        private static readonly Regex FunctionRegex = new Regex(@"\{[^}]+\}", RegexOptions.Compiled);

        /// <summary>
        /// Strip markup tags and scripting syntax, leaving clean readable text.
        /// </summary>
        internal static string CleanText(string raw)
        {
            if (string.IsNullOrEmpty(raw)) return raw;

            string text = raw;

            // Strip <tag>keyword</tag> markup (tooltip highlights)
            text = TagRegex.Replace(text, "");

            // Strip <Header>...</Header> and other game UI markup tags
            text = Regex.Replace(text, @"</?[A-Za-z][A-Za-z0-9]*>", "");

            // Strip Unity rich-text color tags: <color=#RRGGBBAA>, <COLOR=name>, </color>, </COLOR>.
            // Tolerates the game's malformed variants (text-surface-audit flag 2):
            // "<\color>" from Item.makeComparativeColorTag* and "</ color >" in
            // PopUpSaveDelete — a matcher keyed on well-formed tags leaves residue.
            text = Regex.Replace(text, @"<[/\\]?\s*color[^>]*>", "", RegexOptions.IgnoreCase);

            // Strip {functionCall(params)} scripting calls
            text = FunctionRegex.Replace(text, "");

            // Strip color codes like #c=COLOR: and #c=:
            text = Regex.Replace(text, @"#c=[^:]*:", "");

            // Strip remaining hash markup that isn't part of words
            text = Regex.Replace(text, @"#[A-Z]{2,}", "");

            // Strip leading option number prefixes: "1. ", "2) ", etc.
            text = Regex.Replace(text, @"^\d+[.)]\s*", "");

            // Collapse whitespace
            text = Regex.Replace(text, @"\s+", " ").Trim();

            return text;
        }
    }
}
