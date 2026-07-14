using System;
using System.IO;
using System.Threading.Tasks;
using Microsoft.Web.WebView2.Core;

namespace PISMO
{
    /// <summary>
    /// ЕДИНОЕ окружение WebView2 на весь процесс. КРИТИЧНО: WebView2 запрещает в
    /// одном процессе несколько CoreWebView2Environment с РАЗНЫМИ опциями — вторая
    /// такая инициализация падает с 0x8007139F (ERROR_INVALID_STATE). Раньше плееры
    /// GIF/видео создавали дефолтное окружение (EnsureCoreWebView2Async(null)), а
    /// движок звонка — своё с флагами → конфликт, и звонок не поднимался, если до
    /// него открывали медиа. Теперь ВСЕ (плееры и транспорт) берут ОДНО окружение
    /// отсюда — с флагами, нужными для звонка (они безвредны для плееров).
    /// </summary>
    internal static class WebViewShared
    {
        private static Task<CoreWebView2Environment> _envTask;
        private static readonly object _lock = new object();

        /// <summary>Общее окружение (создаётся один раз, лениво). Флаги — те же, что
        /// нужны звонку: --allow-running-insecure-content (ws:// LiveKit) + GPU/feature
        /// из настроек. Для плееров эти флаги безвредны.</summary>
        public static Task<CoreWebView2Environment> GetAsync()
        {
            lock (_lock)
            {
                if (_envTask == null || _envTask.IsFaulted || _envTask.IsCanceled)
                    _envTask = CreateAsync();
                return _envTask;
            }
        }

        private static async Task<CoreWebView2Environment> CreateAsync()
        {
            string udf = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "PISMO", "webview-shared");
            // Полный набор флагов (GPU/feature/WGC) — аппаратный кодек демонстрации.
            // Диагностика 2.5.4/2.5.6 показала: 0x8007139F от флагов/GPU НЕ зависит —
            // среду корёжит активный VR (виртуальный дисплей-драйвер), а не WebView2.
            const string baseArgs =
                "--allow-running-insecure-content --autoplay-policy=no-user-gesture-required";
            var opts = new CoreWebView2EnvironmentOptions(DeviceSettings.WebViewArgs(baseArgs));
            return await CoreWebView2Environment.CreateAsync(null, udf, opts);
        }
    }
}
