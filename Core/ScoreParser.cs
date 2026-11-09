using System.Text;

namespace BgaSpnfdr
{
    /// <summary>
    /// Turns raw OCR text into a digit string. The Windows OCR engine has no
    /// digits-only mode and reads the BattleTanx score font's digits as
    /// lookalike letters (5→S/s/t, 0→O/Ü/u, 9→g/Y/y/J, 1→I/l, 2→Z, 7→r, 8→B),
    /// all mapped back here. A read containing anything unmappable is rejected
    /// outright — dropping a character would silently produce a different,
    /// wrong number.
    /// </summary>
    internal static class ScoreParser
    {
        private const string Ignorable = " \t\r\n'’`\".,:;-_~*()<>«»°";

        /// <summary>Returns the digit string, "" if the text contains nothing
        /// readable, or null if the read is untrustworthy (unmappable chars).</summary>
        public static string Normalize(string text)
        {
            if (text == null) return "";
            var sb = new StringBuilder();
            foreach (char c in text)
            {
                if (Ignorable.IndexOf(c) >= 0) continue;
                char mapped = MapChar(c);
                if (mapped == '\0') return null;
                sb.Append(mapped);
            }
            return sb.ToString();
        }

        private static char MapChar(char c)
        {
            if (c >= '0' && c <= '9') return c;
            switch (c)
            {
                case 'O': case 'o': case 'Q': case 'D':
                case 'U': case 'u': case 'Ü': case 'ü': return '0';
                case 'I': case 'i': case 'l': case 'L': case '|': case '!': return '1';
                case 'Z': case 'z': return '2';
                case 'S': case 's': case 't': return '5';
                case 'r': return '7';
                case 'B': return '8';
                // the font's 9 has a straight tail: OCR sees Y, y, J or g
                case 'g': case 'q': case 'Y': case 'y': case 'J': return '9';
                default: return '\0';
            }
        }
    }
}
