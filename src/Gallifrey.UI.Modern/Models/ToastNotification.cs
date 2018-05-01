using Gallifrey.UI.Modern.MainViews;
using ToastNotifications.Core;

namespace Gallifrey.UI.Modern.Models
{
    public class ToastNotification : NotificationBase
    {
        public override NotificationDisplayPart DisplayPart { get; }
        public string Title { get; }
        public string Message { get; }

        public ToastNotification(string title, string message)
        {
            DisplayPart = new ToastNotificationDisplayPart(this);
            Title = title;
            Message = message;
            CanClose = true;
        }
    }
}