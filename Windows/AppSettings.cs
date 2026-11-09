using System;
using System.Drawing;
using System.Globalization;
using System.IO;

namespace BgaSpnfdr
{
    /// <summary>
    /// Everything the app remembers between sessions, stored as key=value
    /// lines in %APPDATA%\bga_spnfdr\settings.txt. Keys are compatible with
    /// the settings written by earlier builds.
    /// </summary>
    internal sealed class AppSettings
    {
        public string WindowTitle = string.Empty;
        public Rectangle Crop = Rectangle.Empty;
        public string CsvPath = string.Empty;
        public float NotesFontSize = 48f;
        public string NotesFontFamily = "Segoe UI";
        public FontStyle NotesFontStyle = FontStyle.Bold;
        public Color NotesTextColor = Color.White;
        public Color NotesBackColor = Color.Black;
        public bool NotesTopMost = true;
        public Point NotesLocation = new Point(80, 80);
        public Rectangle SetupBounds = Rectangle.Empty;
        public bool SetupMaximized;

        private static string FilePath()
        {
            string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            string dir = Path.Combine(appData, "bga_spnfdr");
            string file = Path.Combine(dir, "settings.txt");
            Directory.CreateDirectory(dir);
            // migrate settings saved under the program's old name
            string oldFile = Path.Combine(appData, "ScoreNotes", "settings.txt");
            try
            {
                if (!File.Exists(file) && File.Exists(oldFile))
                    File.Copy(oldFile, file);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            return file;
        }

        public static AppSettings Load()
        {
            var s = new AppSettings();
            string[] lines;
            try { lines = File.ReadAllLines(FilePath()); }
            catch (IOException) { return s; }
            catch (UnauthorizedAccessException) { return s; }

            foreach (string line in lines)
            {
                int eq = line.IndexOf('=');
                if (eq < 0) continue;
                string key = line.Substring(0, eq);
                string value = line.Substring(eq + 1);
                switch (key)
                {
                    case "Window":
                        s.WindowTitle = value;
                        break;
                    case "Crop":
                        s.Crop = ParseRect(value, s.Crop);
                        break;
                    case "Csv":
                        s.CsvPath = value;
                        break;
                    case "NotesFont":
                        if (float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out float size))
                            s.NotesFontSize = size;
                        break;
                    case "NotesFontFamily":
                        if (value.Length > 0)
                            s.NotesFontFamily = value;
                        break;
                    case "NotesFontStyle":
                        if (int.TryParse(value, out int style))
                            s.NotesFontStyle = (FontStyle)style;
                        break;
                    case "NotesTextColor":
                        s.NotesTextColor = ParseColor(value, s.NotesTextColor);
                        break;
                    case "NotesBackColor":
                        s.NotesBackColor = ParseColor(value, s.NotesBackColor);
                        break;
                    case "NotesTopMost":
                        if (bool.TryParse(value, out bool topMost))
                            s.NotesTopMost = topMost;
                        break;
                    case "NotesPos":
                        var p = ParseRect(value + ",0,0", Rectangle.Empty);
                        if (value.Split(',').Length == 2)
                            s.NotesLocation = new Point(p.X, p.Y);
                        break;
                    case "SetupBounds":
                        s.SetupBounds = ParseRect(value, s.SetupBounds);
                        break;
                    case "SetupMaximized":
                        if (bool.TryParse(value, out bool max))
                            s.SetupMaximized = max;
                        break;
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
                    "Window=" + WindowTitle,
                    $"Crop={Crop.X},{Crop.Y},{Crop.Width},{Crop.Height}",
                    "Csv=" + CsvPath,
                    "NotesFont=" + NotesFontSize.ToString(CultureInfo.InvariantCulture),
                    "NotesFontFamily=" + NotesFontFamily,
                    "NotesFontStyle=" + (int)NotesFontStyle,
                    "NotesTextColor=" + ColorTranslator.ToHtml(NotesTextColor),
                    "NotesBackColor=" + ColorTranslator.ToHtml(NotesBackColor),
                    "NotesTopMost=" + NotesTopMost,
                    $"NotesPos={NotesLocation.X},{NotesLocation.Y}",
                    $"SetupBounds={SetupBounds.X},{SetupBounds.Y},{SetupBounds.Width},{SetupBounds.Height}",
                    "SetupMaximized=" + SetupMaximized,
                });
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        private static Rectangle ParseRect(string value, Rectangle fallback)
        {
            var parts = value.Split(',');
            if (parts.Length >= 4
                && int.TryParse(parts[0], out int x) && int.TryParse(parts[1], out int y)
                && int.TryParse(parts[2], out int w) && int.TryParse(parts[3], out int h))
                return new Rectangle(x, y, w, h);
            return fallback;
        }

        private static Color ParseColor(string value, Color fallback)
        {
            try { return ColorTranslator.FromHtml(value); }
            catch (Exception) { return fallback; }
        }
    }
}
