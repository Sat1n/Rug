using System.Collections.Specialized;
using System.Web;

using Microsoft.Windows.AppNotifications;
using Microsoft.UI.Xaml.Controls;

using Rug.UI.Contracts.Services;
using Rug.UI.ViewModels;

namespace Rug.UI.Notifications;

public class AppNotificationService : IAppNotificationService, IDisposable
{
    private readonly INavigationService _navigationService;

    public AppNotificationService(INavigationService navigationService)
    {
        _navigationService = navigationService;
    }

    private bool _registered;
    private int _closed;
    private int _queued;
    private ContentDialog? _dialog;
    private Task _callback=Task.CompletedTask;
    private Task? _shutdown;
    private readonly Rug.UI.Helpers.UiCallbackFence _callbacks=new();

    public void Initialize()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _closed)!=0,this);
        if(_registered)return;
        AppNotificationManager.Default.NotificationInvoked += OnNotificationInvoked;

        try { AppNotificationManager.Default.Register(); }
        catch { AppNotificationManager.Default.NotificationInvoked-=OnNotificationInvoked;throw; }
        _registered=true;
    }

    public void OnNotificationInvoked(AppNotificationManager sender, AppNotificationActivatedEventArgs args)
    {
        using var callback=_callbacks.Enter();if(callback is null)return;
        if(Volatile.Read(ref _closed)!=0)return;
        if(Interlocked.CompareExchange(ref _queued,1,0)!=0)return;
        // TODO: Handle notification invocations when your app is already running.

        //// // Navigate to a specific page based on the notification arguments.
        //// if (ParseArguments(args.Argument)["action"] == "Settings")
        //// {
        ////    App.MainWindow.DispatcherQueue.TryEnqueue(() =>
        ////    {
        ////        _navigationService.NavigateTo(typeof(SettingsViewModel).FullName!);
        ////    });
        //// }

        if(!App.MainWindow.DispatcherQueue.TryEnqueue(() =>
        {
            if(Volatile.Read(ref _closed)!=0){Interlocked.Exchange(ref _queued,0);return;}
            var completion=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _callback=completion.Task;
            _=ShowDialogAsync(completion);
        }))Interlocked.Exchange(ref _queued,0);
    }
    private async Task ShowDialogAsync(TaskCompletionSource completion)
    {
        try {
            if(Volatile.Read(ref _closed)==0){
                _dialog=new ContentDialog{Title="Notification Invoked",Content="TODO: Handle notification invocations when your app is already running.",CloseButtonText="OK",XamlRoot=App.MainWindow.Content.XamlRoot};
                App.MainWindow.BringToFront();await _dialog.ShowAsync();
            }
        }
        catch(Exception error){Console.Error.WriteLine("Notification UI callback failed: "+error);}
        finally{_dialog=null;Interlocked.Exchange(ref _queued,0);completion.TrySetResult();}
    }
    public Task ShutdownAsync()=>_shutdown??=ShutdownCoreAsync(); // Owner calls on the UI thread.
    private async Task ShutdownCoreAsync()
    {var callbacks=_callbacks.Close();try{Unregister();}finally{await callbacks;_dialog?.Hide();await _callback;}}

    public bool Show(string payload)
    {
        if(Volatile.Read(ref _closed)!=0)return false;
        var appNotification = new AppNotification(payload);

        AppNotificationManager.Default.Show(appNotification);

        return appNotification.Id != 0;
    }

    public NameValueCollection ParseArguments(string arguments)
    {
        return HttpUtility.ParseQueryString(arguments);
    }

    public void Unregister()
    {
        if(Interlocked.Exchange(ref _closed,1)!=0)return;
        if(!_registered)return;
        var errors=new List<Exception>();
        try { AppNotificationManager.Default.NotificationInvoked-=OnNotificationInvoked; } catch(Exception error){errors.Add(error);}
        _registered=false;
        try { AppNotificationManager.Default.Unregister(); } catch(Exception error){errors.Add(error);}
        if(errors.Count>0)throw new AggregateException("Notification cleanup failed",errors);
    }
    public void Dispose()=>Unregister();
}
