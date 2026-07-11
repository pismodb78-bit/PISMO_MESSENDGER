using System;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;

namespace PISMO.Linux.Views
{
    public partial class LoginWindow : Window
    {
        public LoginWindow()
        {
            InitializeComponent();
            // Enter в любом поле — вход.
            TxtLogin.KeyDown += OnKey;
            TxtPassword.KeyDown += OnKey;
            Opened += (_, _) => TxtLogin.Focus();
        }

        private void OnKey(object? sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter) OnLoginClick(sender, new RoutedEventArgs());
        }

        private async void OnLoginClick(object? sender, RoutedEventArgs e)
        {
            LblError.IsVisible = false;
            BtnLogin.IsEnabled = false;
            LblStatus.Text = "Подключение к серверу…";

            string login = TxtLogin.Text ?? "";
            string password = TxtPassword.Text ?? "";

            // Логин (БД + проверка пароля) — в фоне, чтобы окно не подвисало.
            var result = await Task.Run(() => AuthService.Login(login, password));

            if (result.Ok)
            {
                var main = new MainWindow();
                main.Show();
                Close();
                return;
            }

            LblError.Text = result.Error ?? "Не удалось войти.";
            LblError.IsVisible = true;
            LblStatus.Text = "PISMO для Linux · сборка 0.1";
            BtnLogin.IsEnabled = true;
        }
    }
}
