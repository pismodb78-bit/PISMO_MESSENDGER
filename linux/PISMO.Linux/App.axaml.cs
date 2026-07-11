using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using PISMO.Linux.Views;

namespace PISMO.Linux
{
    public partial class App : Application
    {
        public override void Initialize() => AvaloniaXamlLoader.Load(this);

        public override void OnFrameworkInitializationCompleted()
        {
            if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            {
                // Стартуем с окна входа; после успешного логина оно само
                // откроет главное окно и закроется (см. LoginWindow).
                desktop.MainWindow = new LoginWindow();
            }
            base.OnFrameworkInitializationCompleted();
        }
    }
}
