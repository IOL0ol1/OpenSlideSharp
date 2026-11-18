using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;

namespace OpenSlideSharp.Viewer;

/// <summary>
/// The Avalonia application: the Fluent theme (App.axaml) and the main window, which opens the slide given as the
/// first command-line argument.
/// </summary>
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
            var window = new MainWindow();
            desktop.MainWindow = window;
            if (desktop.Args is [string path, ..])
            {
                // A slide that cannot be opened is reported in the window, not thrown.
                _ = window.OpenAsync(path);
            }
        }

        base.OnFrameworkInitializationCompleted();
    }
}
