using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;

namespace OpenSlideSharp.Viewer;

/// <summary>The Avalonia application: the Fluent theme (App.axaml) and the main window.</summary>
public partial class App : Application
{
    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            // Placeholder until the main window exists.
            desktop.MainWindow = new Window { Title = "OpenSlide Viewer", Width = 1024, Height = 768 };
        }

        base.OnFrameworkInitializationCompleted();
    }
}
