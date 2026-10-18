using Playnite.SDK;
using Playnite.ViewModels;
using Playnite.Windows;
using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;

namespace Playnite
{
    // Mandatory launcher update: while a newer release with "mandatory": true is pending,
    // games cannot be started, installed or repaired. The update window then offers only
    // Update or Exit.
    public static class MandatoryUpdate
    {
        private static readonly ILogger logger = LogManager.GetLogger();

        // The check before a game action must not keep the player waiting long when the
        // release server is down, and is not repeated more often than once a minute.
        private const int GateCheckTimeoutMs = 5_000;
        private static readonly TimeSpan GateCheckCacheTtl = TimeSpan.FromSeconds(60);

        private static readonly object stateLock = new object();
        private static DateTime lastCheckAtUtc = DateTime.MinValue;
        private static int windowOpen = 0;

        // True after a successful check found a mandatory update in this session. A failed
        // check does not change it: a mandatory release seen once stays required.
        public static bool IsRequired { get; private set; }

        // Saves the result of a successful check.
        public static void SetState(bool mandatory)
        {
            lock (stateLock)
            {
                IsRequired = mandatory;
                lastCheckAtUtc = DateTime.UtcNow;
            }
        }

        // Runs a live release check. Returns false when the check failed; the state is then unchanged.
        public static bool Refresh(int timeoutMs)
        {
            var updater = new Updater(PlayniteApplication.Current) { CheckTimeoutMs = timeoutMs };
            if (!updater.TryGetMandatoryState(out var mandatory))
            {
                return false;
            }

            SetState(mandatory);
            return true;
        }

        // Gate for game actions (play, install, repair, resume). Returns true when the action
        // may continue. When an update is required, opens the update window and returns false.
        // When a required update was seen earlier but the services are not reachable now,
        // shows the "services unavailable" message with Retry.
        public static bool EnsureNotRequired()
        {
            var forceCheck = false;
            while (true)
            {
                var checkOk = true;
                if (forceCheck || (DateTime.UtcNow - lastCheckAtUtc) >= GateCheckCacheTtl)
                {
                    checkOk = RunPumping(() => Refresh(GateCheckTimeoutMs));
                }

                // No mandatory update known: fail open, also when the check failed.
                if (!IsRequired)
                {
                    return true;
                }

                if (checkOk)
                {
                    ShowBlockingWindow();
                    return false;
                }

                if (!ServiceUnavailableDialog.Show())
                {
                    return false;
                }

                forceCheck = true;
            }
        }

        // Opens the update window in mandatory mode on the UI thread. Only one window is open at
        // a time. Closing the window in any other way than installing the update quits the app.
        // Called from a background thread, it does not wait for the window.
        public static void ShowBlockingWindow()
        {
            if (Interlocked.Exchange(ref windowOpen, 1) == 1)
            {
                return;
            }

            var dispatcher = PlayniteApplication.CurrentNative.Dispatcher;
            if (dispatcher.CheckAccess())
            {
                OpenWindow();
            }
            else
            {
                dispatcher.BeginInvoke(new Action(OpenWindow));
            }
        }

        private static void OpenWindow()
        {
            try
            {
                var app = PlayniteApplication.Current;
                if (app.ResourcesReleased)
                {
                    return;
                }

                logger.Info("Mandatory launcher update is pending, opening the update window.");
                var model = new UpdateViewModel(
                    new Updater(app),
                    new UpdateWindowFactory(),
                    new ResourceProvider(),
                    app.Dialogs,
                    app.Mode,
                    isMandatory: true);

                // True only when the update was started: the app then quits for the updater itself.
                if (model.OpenView() != true && !app.ResourcesReleased)
                {
                    logger.Info("Mandatory update window closed without update, quitting.");
                    app.Quit();
                }
            }
            catch (Exception e) when (!PlayniteEnvironment.ThrowAllErrors)
            {
                logger.Error(e, "Failed to show the mandatory update window.");
            }
            finally
            {
                Interlocked.Exchange(ref windowOpen, 0);
            }
        }

        // Runs work on a worker thread. On the UI thread the message loop keeps running
        // meanwhile, so the window does not freeze while the check waits for its timeout.
        private static bool RunPumping(Func<bool> work)
        {
            var dispatcher = PlayniteApplication.CurrentNative?.Dispatcher;
            if (dispatcher == null || !dispatcher.CheckAccess())
            {
                return work();
            }

            var frame = new DispatcherFrame();
            var task = Task.Run(work);
            task.ContinueWith(_ => dispatcher.BeginInvoke(new Action(() => frame.Continue = false)));
            Dispatcher.PushFrame(frame);

            try
            {
                return task.Result;
            }
            catch (Exception e)
            {
                logger.Warn(e, "Mandatory update check failed.");
                return false;
            }
        }
    }
}
