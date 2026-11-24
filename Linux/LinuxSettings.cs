using System;
using System.Drawing;
using System.Globalization;
using System.IO;

namespace BgaSpnfdr
{
    /// <summary>
    /// Everything the Linux build remembers between sessions, as key=value
    /// lines in ~/.config/bga_spnfdr/settings.txt (same style as the Windows
    /// build's file). Fonts are Pango descriptions ("Sans Bold 48"), colors
    /// #rrggbb.
    /// </summary>
    internal sealed class LinuxSettings
    {
        public string RestoreToken = string.Empty;  // reuses the shared window without prompting
        public Rectangle Crop = Rectangle.Empty;    // game picture, in captured-frame pixels
        public Size CropFrameSize = Size.Empty;     // frame size the crop was made for
        public bool CropVerified;                   // the score has been found in it (Core/GameFinder)
        public string CsvPath = string.Empty;
        public string NotesFont = "Sans Bold 48";
        public string NotesTextColor = "#ffffff";
        public string NotesBackColor = "#000000";
        public bool NotesTopMost = true;
        public Point NotesLocation = new Point(80, 80);
        public Rectangle SetupBounds = Rectangle.Empty;

        public static string FilePath()
        {
            string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "bga_spnfdr");
            Directory.CreateDirectory(dir);
            return Path.Combine(dir, "settings.txt");
        }

        public static LinuxSettings Load()
        {
            var s = new LinuxSettings();
            string[] lines;
            try { lines = File.ReadAllLines(FilePath()); }
            catch (IOException) { return s; }
            catch (UnauthorizedAccessException) { return s; }

            foreach (string line in lines)
            {
                int eq = line.IndexOf('=');
                if (eq < 0) continue;
                string key = line.Substring(0, eq), value = line.Substring(eq + 1);
                switch (key)
                {
                    case "RestoreToken": s.RestoreToken = value; break;
                    case "Crop": s.Crop = ParseRect(value, s.Crop); break;
                    case "CropFrameSize":
                        var r = ParseRect(value + ",0,0", Rectangle.Empty);
                        s.CropFrameSize = new Size(r.X, r.Y);
                        break;
                    case "CropVerified": if (bool.TryParse(value, out bool verified)) s.CropVerified = verified; break;
                    case "Csv": s.CsvPath = value; break;
                    case "NotesFont": if (value.Length > 0) s.NotesFont = value; break;
                    case "NotesTextColor": if (IsColor(value)) s.NotesTextColor = value; break;
                    case "NotesBackColor": if (IsColor(value)) s.NotesBackColor = value; break;
                    case "NotesTopMost": if (bool.TryParse(value, out bool top)) s.NotesTopMost = top; break;
                    case "NotesPos":
                        var p = ParseRect(value + ",0,0", Rectangle.Empty);
                        if (value.Split(',').Length == 2) s.NotesLocation = new Point(p.X, p.Y);
                        break;
                    case "SetupBounds": s.SetupBounds = ParseRect(value, s.SetupBounds); break;
                }
            }
            return s;
        }

        public void Save()
        {
            try
            {
                File.WriteAllLines(FilePath(), new[]
                {
                    "RestoreToken=" + RestoreToken,
                    $"Crop={Crop.X},{Crop.Y},{Crop.Width},{Crop.Height}",
                    $"CropFrameSize={CropFrameSize.Width},{CropFrameSize.Height}",
                    "CropVerified=" + CropVerified,
                    "Csv=" + CsvPath,
                    "NotesFont=" + NotesFont,
                    "NotesTextColor=" + NotesTextColor,
                    "NotesBackColor=" + NotesBackColor,
                    "NotesTopMost=" + NotesTopMost,
                    $"NotesPos={NotesLocation.X},{NotesLocation.Y}",
                    $"SetupBounds={SetupBounds.X},{SetupBounds.Y},{SetupBounds.Width},{SetupBounds.Height}",
                });
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        private static Rectangle ParseRect(string value, Rectangle fallback)
        {
            var parts = value.Split(',');
            if (parts.Length >= 4
                && int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out int x)
                && int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int y)
                && int.TryParse(parts[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out int w)
                && int.TryParse(parts[3], NumberStyles.Integer, CultureInfo.InvariantCulture, out int h))
                return new Rectangle(x, y, w, h);
            return fallback;
        }

        private static bool IsColor(string value) =>
            value.Length == 7 && value[0] == '#'
            && int.TryParse(value.Substring(1), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out _);
    }
}
