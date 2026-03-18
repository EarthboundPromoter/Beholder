using System.Text.RegularExpressions;

namespace SkaldAccessibility.Patches
{
    /// <summary>
    /// Text cleaning utility. Strips markup, color codes, scripting syntax,
    /// and formatting from raw game text, leaving clean readable strings.
    ///
    /// Previously a Harmony patch on UITextBlock.setContent(). Now a utility
    /// class only — speech is handled by ContentSpeechPatch (event-driven hooks
    /// on specific content methods) and IndexNavigationPatch (navigation speech).
    /// </summary>
    public static class TextInterceptPatch
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

            // Strip Unity rich-text color tags: <color=#RRGGBBAA>, <COLOR=name>, </color>, </COLOR>
            text = Regex.Replace(text, @"</?color[^>]*>", "", RegexOptions.IgnoreCase);

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
