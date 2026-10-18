using Playnite.SDK;
using System.Collections.Generic;
using System.Windows;

namespace Playnite
{
    // Message that the remote Melcosoft services are not reachable or under maintenance,
    // with Retry and Close.
    public static class ServiceUnavailableDialog
    {
        // Shows the message. Returns true when the player chose Retry.
        public static bool Show()
        {
            var retry = new MessageBoxOption(ResourceProvider.GetString("LOCRetryLabel"), true, false);
            var close = new MessageBoxOption(ResourceProvider.GetString("LOCCloseLabel"), false, true);

            var result = PlayniteApplication.Current.Dialogs.ShowMessage(
                ResourceProvider.GetString("LOCServicesUnavailableMessage"),
                ResourceProvider.GetString("LOCServicesUnavailableTitle"),
                MessageBoxImage.Warning,
                new List<MessageBoxOption> { retry, close });

            return result == retry;
        }
    }
}
