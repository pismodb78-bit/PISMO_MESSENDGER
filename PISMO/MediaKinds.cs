using System;

namespace PISMO
{
    /// <summary>
    /// Какие расширения приложение умеет проигрывать само (декодерами Chromium
    /// в WebView2). Раньше эти проверки жили в MediaPlayerForm — отдельном окне
    /// плеера; окно убрано, воспроизведение идёт прямо в пузыре сообщения
    /// (см. InlineVideoPlayer), а списки форматов остались нужны и здесь.
    /// </summary>
    internal static class MediaKinds
    {
        private static readonly string[] AudioExt =
            { "mp3", "wav", "ogg", "oga", "m4a", "aac", "flac", "opus", "weba" };
        private static readonly string[] VideoExt =
            { "mp4", "webm", "m4v", "ogv", "mov" };

        public static bool IsAudio(string ext) => Array.IndexOf(AudioExt, ext) >= 0;
        public static bool IsVideo(string ext) => Array.IndexOf(VideoExt, ext) >= 0;
        public static bool IsMedia(string ext) => IsAudio(ext) || IsVideo(ext);
    }
}
