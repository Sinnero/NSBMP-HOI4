using System;
using System.IO;
using System.Text;
using Microsoft.Win32;

namespace NSBMP.Pacer
{
    public static class SteamConfigManager
    {
        public const string HOI4_APP_ID = "394360";

        public static void CleanHoi4LaunchOptions()
        {
            try
            {
                string steamPath = GetSteamPath();
                if (string.IsNullOrEmpty(steamPath)) return;

                string userDataDir = Path.Combine(steamPath, "userdata");
                if (!Directory.Exists(userDataDir)) return;

                foreach (var userDir in Directory.GetDirectories(userDataDir))
                {
                    string vdf = Path.Combine(userDir, "config", "localconfig.vdf");
                    if (File.Exists(vdf))
                    {
                        CleanVdfFile(vdf);
                    }
                }
            }
            catch { }
        }

        private static void CleanVdfFile(string vdfPath)
        {
            try
            {
                string text = File.ReadAllText(vdfPath, Encoding.UTF8);
                if (!text.Contains(HOI4_APP_ID) || !text.Contains("NSBMP_Pacer")) return;

                string[] lines = File.ReadAllLines(vdfPath, Encoding.UTF8);
                var newLines = new StringBuilder();
                bool inHoi4 = false;
                int depth = 0;
                bool modified = false;

                foreach (string line in lines)
                {
                    string stripped = line.Trim();
                    if (stripped == "\"" + HOI4_APP_ID + "\"")
                    {
                        inHoi4 = true;
                        newLines.AppendLine(line);
                        continue;
                    }

                    if (inHoi4)
                    {
                        if (stripped == "{")
                        {
                            depth++;
                            newLines.AppendLine(line);
                            continue;
                        }
                        else if (stripped == "}")
                        {
                            depth--;
                            if (depth == 0) inHoi4 = false;
                            newLines.AppendLine(line);
                            continue;
                        }
                        else if (stripped.StartsWith("\"LaunchOptions\"") && line.Contains("NSBMP"))
                        {
                            modified = true;
                            continue;
                        }
                    }

                    newLines.AppendLine(line);
                }

                if (modified)
                {
                    File.WriteAllText(vdfPath, newLines.ToString(), Encoding.UTF8);
                }
            }
            catch { }
        }

        private static string GetSteamPath()
        {
            try
            {
                using (var key = Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam"))
                {
                    if (key != null)
                    {
                        var val = key.GetValue("SteamPath") as string;
                        if (!string.IsNullOrEmpty(val) && Directory.Exists(val))
                        {
                            return Path.GetFullPath(val);
                        }
                    }
                }
            }
            catch { }

            string defaultPath = @"C:\Program Files (x86)\Steam";
            if (Directory.Exists(defaultPath)) return defaultPath;

            return null;
        }
    }
}
