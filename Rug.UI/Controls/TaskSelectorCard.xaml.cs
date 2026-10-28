using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Rug.UI.Core.Schema;

namespace Rug.UI.Controls;

public sealed partial class TaskSelectorCard : UserControl
{
    private readonly List<SchemaTask> _tasks = [];
    private readonly HashSet<string> _selected = new(StringComparer.Ordinal);
    private bool _updating;

    public event EventHandler<string[]>? SelectionChanged;
    public event EventHandler? StartRequested;

    public TaskSelectorCard() => InitializeComponent();

    public void SetTasks(IEnumerable<SchemaTask> tasks)
    {
        _tasks.Clear();
        _tasks.AddRange(tasks);
        _selected.IntersectWith(_tasks.Select(task => task.Id));
        Render();
    }

    public void SetSelection(IEnumerable<string>? ids)
    {
        _updating = true;
        _selected.Clear();
        foreach (string id in ids ?? [])
            if (_tasks.Any(task => task.Id == id)) _selected.Add(id);
        Render();
        _updating = false;
    }

    public string[] SelectedIds => _tasks.Where(task => _selected.Contains(task.Id))
        .Select(task => task.Id).ToArray();

    private void Render()
    {
        TaskList.Children.Clear();
        foreach (SchemaTask task in _tasks)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
            var checkbox = new CheckBox { Content = task.Title, IsChecked = _selected.Contains(task.Id), MinWidth = 180 };
            checkbox.Checked += (_, _) => ChangeSelection(task.Id, true);
            checkbox.Unchecked += (_, _) => ChangeSelection(task.Id, false);
            var up = new Button { Content = "↑", IsEnabled = _tasks.IndexOf(task) > 0 };
            var down = new Button { Content = "↓", IsEnabled = _tasks.IndexOf(task) < _tasks.Count - 1 };
            up.Click += (_, _) => Move(task, -1);
            down.Click += (_, _) => Move(task, 1);
            row.Children.Add(checkbox);
            row.Children.Add(up);
            row.Children.Add(down);
            TaskList.Children.Add(row);
        }
    }

    private void ChangeSelection(string id, bool selected)
    {
        if (_updating) return;
        if (selected) _selected.Add(id);
        else _selected.Remove(id);
        SelectionChanged?.Invoke(this, SelectedIds);
    }

    private void Move(SchemaTask task, int delta)
    {
        int index = _tasks.IndexOf(task);
        int next = index + delta;
        if (next < 0 || next >= _tasks.Count) return;
        _tasks.RemoveAt(index);
        _tasks.Insert(next, task);
        Render();
        SelectionChanged?.Invoke(this, SelectedIds);
    }

    private void SelectAll_Click(object sender, RoutedEventArgs e)
    {
        SetSelection(_tasks.Select(task => task.Id));
        SelectionChanged?.Invoke(this, SelectedIds);
    }

    private void Clear_Click(object sender, RoutedEventArgs e)
    {
        SetSelection([]);
        SelectionChanged?.Invoke(this, SelectedIds);
    }

    private void Start_Click(object sender, RoutedEventArgs e) => StartRequested?.Invoke(this, EventArgs.Empty);
}
