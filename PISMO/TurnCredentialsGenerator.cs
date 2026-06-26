using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Windows.Forms;

namespace PISMO
{
    public static class TurnCredentialsGenerator
    {
        /// <summary>
        /// Сгенерировать username/password и попытаться сохранить (в БД и в файл) если включён TURN и включены временные креды.
        /// Возвращает (username, password) или null.
        /// </summary>
        public static (string username, string password)? GenerateAndSaveIfNeeded()
        {
            try
            {
                if (!TurnSettings.TurnEnabled || !TurnSettings.UseTimeLimitedCredentials)
                    return null;

                string secret = TurnSettings.TurnSecret;
                if (string.IsNullOrWhiteSpace(secret))
                    return null; // нет секрета — пропускаем

                int ttl = TurnSettings.TurnCredentialsTtl;

                // Минимум 24 часа — кредо должны жить дольше любого звонка.
                // 300 сек (5 мин) просрочены ещё до завершения ICE-переговоров.
                if (ttl < 86400) ttl = 86400;

                // Используем ваш вариант: фиксированный базовый user = "alice"
                string baseUser = "alice";

                // ВАЖНО: генерация делегирована в TurnSettings.GenerateTimeLimitedCredentials,
                // чтобы во всём приложении был ровно ОДИН источник правды для HMAC-логики
                // (включая санитизацию секрета от невидимых символов). Раньше здесь была
                // независимая копия той же логики — при расхождении в будущем (например,
                // кто-то поправит один метод и забудет другой) это давало бы разные
                // password для одного и того же username, что именно и наблюдалось.
                var (username, password, _) = TurnSettings.GenerateTimeLimitedCredentials(baseUser);

                // Пытаемся сохранить в репозиторий (если есть доступ)
                try
                {
                    TurnServerRepository.SaveTurnServer(
                        "default",
                        TurnSettings.TurnServerAddress,
                        TurnSettings.TurnServerPort,
                        TurnSettings.TurnTransport ?? "tcp",
                        username,
                        secret,
                        ttl,
                        TurnSettings.UseTimeLimitedCredentials);
                }
                catch (Exception ex)
                {
                    Debug.WriteLine("[TURN AUTO] Save to DB failed: " + ex.Message);
                }

                // Пишем файл turn.key — пробуем несколько путей
                string savedFilePath = null;
                string fileError = null;
                try
                {
                    // Путь 1: AppData\Roaming\PISMO (без \cache)
                    string appData = Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                        "PISMO");
                    Directory.CreateDirectory(appData);
                    string filePath = Path.Combine(appData, "turn.key");

                    string content = $"# PISMO TURN Server Credentials\r\n" +
                                     $"# Сгенерировано: {DateTime.Now:O}\r\n\r\n" +
                                     $"ServerAddress={TurnSettings.TurnServerAddress}\r\n" +
                                     $"ServerPort={TurnSettings.TurnServerPort}\r\n" +
                                     $"Transport={TurnSettings.TurnTransport ?? "tcp"}\r\n" +
                                     $"Username={username}\r\n" +
                                     $"SecretHex={secret}\r\n" +
                                     $"TtlSeconds={ttl}\r\n" +
                                     $"UseTimeLimited=true\r\n" +
                                     $"CreatedAt={DateTime.UtcNow:O}\r\n";

                    File.WriteAllText(filePath, content, Encoding.UTF8);
                    savedFilePath = filePath;
                    Debug.WriteLine("[TURN AUTO] Файл сохранён: " + filePath);
                }
                catch (Exception ex)
                {
                    fileError = ex.Message;
                    Debug.WriteLine("[TURN AUTO] Save to AppData failed: " + ex.Message);

                    // Путь 2: рабочий стол
                    try
                    {
                        string desktop = Environment.GetFolderPath(Environment.SpecialFolder.Desktop);
                        string filePath2 = Path.Combine(desktop, "turn.key");
                        string content2 = $"# PISMO TURN Server Credentials\r\n" +
                                          $"Username={username}\r\nPassword={password}\r\n" +
                                          $"ServerAddress={TurnSettings.TurnServerAddress}\r\n";
                        File.WriteAllText(filePath2, content2, Encoding.UTF8);
                        savedFilePath = filePath2;
                        Debug.WriteLine("[TURN AUTO] Файл сохранён на рабочем столе: " + filePath2);
                    }
                    catch (Exception ex2)
                    {
                        Debug.WriteLine("[TURN AUTO] Save to Desktop failed: " + ex2.Message);
                    }
                }

                // Отобразим username в настройках UI
                try { TurnSettings.TurnUsername = username; } catch { }

                // --- Отладочный блок: скопировать creds в буфер и показать MessageBox ---
                try
                {
                    var txt = $"username: {username}\npassword: {password}";
                    Clipboard.SetText(txt);
                }
                catch { /* ignore clipboard errors */ }

                try
                {
                    string fileInfo = savedFilePath != null
                        ? $"\n\n📁 Файл сохранён:\n{savedFilePath}"
                        : $"\n\n⚠️ Файл НЕ сохранён: {fileError}";

                    MessageBox.Show(
                        $"TURN credentials сгенерированы и скопированы в буфер:\n\nusername: {username}\npassword: {password}{fileInfo}",
                        "TURN debug",
                        MessageBoxButtons.OK,
                        MessageBoxIcon.Information);
                }
                catch { /* ignore UI errors */ }
                // --- /Отладочный блок ---

                return (username, password);
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[TURN AUTO] Exception: " + ex.Message);
                return null;
            }
        }
    }
}