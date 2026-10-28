using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Rug.UI.Core.Schema;

namespace Rug.UI.Controls;

public sealed partial class StepFlowView : UserControl
{
    public StepFlowView() => InitializeComponent();

    public void SetSteps(IEnumerable<SchemaStep> steps)
    {
        StepsPanel.Children.Clear();
        foreach (SchemaStep step in steps)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12 };
            var status = new FontIcon
            {
                Glyph = step.Status switch
                {
                    "Succeeded" => "\uE73E", "Failed" => "\uEA39", "Running" => "\uE895", _ => "\uE916"
                },
                FontSize = 18,
                VerticalAlignment = VerticalAlignment.Top
            };
            row.Children.Add(status);
            var body = new StackPanel { Spacing = 4 };
            body.Children.Add(new TextBlock { Text = step.Title, TextWrapping = TextWrapping.Wrap });
            body.Children.Add(new TextBlock { Text = step.Status, Opacity = 0.65 });
            if (step.Thumbnail is { Length: > 0 } uri && Uri.TryCreate(uri, UriKind.Absolute, out var parsed)
                && parsed.Scheme is "ms-appx" or "ms-appdata")
            {
                body.Children.Add(new Image
                {
                    Source = new BitmapImage(parsed), Width = 120, Height = 72,
                    Stretch = Stretch.UniformToFill, HorizontalAlignment = HorizontalAlignment.Left
                });
            }
            row.Children.Add(body);
            StepsPanel.Children.Add(row);
        }
    }
}
