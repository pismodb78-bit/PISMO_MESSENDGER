namespace PISMO
{
    /// <summary>
    /// Linux-заглушка WinForms-версии ConnectionGuard (окно «нет связи с БД» с
    /// анимацией и авто-переподключением). DBHelper дёргает NotifyOk/NotifyLost
    /// на каждом открытии/сбое соединения — здесь это тихие no-op, чтобы линкнуть
    /// DBHelper без WinForms. Позже заменим на нативный Avalonia-индикатор связи.
    /// </summary>
    internal static class ConnectionGuard
    {
        public static void NotifyOk() { }
        public static void NotifyLost() { }
    }
}
