using Playnite.SDK;
using Playnite.Commands;
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Windows;
using Playnite.Windows;

namespace Playnite.ViewModels
{
    public class UpdateViewModel : ObservableObject
    {
        private static ILogger logger = LogManager.GetLogger();
        private IWindowFactory window;
        private Updater updater;
        private IResourceProvider resources;
        private IDialogsFactory dialogs;
        private ApplicationMode mode;
        private readonly SynchronizationContext context;

        private int updateProgress;
        public int UpdateProgress
        {
            get => updateProgress;
            set
            {
                updateProgress = value;
                OnPropertyChanged();
            }
        }

        private bool showProgress;
        public bool ShowProgress
        {
            get => showProgress;
            set
            {
                showProgress = value;
                OnPropertyChanged();
            }
        }

        public RelayCommand<object> CloseCommand
        {
            get => new RelayCommand<object>((a) =>
            {
                CloseView();
            });
        }

        public RelayCommand<object> InstallUpdateCommand
        {
            get => new RelayCommand<object>((a) =>
            {
                InstallUpdate();
            });
        }

        public List<ReleaseNoteData> ReleaseNotes
        {
            get;
            private set;
        }

        public string CurrentRegionDisplay { get; private set; }

        private static string ReadCurrentRegion()
        {
            try
            {
                var configPath = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                    "Melcosoft", "config.env");

                if (!File.Exists(configPath))
                    return "EU";

                foreach (var line in File.ReadAllLines(configPath))
                {
                    var trimmed = line.Trim();
                    if (trimmed.StartsWith("REGION", StringComparison.OrdinalIgnoreCase))
                    {
                        var eq = trimmed.IndexOf('=');
                        if (eq < 0) continue;
                        var code = trimmed.Substring(eq + 1).Trim().Trim('\'').Trim('"').ToUpperInvariant();
                        if (code.Length > 0) return code;
                    }
                }
            }
            catch { }
            return "EU";
        }

        private string BuildRegionDisplay(string code)
        {
            string nameKey;
            switch (code)
            {
                case "EU": nameKey = "LOCMelcosoftRegionEurope"; break;
                case "RU": nameKey = "LOCMelcosoftRegionRussia"; break;
                case "NA": nameKey = "LOCMelcosoftRegionSouthAmerica"; break;
                case "CN": nameKey = "LOCMelcosoftRegionChina"; break;
                default:   nameKey = null; break;
            }

            var name = nameKey != null ? resources.GetString(nameKey) : null;
            if (string.IsNullOrEmpty(name)) name = code;

            var format = resources.GetString("LOCMelcosoftSelectedRegion");
            if (string.IsNullOrEmpty(format)) format = "Selected download region: {0}";

            return string.Format(format, name);
        }

        public UpdateViewModel(
            Updater updater,
            IWindowFactory window,
            IResourceProvider resources,
            IDialogsFactory dialogs,
            ApplicationMode mode)
        {
            context = SynchronizationContext.Current;
            this.window = window;
            this.updater = updater;
            this.resources = resources;
            this.dialogs = dialogs;
            this.mode = mode;

            CurrentRegionDisplay = BuildRegionDisplay(ReadCurrentRegion());

            try
            {
                ReleaseNotes = updater.GetReleaseNotes();
            }
            catch (Exception e) when (!PlayniteEnvironment.ThrowAllErrors)
            {
                logger.Error(e, "Failed to download release notes.");
            }
        }

        public bool? OpenView()
        {
            return window.CreateAndOpenDialog(this);
        }

        public void CloseView()
        {
            window.Close();
        }

        public async void InstallUpdate()
        {
            if (GlobalTaskHandler.IsActive)
            {
                if (dialogs.ShowMessage(resources.GetString("LOCUpdateProgressCancelAsk"), "", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes)
                {
                    dialogs.ActivateGlobalProgress((_) =>
                    {
                        try
                        {
                            GlobalTaskHandler.CancelAndWait();
                        }
                        catch (Exception exc) when (!PlayniteEnvironment.ThrowAllErrors)
                        {
                            logger.Error(exc, "Failed to cancel global progress task.");
                            throw;
                        }
                    }, new GlobalProgressOptions("LOCProgressReleasingResources"));
                }
                else
                {
                    window.Close(false);
                    return;
                }
            }

            try
            {
                ShowProgress = true;
                UpdateProgress = 0;

                await updater.DownloadUpdate(percent =>
                {
                    context.Post(_ =>
                    {
                        UpdateProgress = percent;
                    }, null);
                });

                UpdateProgress = 100;
                updater.InstallUpdate(mode);
                window.Close(true);
            }
            catch (Exception exc) when (!PlayniteEnvironment.ThrowAllErrors)
            {
                ShowProgress = false;
                logger.Error(exc, "Failed to download and install update.");
                dialogs.ShowMessage(
                    resources.GetString("LOCGeneralUpdateFailMessage") + $"\n{exc.Message}",
                    resources.GetString("LOCUpdateError"),
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);

                window.Close(false);
                return;
            }
        }
    }
}
