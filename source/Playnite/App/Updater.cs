using System;
using System.Collections.Generic;
using System.Configuration;
using System.IO;
using System.Net;
using System.Text;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Playnite.Common;
using Playnite.Common.Web;
using Playnite.Settings;
using Playnite.SDK;

namespace Playnite
{
    public class Updater
    {
        // Hardcode local backend endpoint.
        private const string BackendBaseUrl = "http://127.0.0.1:17877";
        private const string CheckEndpoint = "/launcher/release/check";
        private const string DownloadEndpoint = "/launcher/release/download";
        private const string PrepareUpdaterEndpoint = "/launcher/release/prepare_updater";

        private static ILogger logger = LogManager.GetLogger();

        private IPlayniteApplication playniteApp;
        private IDownloader downloader;

        // Cache check response to avoid calling backend too often inside same window open
        private BackendManifest cachedManifest;
        private DateTime cachedManifestAtUtc = DateTime.MinValue;
        private static readonly TimeSpan CacheTtl = TimeSpan.FromSeconds(10);

        private static string updateBranch
        {
            get
            {
                return ConfigurationManager.AppSettings["UpdateBranch"];
            }
        }

        private static Version currentVersion;
        public static Version CurrentVersion
        {
            get
            {
                if (currentVersion == null)
                {
                    currentVersion = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version;
                }

                return currentVersion;
            }
        }

        // Backend manifest shape (subset)
        private sealed class BackendManifest
        {
            [JsonProperty("pending_update")]
            public bool PendingUpdate { get; set; }

            [JsonProperty("pending_version")]
            public string PendingVersion { get; set; }

            [JsonProperty("pending_release_notes")]
            public string PendingReleaseNotes { get; set; }

            [JsonProperty("update_state")]
            public string UpdateState { get; set; }

            [JsonProperty("pending_package_path")]
            public string PendingPackagePath { get; set; }

            [JsonProperty("install_dir")]
            public string InstallDir { get; set; }
        }

        private sealed class PrepareUpdaterResponse
        {
            [JsonProperty("ok")]
            public bool Ok { get; set; }

            [JsonProperty("error")]
            public string Error { get; set; }

            [JsonProperty("version")]
            public string Version { get; set; }

            [JsonProperty("package")]
            public string Package { get; set; }

            [JsonProperty("install_dir")]
            public string InstallDir { get; set; }

            [JsonProperty("updater_path")]
            public string UpdaterPath { get; set; }
        }

        public Updater(IPlayniteApplication app) : this(app, new Downloader())
        {
        }

        public Updater(IPlayniteApplication app, IDownloader webDownloader)
        {
            playniteApp = app;
            downloader = webDownloader;
        }

        public bool IsUpdateAvailable
        {
            get
            {
                var latest = GetLatestVersion();
                var current = CurrentVersion;
                if (latest > current)
                {
                    // Windows 7 and 8 and 32bit systems should no longer update, except for patches
                    if (Computer.WindowsVersion == WindowsVersion.Win7 || Computer.WindowsVersion == WindowsVersion.Win8 || !Environment.Is64BitOperatingSystem)
                    {
                        return latest.Major == current.Major;
                    }
                    else
                    {
                        return true;
                    }
                }

                return false;
            }
        }

        public Version GetLatestVersion()
        {
            var m = EnsureBackendChecked();
            if (m == null)
            {
                return CurrentVersion;
            }

            if (!m.PendingUpdate || string.IsNullOrWhiteSpace(m.PendingVersion))
            {
                return CurrentVersion;
            }

            if (Version.TryParse(m.PendingVersion.Trim(), out var v))
            {
                return v;
            }

            return CurrentVersion;
        }

        public List<ReleaseNoteData> GetReleaseNotes()
        {
            var notes = new List<ReleaseNoteData>();
            var m = EnsureBackendChecked();
            if (m == null)
            {
                return notes;
            }

            if (!m.PendingUpdate || string.IsNullOrWhiteSpace(m.PendingVersion))
            {
                return notes;
            }

            if (!Version.TryParse(m.PendingVersion.Trim(), out var v))
            {
                v = CurrentVersion;
            }

            var rn = m.PendingReleaseNotes ?? string.Empty;

            notes.Add(new ReleaseNoteData()
            {
                Version = v,
                Note = rn
            });

            return notes;
        }

        public async Task DownloadUpdate(Action<DownloadProgressChangedEventArgs> progressHandler)
        {
            try
            {
                // Notify start (0%)
                progressHandler?.Invoke(null);

                await PostJsonAsync(BackendBaseUrl + DownloadEndpoint, "{}");

                // Notify finish (100%)
                progressHandler?.Invoke(null);
            }
            catch (Exception e)
            {
                logger.Error(e, "Failed to trigger backend download.");
                throw new Exception("Failed to download update file.");
            }
        }

        public void InstallUpdate(ApplicationMode mode)
        {
            // 1) Ask backend to prepare Melcosoft.Updater.exe
            PrepareUpdaterResponse prep;
            try
            {
                var json = PostJson(BackendBaseUrl + PrepareUpdaterEndpoint, "{}");
                prep = JsonConvert.DeserializeObject<PrepareUpdaterResponse>(json);
            }
            catch (Exception e)
            {
                logger.Error(e, "prepare_updater call failed.");
                throw new Exception("Failed to prepare updater.");
            }

            if (prep == null || !prep.Ok)
            {
                var err = prep?.Error ?? "prepare_updater_failed";
                throw new Exception(err);
            }

            if (string.IsNullOrWhiteSpace(prep.UpdaterPath) ||
                string.IsNullOrWhiteSpace(prep.Version) ||
                string.IsNullOrWhiteSpace(prep.Package) ||
                string.IsNullOrWhiteSpace(prep.InstallDir))
            {
                throw new Exception("prepare_updater returned invalid payload.");
            }

            if (!File.Exists(prep.UpdaterPath))
            {
                throw new Exception($"updater_exe_missing: {prep.UpdaterPath}");
            }

            // 2) Build args for the Melcosoft.Updater.exe
            var args = $"--apply --version \"{prep.Version}\" --package \"{prep.Package}\" --install-dir \"{prep.InstallDir}\"";

            logger.Info($"Starting Melcosoft updater: {prep.UpdaterPath} {args}");

            // 3) Elevate
            playniteApp.QuitAndStart(prep.UpdaterPath, args, true);
        }

        private BackendManifest EnsureBackendChecked()
        {
            if (cachedManifest != null && (DateTime.UtcNow - cachedManifestAtUtc) < CacheTtl)
            {
                return cachedManifest;
            }

            try
            {
                var json = PostJson(BackendBaseUrl + CheckEndpoint, "{}");
                var m = JsonConvert.DeserializeObject<BackendManifest>(json);
                cachedManifest = m;
                cachedManifestAtUtc = DateTime.UtcNow;
                return m;
            }
            catch (Exception e)
            {
                logger.Warn(e, "Failed to check updates via backend.");
                return null;
            }
        }

        private static async Task<string> PostJsonAsync(string url, string body)
        {
            var req = (HttpWebRequest)WebRequest.Create(url);
            req.Method = "POST";
            req.ContentType = "application/json";
            req.Timeout = 30_000;

            var bytes = Encoding.UTF8.GetBytes(body ?? "{}");
            using (var stream = await req.GetRequestStreamAsync().ConfigureAwait(false))
            {
                await stream.WriteAsync(bytes, 0, bytes.Length).ConfigureAwait(false);
            }

            using (var resp = (HttpWebResponse)await req.GetResponseAsync().ConfigureAwait(false))
            using (var reader = new StreamReader(resp.GetResponseStream()))
            {
                return await reader.ReadToEndAsync().ConfigureAwait(false);
            }
        }

        private static string PostJson(string url, string body)
        {
            var req = (HttpWebRequest)WebRequest.Create(url);
            req.Method = "POST";
            req.ContentType = "application/json";
            req.Timeout = 30_000;

            var bytes = Encoding.UTF8.GetBytes(body ?? "{}");
            using (var stream = req.GetRequestStream())
            {
                stream.Write(bytes, 0, bytes.Length);
            }

            using (var resp = (HttpWebResponse)req.GetResponse())
            using (var reader = new StreamReader(resp.GetResponseStream()))
            {
                return reader.ReadToEnd();
            }
        }

        // Unused, kept for compatibility
        private string GetUpdateDataRootUrl(string configKey)
        {
            return string.Empty;
        }

        // Unused, kept for compatibility
        public UpdateManifest DownloadManifest()
        {
            EnsureBackendChecked();
            return null;
        }
    }
}
