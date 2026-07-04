namespace PISMO
{
    /// <summary>Статическое хранилище данных залогиненного пользователя.</summary>
    public static class UserSession
    {
        public static int    UserId       { get; set; } = 0;
        public static string UserName     { get; set; } = "";
        public static string Role         { get; set; } = "";   // "admin" | "teacher"

        /// <summary>JWT текущей сессии (выдаётся при входе, используется WS-сервером).</summary>
        public static string AuthToken    { get; set; } = "";

        // Для режима «войти за пользователя» (только admin)
        public static int    ImpersonatedId   { get; set; } = 0;
        public static string ImpersonatedName { get; set; } = "";

        public static bool IsImpersonating => ImpersonatedId > 0;

        /// <summary>Эффективный ID с учётом impersonation</summary>
        public static int    EffectiveId   => IsImpersonating ? ImpersonatedId   : UserId;
        public static string EffectiveName => IsImpersonating ? ImpersonatedName : UserName;

        /// <summary>Действует ли админ ОТ СЕБЯ (для обхода приватности ЛС).
        /// В режиме «войти за пользователя» привилегия НЕ действует — иначе
        /// через админа обходились бы правила «писать могут только друзья».</summary>
        public static bool IsAdminActing => Role == "admin" && !IsImpersonating;

        public static void Clear()
        {
            UserId = 0;
            UserName = "";
            Role = "";
            AuthToken = "";
            StopImpersonating();
        }

        public static void StopImpersonating()
        {
            ImpersonatedId = 0;
            ImpersonatedName = "";
        }
    }
}
