// Only for executing the actual notification service in deterministic tests; not native toast evidence.
namespace Rug.UI.Contracts.Services { public interface INavigationService; }
namespace Microsoft.Windows.AppNotifications
{
    public sealed class AppNotificationActivatedEventArgs : EventArgs;
    public sealed class AppNotification {public AppNotification(string payload){}public uint Id{get;set;} }
    public sealed class AppNotificationManager
    {
        public static AppNotificationManager Default{get;}=new();
        public event Action<AppNotificationManager,AppNotificationActivatedEventArgs>? NotificationInvoked;
        public int Registers,Unregisters;public bool FailUnregister;
        public int Subscribers=>NotificationInvoked?.GetInvocationList().Length??0;
        public void Register()=>Registers++;
        public void Unregister(){Unregisters++;if(FailUnregister)throw new IOException("notification cleanup failure");}
        public void Show(AppNotification notification)=>notification.Id=1;
        public void Raise()=>NotificationInvoked?.Invoke(this,new());
        public void Reset(){NotificationInvoked=null;Registers=Unregisters=0;FailUnregister=false;}
    }
}
namespace Rug.UI
{
    internal sealed class NotificationTestWindow
    {
        internal Microsoft.UI.Xaml.Controls.TestDispatcher DispatcherQueue{get;}=new();
        internal NotificationTestContent Content{get;}=new();
        public void BringToFront(){}
    }
    internal sealed class NotificationTestContent{public object XamlRoot{get;}=new();}
}
namespace Microsoft.UI.Xaml.Controls
{
    public sealed class ContentDialog
    {
        public static int Created;public static ContentDialog? Last;
        public ContentDialog(){Created++;Last=this;}
        public object? Title{get;set;}public object? Content{get;set;}public string? CloseButtonText{get;set;}public object? XamlRoot{get;set;}
        private readonly TaskCompletionSource _completion=new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool Hidden;
        public Task ShowAsync()=>_completion.Task;
        public void Hide()=>Hidden=true;
        public void Finish()=>_completion.TrySetResult();
    }
}
