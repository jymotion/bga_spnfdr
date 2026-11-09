using System.Collections.Generic;
using System.IO;

namespace BgaSpnfdr
{
    /// <summary>Reads route CSVs: column 1 = score, column 2 = note. Quoted
    /// multi-line notes work; malformed and non-numeric rows (e.g. a header)
    /// are skipped.</summary>
    internal static class RouteCsv
    {
        /// <summary>Throws if the file can't be read or has no "score,note"
        /// rows.</summary>
        public static Dictionary<long, string> Load(string path)
        {
            var table = new Dictionary<long, string>();
            using (var parser = new Microsoft.VisualBasic.FileIO.TextFieldParser(path))
            {
                parser.SetDelimiters(",");
                parser.HasFieldsEnclosedInQuotes = true; // multi-line quoted notes
                while (!parser.EndOfData)
                {
                    string[] fields;
                    try { fields = parser.ReadFields(); }
                    catch (Microsoft.VisualBasic.FileIO.MalformedLineException) { continue; }
                    if (fields != null && fields.Length >= 2
                        && long.TryParse(fields[0].Trim(), out long score))
                        table[score] = fields[1];
                }
            }
            if (table.Count == 0)
                throw new InvalidDataException("no \"score,note\" rows found");
            return table;
        }
    }
}
