using BetterTranslator.Mac.Seams;

namespace BetterTranslator.App.Notifications;

public static class UpdateNotifications
{
    public static void RemoveRegistration()
    {
        if (Platform.IsInstalled)
        {
            Platform.Current.Notifications.WithdrawRegistration();
        }
    }
}
