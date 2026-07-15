using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AuroraSuite
{
    public class Settings
    {
        /// <summary>
        /// Which protocol the Sync tab uses. FTP stays the default so nothing changes for
        /// existing setups; XBDM is the one that needs no username or password.
        /// </summary>
        [JsonConverter(typeof(JsonStringEnumConverter))]
        public TransportKind Transport { get; set; } = TransportKind.Ftp;

        // ---- Console connection (FTP) ----
        public string Ip { get; set; } = "192.168.4.128";
        public int Port { get; set; } = 21;

        /// <summary>
        /// FTP only, and only if the server asks for them. Aurora's FTP server usually does,
        /// which is the whole reason XBDM exists as an option here. Leave blank for
        /// anonymous, or switch Transport to Xbdm to skip credentials entirely.
        /// </summary>
        public string Username { get; set; } = "";
        public string Password { get; set; } = "";

        /// <summary>
        /// Accepted in either style; the XBDM transport translates as needed.
        ///     FTP  : /Hdd1/Aurora_0.7b/Data/GameData
        ///     XBDM : Hdd:\Aurora_0.7b\Data\GameData
        /// </summary>
        public string AuroraGameDataPath { get; set; } = "/Hdd1/Aurora_0.7b/Data/GameData";

        // ---- Console connection (XBDM) ----
        /// <summary>Name or IP of the console for XBDM. Blank means "use the IP above".</summary>
        public string XbdmTarget { get; set; } = "";
        public int XbdmPort { get; set; } = XbdmClient.DefaultPort;

        // ---- Sync tab ----
        public string LibraryPath { get; set; } = "";
        public List<string> AssetPrefixes { get; set; } = new List<string> { "GC" };
        public bool OnlyOverwriteExisting { get; set; } = true;

        // ---- Image Assets tab ----
        public string ImageAssetsSourcePath { get; set; } = "";
        public string ImageAssetsOutputPath { get; set; } = "";
        public bool ConvertIcon { get; set; } = false;
        public bool ConvertBoxart { get; set; } = true;
        public bool ConvertBackground { get; set; } = false;
        public bool ConvertScreenshots { get; set; } = true;

        private static string SettingsPath
        {
            get
            {
                var dir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "AuroraSuite");
                Directory.CreateDirectory(dir);
                return Path.Combine(dir, "settings.json");
            }
        }

        public static string DefaultImageAssetsSourcePath =>
            Path.Combine(AppContext.BaseDirectory, "Image_Assets");

        public static string DefaultImageAssetsOutputPath =>
            Path.Combine(AppContext.BaseDirectory, "Output");

        public static Settings Load()
        {
            try
            {
                if (File.Exists(SettingsPath))
                {
                    var json = File.ReadAllText(SettingsPath);
                    var loaded = JsonSerializer.Deserialize<Settings>(json);
                    if (loaded != null)
                    {
                        if (string.IsNullOrWhiteSpace(loaded.ImageAssetsSourcePath))
                            loaded.ImageAssetsSourcePath = DefaultImageAssetsSourcePath;
                        if (string.IsNullOrWhiteSpace(loaded.ImageAssetsOutputPath))
                            loaded.ImageAssetsOutputPath = DefaultImageAssetsOutputPath;
                        // A settings.json written before XBDM support existed has no
                        // XbdmPort, which deserializes as 0 and would never connect.
                        if (loaded.XbdmPort <= 0 || loaded.XbdmPort > 65535)
                            loaded.XbdmPort = XbdmClient.DefaultPort;
                        return loaded;
                    }
                }
            }
            catch
            {
            }

            return new Settings
            {
                ImageAssetsSourcePath = DefaultImageAssetsSourcePath,
                ImageAssetsOutputPath = DefaultImageAssetsOutputPath,
            };
        }

        public void Save()
        {
            var options = new JsonSerializerOptions { WriteIndented = true };
            var json = JsonSerializer.Serialize(this, options);
            File.WriteAllText(SettingsPath, json);
        }
    }
}
