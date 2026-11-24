using System.Collections.Generic;
using System.IO;
using System.Text;

namespace BgaSpnfdr
{
    /// <summary>Reads route CSVs: column 1 = score, column 2 = note. A note
    /// in quotes may span lines ("" inside it is a quote); one without may
    /// hold commas. Rows without a number in column 1 (e.g. a header) are
    /// skipped. Parsed by hand: Microsoft.VisualBasic's TextFieldParser
    /// added 290 KB to the Linux program, dropped blank lines inside notes
    /// and cut unquoted notes at their first comma.</summary>
    internal static class RouteCsv
    {
        /// <summary>Throws if the file can't be read or has no "score,note"
        /// rows.</summary>
        public static Dictionary<long, string> Load(string path)
        {
            var table = new Dictionary<long, string>();
            string text = File.ReadAllText(path);
            int i = 0;
            while (i < text.Length)
            {
                string first = Field(text, ref i, false);
                if (i < text.Length && text[i] == ',')
                {
                    i++;
                    string note = Field(text, ref i, true);
                    if (long.TryParse(first, out long score))
                        table[score] = note;
                    while (i < text.Length && text[i] == ',') // further columns
                    {
                        i++;
                        Field(text, ref i, false);
                    }
                }
                i++; // the line end
            }
            if (table.Count == 0)
                throw new InvalidDataException("no \"score,note\" rows found");
            return table;
        }

        /// <summary>Reads the field starting at i and leaves i on the comma
        /// or line end after it (or at the end of the text). An unquoted
        /// field loses the spaces around it; with toLineEnd it runs to the
        /// end of the line, commas included, except empty trailing columns
        /// (spreadsheet exports).</summary>
        private static string Field(string text, ref int i, bool toLineEnd)
        {
            int start = i;
            while (i < text.Length && (text[i] == ' ' || text[i] == '\t'))
                i++;
            if (i < text.Length && text[i] == '"')
            {
                var field = new StringBuilder();
                for (i++; i < text.Length; i++)
                {
                    char c = text[i];
                    if (c == '"' && i + 1 < text.Length && text[i + 1] == '"')
                        i++; // "" = one quote
                    else if (c == '"')
                        break;
                    if (c != '\r')
                        field.Append(c);
                }
                while (i < text.Length && text[i] != ',' && text[i] != '\n')
                    i++;
                return field.ToString();
            }
            while (i < text.Length && text[i] != '\n' && (toLineEnd || text[i] != ','))
                i++;
            string plain = text.Substring(start, i - start);
            return toLineEnd ? plain.TrimEnd(',', ' ', '\t', '\r').TrimStart() : plain.Trim();
        }
    }
}
