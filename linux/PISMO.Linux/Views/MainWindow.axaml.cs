using Avalonia.Controls;

namespace PISMO.Linux.Views
{
    public partial class MainWindow : Window
    {
        public MainWindow()
        {
            InitializeComponent();
            // Показываем, кто вошёл — подтверждение, что сессия реально поднялась.
            string who = UserSession.EffectiveName;
            string role = string.IsNullOrEmpty(UserSession.Role) ? "" : $" · {UserSession.Role}";
            LblHello.Text = $"Вы вошли как {who}{role} (id {UserSession.EffectiveId})";
            LblWho.Text = who;
        }
    }
}
