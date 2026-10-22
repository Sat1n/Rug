// Minimal generated-XAML/control doubles for executing the actual page code-behind.
// These test-only types are not UI automation or evidence of native rendering.
using System.Collections.Concurrent;

namespace Microsoft.UI.Xaml
{
    public class RoutedEventArgs : EventArgs;
    public delegate void RoutedEventHandler(object sender,RoutedEventArgs args);
}
namespace Microsoft.UI.Xaml.Controls
{
    public class SelectionChangedEventArgs : Microsoft.UI.Xaml.RoutedEventArgs;
    public class TextChangedEventArgs : Microsoft.UI.Xaml.RoutedEventArgs;
    public sealed class TestDispatcher
    {
        private readonly ConcurrentQueue<Action> _queue=new();
        public bool TryEnqueue(Action action){_queue.Enqueue(action);return true;}
        public int Count=>_queue.Count;
        public void Drain(){while(_queue.TryDequeue(out var action))action();}
    }
    public class Page
    {
        public event Microsoft.UI.Xaml.RoutedEventHandler? Loaded,Unloaded;
        public TestDispatcher DispatcherQueue { get; }=new();
        public void Load()=>Loaded?.Invoke(this,new());
        public void Unload()=>Unloaded?.Invoke(this,new());
    }
    public class TextBlock { public string Text { get; set; }=""; }
    public class TextBox : TextBlock;
    public class Button { public bool IsEnabled { get; set; } }
    public class ListViewItem { public object? Content { get; set; } public object? Tag { get; set; } }
    public class ListView
    {
        private object[] _items=[];
        private int _index=-1;
        public event Action<object,SelectionChangedEventArgs>? SelectionChanged;
        public object? ItemsSource
        {
            get=>_items;
            set{_items=(value as System.Collections.IEnumerable)?.Cast<object>().ToArray()??[];SelectedIndex=-1;}
        }
        public object? SelectedItem=>_index<0?null:_items[_index];
        public int SelectedIndex
        {
            get=>_index;
            set{if(value==_index)return;_index=value;SelectionChanged?.Invoke(this,new());}
        }
    }
}
namespace Rug.UI
{
    internal static class App
    {
        internal static object? TestService;
        public static T GetService<T>()=>(T)TestService!;
    }
}
