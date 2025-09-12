using System;
using System.ComponentModel;                 // Win32Exception (UAC cancel)
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Security.Principal;             // IsAdministrator
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Navigation;              // RequestNavigateEventArgs
using Microsoft.Win32;

namespace TweakerToolGUI
{
    public partial class MainWindow : Window
    {
        private static readonly Version CurrentVersion = new Version(0, 1, 0);

        private const string ReleasesUrl = "https://github.com/eccko/CleanCommitAI/releases";
        private const string LatestApiUrl = "https://api.github.com/repos/eccko/CleanCommitAI/releases/latest";

        private readonly string scriptDir = Path.Combine(Directory.GetCurrentDirectory(), "scripts");
        private string noScriptsMessage = "No scripts found. Put your *.ps1 or *.cmd files into the 'scripts' folder.";

        // ===== Wbudowany, prosty system tweaków =====
        private class Tweak
        {
            public string Id { get; init; } = "";
            public Func<string> Label { get; init; } = () => "";
            public Func<Task<bool>> RunAsync { get; init; } = () => Task.FromResult(true);
            public bool RequiresAdmin { get; init; } = false;
        }

        private readonly System.Collections.Generic.List<Tweak> _builtins = new();
        private readonly System.Collections.Generic.Dictionary<string, Tweak> _byId = new();

        private bool _updateAvailable = false;
        private string? _latestTag = null;

        public MainWindow()
        {
            InitializeComponent();

            // === Rejestracja wbudowanych tweaków ===
            _builtins.Add(new Tweak
            {
                Id = "builtin:dns_cloudflare",
                Label = GetDnsLabel,
                RequiresAdmin = true,
                RunAsync = ApplyDnsCloudflareAsync
            });

            _builtins.Add(new Tweak
            {
                Id = "builtin:power_ultimate",
                Label = GetUltimatePowerLabel,
                RequiresAdmin = true,
                RunAsync = ApplyUltimatePerformanceAsync
            });

            foreach (var t in _builtins) _byId[t.Id] = t;

            ApplyTheme("System");
            LoadScripts();
        }

        private async void Window_Loaded(object sender, RoutedEventArgs e)
        {
            var lang = CurrentLang();
            var theme = (ThemeSelector.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "System";
            UpdateTextsForLanguage(lang);
            ApplyTheme(theme);

            await CheckForUpdatesAsync();
            UpdateUpdateBadgeText();
        }

        /* ============== THEME ============== */

        private static bool IsSystemLightTheme()
        {
            try
            {
                object? v = Registry.GetValue(
                    @"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize",
                    "AppsUseLightTheme", 1);
                return v is int i && i == 1;
            }
            catch { return true; }
        }

        private void SetBrush(string key, string hex)
        {
            var color = (Color)ColorConverter.ConvertFromString(hex);
            if (Resources.Contains(key))
                Resources[key] = new SolidColorBrush(color);
            else
                Resources.Add(key, new SolidColorBrush(color));
        }

        private void ApplyTheme(string mode)
        {
            bool light = mode == "Light" || (mode == "System" && IsSystemLightTheme());

            if (light)
            {
                SetBrush("BrushBg", "#f4f6fb");
                SetBrush("BrushSurface", "#ffffff");
                SetBrush("BrushCard", "#ffffff");
                SetBrush("BrushBorder", "#e4e7ee");
                SetBrush("BrushFg", "#111827");
                SetBrush("BrushSubtle", "#4b5563");
                SetBrush("BrushInputBg", "#f3f6fb");
                SetBrush("BrushHover", "#e9eef7");
            }
            else
            {
                SetBrush("BrushBg", "#0f172a");
                SetBrush("BrushSurface", "#0b1220");
                SetBrush("BrushCard", "#0d141f");
                SetBrush("BrushBorder", "#243044");
                SetBrush("BrushFg", "#ffffff");
                SetBrush("BrushSubtle", "#aab7c9");
                SetBrush("BrushInputBg", "#111a28");
                SetBrush("BrushHover", "#162033");
            }
        }

        private void ThemeSelector_Changed(object sender, SelectionChangedEventArgs e)
        {
            if (!IsLoaded) return;
            string mode = (ThemeSelector.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "System";
            ApplyTheme(mode);
        }

        /* ============== SCRIPTS + BUILT-IN ============== */

        private string CurrentLang()
            => (LanguageSelector.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "English";

        private string GetDnsLabel() => Localize(
            en: "Change DNS to Cloudflare (malware blocking + better responsiveness)",
            pl: "Zmiana DNS na Cloudflare (blokowanie malware + lepsza responsywność)",
            ru: "Смена DNS на Cloudflare (блокировка вредоносного ПО + лучшая отзывчивость)",
            de: "DNS auf Cloudflare ändern (Malware-Block + bessere Reaktionszeit)",
            es: "Cambiar DNS a Cloudflare (bloqueo de malware + mejor respuesta)");

        private string GetUltimatePowerLabel() => Localize(
            en: "Power plan: Ultimate Performance (create + activate)",
            pl: "Plan zasilania: Najwyższa wydajność (utwórz + aktywuj)",
            ru: "План питания: Максимальная производительность (создать + активировать)",
            de: "Energieplan: Ultimative Leistung (erstellen + aktivieren)",
            es: "Plan de energía: Máximo rendimiento (crear + activar)");

        private string GetNoScriptsMessage() => Localize(
            en: "No scripts found. Put your *.ps1 or *.cmd files into the 'scripts' folder.",
            pl: "Brak skryptów. Wrzuć pliki *.ps1 lub *.cmd do folderu 'scripts'.",
            ru: "Скрипты не найдены. Добавьте файлы *.ps1 или *.cmd в папку 'scripts'.",
            de: "Keine Skripte gefunden. Lege *.ps1 oder *.cmd in den Ordner 'scripts'.",
            es: "No se encontraron scripts. Coloca *.ps1 o *.cmd en la carpeta 'scripts'.");

        private void LoadScripts(bool showEmptyMessage = true)
        {
            if (!Directory.Exists(scriptDir))
                Directory.CreateDirectory(scriptDir);

            noScriptsMessage = GetNoScriptsMessage();

            ScriptList.Children.Clear();

            // --- Wbudowane tweaki ---
            foreach (var t in _builtins)
            {
                ScriptList.Children.Add(new CheckBox
                {
                    Tag = t.Id,
                    IsChecked = true,
                    Content = t.Label()
                });
            }

            // --- Zewnętrzne skrypty .ps1/.cmd (kompatybilnie z poprzednią wersją) ---
            foreach (var file in Directory.GetFiles(scriptDir, "*.*")
                                          .Where(f => f.EndsWith(".ps1", StringComparison.OrdinalIgnoreCase) ||
                                                      f.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase)))
            {
                ScriptList.Children.Add(new CheckBox
                {
                    Content = Path.GetFileName(file),
                    Tag = file,
                    IsChecked = true
                });
            }

            // Jeżeli są tylko wbudowane i nie ma zewnętrznych – pokaż podpowiedź o folderze
            if (ScriptList.Children.Count == _builtins.Count && showEmptyMessage)
                Log(noScriptsMessage);
        }

        private async void RunButton_Click(object sender, RoutedEventArgs e)
        {
            bool anySelected = ScriptList.Children.OfType<CheckBox>().Any(cb => cb.IsChecked == true);
            if (!anySelected)
            {
                MessageBox.Show(
                    Localize(
                        en: "Please select at least one option or upload your own.",
                        pl: "Proszę wybrać co najmniej jedną możliwość albo wgrać własne.",
                        ru: "Пожалуйста, выберите хотя бы один вариант или загрузите свои.",
                        de: "Bitte wähle mindestens eine Option oder lade eigene hoch.",
                        es: "Selecciona al menos una opción o sube las propias."),
                    "Info", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            Log("Starting backup...");
            string backupFile = $"backup_{DateTime.Now:yyyyMMdd_HHmmss}.reg";
            RunPowerShell($"reg export HKCU \"{backupFile}\" /y");
            Log($"✅ Backup saved: {backupFile}");

            foreach (var child in ScriptList.Children)
            {
                if (child is CheckBox cb && cb.IsChecked == true)
                {
                    if (cb.Tag is string tag && _byId.TryGetValue(tag, out var tweak))
                    {
                        Log($"▶ {cb.Content} ...");
                        bool ok = await tweak.RunAsync();
                        if (!ok)
                            Log(Localize(
                                en: "⚠ Tweak finished with warnings/errors.",
                                pl: "⚠ Tweak zakończył się z ostrzeżeniami/błędami.",
                                ru: "⚠ Твик завершился с предупреждениями/ошибками.",
                                de: "⚠ Tweak mit Warnungen/Fehlern beendet.",
                                es: "⚠ El ajuste terminó con advertencias/errores."));
                        continue;
                    }

                    // Zewnętrzne skrypty
                    Log($"▶ Running {cb.Content} ...");
                    RunPowerShell($"-ExecutionPolicy Bypass -File \"{cb.Tag}\"");
                }
            }

            Log("✨ All selected tweaks applied!");
        }

        /* ============== DNS TWEAK (Cloudflare) ============== */

        private async Task<bool> ApplyDnsCloudflareAsync()
        {
            Log(Localize(
                en: "Applying Cloudflare DNS (malware blocking + better responsiveness)...",
                pl: "Ustawianie DNS Cloudflare (blokowanie malware + lepsza responsywność)...",
                ru: "Применение DNS Cloudflare (блокировка вредоносного ПО + лучшая отзывчивость)...",
                de: "Cloudflare-DNS wird gesetzt (Malware-Block + bessere Reaktionszeit)...",
                es: "Aplicando DNS de Cloudflare (bloqueo de malware + mejor respuesta)..."));

            // Ustawiamy DNS per interfejs przez netsh (pewniejsze dla IPv6) i włączamy binding IPv6, jeśli wyłączony
            string psSet = @"
$ErrorActionPreference='Stop'
$WarningPreference='SilentlyContinue'
$ProgressPreference='SilentlyContinue'

$ad = Get-NetAdapter | Where-Object { $_.Status -eq 'Up' }

foreach ($a in $ad) {
  try {
    $b = Get-NetAdapterBinding -InterfaceDescription $a.InterfaceDescription -ComponentID ms_tcpip6 -ErrorAction SilentlyContinue
    if ($b -and -not $b.Enabled) {
      Enable-NetAdapterBinding -InterfaceDescription $a.InterfaceDescription -ComponentID ms_tcpip6 -ErrorAction SilentlyContinue
    }
  } catch {}

  $alias = $a.InterfaceAlias

  # IPv4 (Cloudflare malware-blocking)
  try { netsh interface ipv4 set dnsservers name=""$alias"" static 1.1.1.2 primary } catch {}
  try { netsh interface ipv4 add dnsservers name=""$alias"" address=1.0.0.2 index=2 } catch {}

  # IPv6 (Cloudflare malware-blocking)
  try { netsh interface ipv6 set dnsservers name=""$alias"" static 2606:4700:4700::1112 primary } catch {}
  try { netsh interface ipv6 add dnsservers name=""$alias"" address=2606:4700:4700::1002 index=2 } catch {}
}
";

            bool executed;
            if (IsAdministrator())
            {
                var path = WriteTempPs1(psSet);
                var (code, so, se) = RunPowerShellCapture($"-NoProfile -ExecutionPolicy Bypass -File \"{path}\"");
                TryDelete(path);
                if (!string.IsNullOrWhiteSpace(se)) Log("⚠ " + se.Trim());
                executed = (code == 0);
            }
            else
            {
                executed = RunPowerShellElevated(psSet);
                if (!executed)
                {
                    Log(Localize(
                        en: "⚠ Could not obtain administrator rights or the command failed.",
                        pl: "⚠ Nie udało się uzyskać uprawnień administratora albo komenda nie powiodła się.",
                        ru: "⚠ Не удалось получить права администратора или команда завершилась ошибкой.",
                        de: "⚠ Administratorrechte konnten nicht erlangt werden oder Befehl fehlgeschlagen.",
                        es: "⚠ No se pudieron obtener privilegios de administrador o el comando falló."));
                }
            }

            var (ok, report) = await VerifyDnsCloudflareAsync();
            Log(report);

            return executed && ok;
        }

        private async Task<(bool ok, string report)> VerifyDnsCloudflareAsync()
        {
            string psCheck = @"
$ErrorActionPreference='SilentlyContinue'
$WarningPreference='SilentlyContinue'
$ProgressPreference='SilentlyContinue'

$up = Get-NetAdapter | Where-Object { $_.Status -eq 'Up' }
$result = foreach ($a in $up) {
  $v4 = (Get-DnsClientServerAddress -InterfaceIndex $a.IfIndex -AddressFamily IPv4).ServerAddresses
  $v6 = (Get-DnsClientServerAddress -InterfaceIndex $a.IfIndex -AddressFamily IPv6).ServerAddresses
  [pscustomobject]@{ Name = $a.Name; IPv4 = $v4; IPv6 = $v6 }
}
$result | ConvertTo-Json -Compress -Depth 4
";
            var temp = WriteTempPs1(psCheck);
            var (code, stdout, stderr) = RunPowerShellCapture($"-NoProfile -ExecutionPolicy Bypass -File \"{temp}\"");
            TryDelete(temp);
            if (!string.IsNullOrWhiteSpace(stderr)) Log("⚠ " + stderr.Trim());
            if (string.IsNullOrWhiteSpace(stdout)) stdout = "[]";

            var sb = new StringBuilder();
            bool anyCF = false;
            bool anyV6Configured = false;

            try
            {
                using var doc = JsonDocument.Parse(stdout);
                if (doc.RootElement.ValueKind == JsonValueKind.Array)
                {
                    foreach (var el in doc.RootElement.EnumerateArray())
                    {
                        string name = el.TryGetProperty("Name", out var n) ? n.GetString() ?? "" : "";
                        var v4 = el.TryGetProperty("IPv4", out var v4e) && v4e.ValueKind == JsonValueKind.Array
                                 ? v4e.EnumerateArray().Select(x => x.GetString() ?? "").ToList()
                                 : new System.Collections.Generic.List<string>();
                        var v6 = el.TryGetProperty("IPv6", out var v6e) && v6e.ValueKind == JsonValueKind.Array
                                 ? v6e.EnumerateArray().Select(x => x.GetString() ?? "").ToList()
                                 : new System.Collections.Generic.List<string>();

                        anyV6Configured |= v6.Count > 0;

                        bool hasCfV4 = v4.Any(x => x == "1.1.1.2" || x == "1.0.0.2");
                        bool hasCfV6 = v6.Any(x =>
                            x.Equals("2606:4700:4700::1112", StringComparison.OrdinalIgnoreCase) ||
                            x.Equals("2606:4700:4700::1002", StringComparison.OrdinalIgnoreCase));

                        anyCF |= hasCfV4 || hasCfV6;

                        sb.AppendLine($"• {name} [IPv4] → {string.Join(", ", v4)}");
                        sb.AppendLine($"  [IPv6] → {string.Join(", ", v6)}");
                    }
                }
            }
            catch
            {
                return (false, Localize(
                    en: "Could not parse DNS configuration.",
                    pl: "Nie udało się sparsować konfiguracji DNS.",
                    ru: "Не удалось разобрать конфигурацию DNS.",
                    de: "DNS-Konfiguration konnte nicht geparst werden.",
                    es: "No se pudo analizar la configuración DNS."));
            }

            if (!anyV6Configured)
            {
                try
                {
                    object? disabled = Registry.GetValue(
                        @"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Services\Tcpip6\Parameters",
                        "DisabledComponents", 0);
                    if (disabled is int i && i != 0)
                    {
                        sb.AppendLine();
                        sb.AppendLine(Localize(
                            en: "ℹ️ IPv6 may be disabled by system policy (HKLM...\\Tcpip6\\Parameters\\DisabledComponents ≠ 0).",
                            pl: "ℹ️ IPv6 może być wyłączone polityką systemową (HKLM...\\Tcpip6\\Parameters\\DisabledComponents ≠ 0).",
                            ru: "ℹ️ IPv6 может быть отключён политикой системы (HKLM...\\Tcpip6\\Parameters\\DisabledComponents ≠ 0).",
                            de: "ℹ️ IPv6 könnte per Systemrichtlinie deaktiviert sein (HKLM...\\Tcpip6\\Parameters\\DisabledComponents ≠ 0).",
                            es: "ℹ️ IPv6 puede estar deshabilitado por política del sistema (HKLM...\\Tcpip6\\Parameters\\DisabledComponents ≠ 0)."));
                    }
                }
                catch { /* ignore */ }
            }

            string reportHeader = Localize(
                en: "DNS on active adapters:",
                pl: "DNS na aktywnych interfejsach:",
                ru: "DNS на активных адаптерах:",
                de: "DNS auf aktiven Adaptern:",
                es: "DNS en adaptadores activos:");

            return (anyCF, $"{reportHeader}{Environment.NewLine}{sb.ToString().TrimEnd()}");
        }

        /* ============== POWER PLAN: ULTIMATE PERFORMANCE ============== */

        private async Task<bool> ApplyUltimatePerformanceAsync()
        {
            Log(Localize(
                en: "Creating and activating Ultimate Performance power plan...",
                pl: "Tworzenie i aktywacja planu zasilania Najwyższa wydajność...",
                ru: "Создание и активация плана питания Максимальная производительность...",
                de: "Erstelle und aktiviere Energieplan Ultimative Leistung...",
                es: "Creando y activando el plan de energía Máximo rendimiento..."));

            // CMD: powercfg -duplicatescheme e9a4... + natychmiastowe ustawienie aktywnego GUID
            string ps = @"
$ErrorActionPreference='Stop'
# Duplicate
$dupOut = & powercfg -duplicatescheme e9a42b02-d5df-448d-aa00-03f14749eb61 2>$null
# Parse GUID z wyjścia (ostatni występujący)
$guid = ($dupOut | Select-String -Pattern '[0-9a-fA-F-]{36}' -AllMatches | ForEach-Object { $_.Matches.Value } | Select-Object -Last 1)

# Jeżeli nie udało się wyłuskać (lokalizacja różna), spróbuj znaleźć Ultimate w -list
if (-not $guid) {
  $list = & powercfg -list
  $m = ($list | Select-String -Pattern '([0-9a-fA-F-]{36}).*(Ultimate|Najwyższa|Ultimative|Máximo|Максимальная)' -AllMatches)
  if ($m) { $guid = $m.Matches[0].Groups[1].Value }
}

if ($guid) {
  & powercfg -setactive $guid
} else {
  throw 'Could not determine duplicated plan GUID.'
}
";

            bool executed;
            if (IsAdministrator())
            {
                var path = WriteTempPs1(ps);
                var (code, so, se) = RunPowerShellCapture($"-NoProfile -ExecutionPolicy Bypass -File \"{path}\"");
                TryDelete(path);
                if (!string.IsNullOrWhiteSpace(se)) Log("⚠ " + se.Trim());
                executed = (code == 0);
            }
            else
            {
                executed = RunPowerShellElevated(ps);
            }

            // Loguj aktywny plan
            var (_, activeOut, activeErr) = RunPowerShellCapture("-NoProfile -Command \"powercfg -getactivescheme\"");
            if (!string.IsNullOrWhiteSpace(activeErr)) Log("⚠ " + activeErr.Trim());
            if (!string.IsNullOrWhiteSpace(activeOut)) Log(activeOut.Trim());

            if (!executed)
            {
                Log(Localize(
                    en: "⚠ Could not create or activate Ultimate Performance plan.",
                    pl: "⚠ Nie udało się utworzyć lub aktywować planu Najwyższa wydajność.",
                    ru: "⚠ Не удалось создать или активировать план Максимальная производительность.",
                    de: "⚠ Plan Ultimative Leistung konnte nicht erstellt oder aktiviert werden.",
                    es: "⚠ No se pudo crear o activar el plan Máximo rendimiento."));
            }

            return executed;
        }

        /* ============== RUNNERS & UTIL ================== */

        private static bool IsAdministrator()
        {
            try
            {
                using var id = WindowsIdentity.GetCurrent();
                var p = new WindowsPrincipal(id);
                return p.IsInRole(WindowsBuiltInRole.Administrator);
            }
            catch { return false; }
        }

        private (int ExitCode, string StdOut, string StdErr) RunPowerShellCapture(string args)
        {
            var psi = new ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = args,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var process = Process.Start(psi)!;
            string output = process.StandardOutput.ReadToEnd();
            string error = process.StandardError.ReadToEnd();
            process.WaitForExit();
            return (process.ExitCode, output, error);
        }

        // Uruchom PS z podniesieniem uprawnień (UAC). Zwraca true/false.
        private bool RunPowerShellElevated(string scriptBody)
        {
            string path = WriteTempPs1(scriptBody);
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = "powershell.exe",
                    Arguments = $"-NoProfile -ExecutionPolicy Bypass -File \"{path}\"",
                    UseShellExecute = true,
                    Verb = "runas",
                    WindowStyle = ProcessWindowStyle.Hidden
                };

                using var proc = Process.Start(psi);
                if (proc == null) return false;
                proc.WaitForExit();
                return proc.ExitCode == 0;
            }
            catch (Win32Exception)
            {
                Log(Localize(
                    en: "⚠ Administrator permission denied (UAC cancelled).",
                    pl: "⚠ Odmowa uprawnień administratora (anulowano UAC).",
                    ru: "⚠ Отказано в правах администратора (UAC отменён).",
                    de: "⚠ Administratorrechte verweigert (UAC abgebrochen).",
                    es: "⚠ Permiso de administrador denegado (UAC cancelado)."));
                return false;
            }
            catch (Exception ex)
            {
                Log("⚠ " + ex.Message);
                return false;
            }
            finally
            {
                TryDelete(path);
            }
        }

        private static string WriteTempPs1(string body)
        {
            string path = Path.Combine(Path.GetTempPath(), "w11tweak_" + Guid.NewGuid().ToString("N") + ".ps1");
            File.WriteAllText(path, body, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            return path;
        }

        private static void TryDelete(string path)
        {
            try { if (File.Exists(path)) File.Delete(path); } catch { /* ignore */ }
        }

        // Pozostawione dla kompatybilności z zewnętrznymi skryptami i backupem rejestru
        private void RunPowerShell(string args)
        {
            var psi = new ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = args,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var process = Process.Start(psi);
            string output = process!.StandardOutput.ReadToEnd();
            string error = process.StandardError.ReadToEnd();
            process.WaitForExit();

            if (!string.IsNullOrWhiteSpace(output)) Log(output.Trim());
            if (!string.IsNullOrWhiteSpace(error)) Log("⚠ " + error.Trim());
        }

        private void UploadButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (!Directory.Exists(scriptDir))
                    Directory.CreateDirectory(scriptDir);

                Process.Start("explorer.exe", scriptDir);
                Log(Localize(
                    en: "Opened scripts folder for adding your own .ps1 or .cmd files.",
                    pl: "Otworzono folder 'scripts' – dodaj własne pliki .ps1 lub .cmd.",
                    ru: "Открыта папка 'scripts' — добавьте файлы .ps1 или .cmd.",
                    de: "Ordner 'scripts' geöffnet — füge .ps1 oder .cmd hinzu.",
                    es: "Carpeta 'scripts' abierta — agrega .ps1 o .cmd."));
            }
            catch (Exception ex)
            {
                Log("⚠ " + ex.Message);
            }
        }

        /* ============== LOG ============== */

        private void Log(string msg)
        {
            string ts = DateTime.Now.ToString("HH:mm:ss");
            LogBox.AppendText($"[{ts}] {msg}{Environment.NewLine}");
            LogBox.ScrollToEnd();
        }

        private void CopyLogButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                Clipboard.SetText(LogBox.Text);
                Log(Localize(
                    en: "Log copied to clipboard.",
                    pl: "Skopiowano log do schowka.",
                    ru: "Лог скопирован в буфер обмена.",
                    de: "Protokoll in die Zwischenablage kopiert.",
                    es: "Registro copiado al portapapeles."));
            }
            catch (Exception ex)
            {
                Log("⚠ " + ex.Message);
            }
        }

        /* ============== LANGUAGE ============== */

        private void LanguageSelector_Changed(object sender, SelectionChangedEventArgs e)
        {
            if (!IsLoaded) return;

            // 1) najpierw przebuduj listę (etykiety wbudowanych tweaków dostaną nowy język)
            LoadScripts(false);

            // 2) potem zaktualizuj wszystkie napisy
            UpdateTextsForLanguage(CurrentLang());

            // 3) badge w bieżącym języku
            UpdateUpdateBadgeText();
        }

        private void UpdateTextsForLanguage(string lang)
        {
            string tweaks = "Available Tweaks";
            string run = "▶ Run Tweaks";
            string upload = "📂 Upload custom";
            string theme = "Theme:";
            string language = "Language:";
            string log = "Log";
            string upd = "Check for updates";
            string badge = "New!";

            switch (lang)
            {
                case "Polski":
                    tweaks = "Dostępne tweaki";
                    run = "▶ Uruchom tweaki";
                    upload = "📂 Wgraj własne";
                    theme = "Motyw:";
                    language = "Język:";
                    log = "Log";
                    upd = "Sprawdź aktualizacje";
                    badge = "Nowa!";
                    break;
                case "Русский":
                    tweaks = "Доступные твики";
                    run = "▶ Запустить твики";
                    upload = "📂 Загрузить свои";
                    theme = "Тема:";
                    language = "Язык:";
                    log = "Лог";
                    upd = "Проверить обновления";
                    badge = "Новое!";
                    break;
                case "Deutsch":
                    tweaks = "Verfügbare Tweaks";
                    run = "▶ Tweaks ausführen";
                    upload = "📂 Eigene hinzufügen";
                    theme = "Motiv:";
                    language = "Sprache:";
                    log = "Protokoll";
                    upd = "Nach Updates suchen";
                    badge = "Neu!";
                    break;
                case "Español":
                    tweaks = "Ajustes disponibles";
                    run = "▶ Ejecutar ajustes";
                    upload = "📂 Subir propios";
                    theme = "Tema:";
                    language = "Idioma:";
                    log = "Registro";
                    upd = "Buscar actualizaciones";
                    badge = "¡Nuevo!";
                    break;
            }

            TweaksHeader.Text = tweaks;
            RunButton.Content = run;
            UploadButton.Content = upload;
            ThemeLabel.Text = theme;
            LanguageLabel.Text = language;
            LogHeader.Text = log;

            // Link + badge
            UpdateLinkText.Text = upd;
            UpdateBadgeText.Text = badge;

            // Komunikat o braku skryptów
            noScriptsMessage = GetNoScriptsMessage();

            // Odśwież etykiety wbudowanych tweaków
            foreach (var child in ScriptList.Children.OfType<CheckBox>())
            {
                if (child.Tag is string tag && _byId.ContainsKey(tag))
                {
                    // ustaw etykietę zgodnie z bieżącym językiem
                    if (tag == "builtin:dns_cloudflare") child.Content = GetDnsLabel();
                    else if (tag == "builtin:power_ultimate") child.Content = GetUltimatePowerLabel();
                }
            }
        }

        private string Localize(string en, string pl, string ru, string de, string es)
        {
            string selected = CurrentLang();
            return selected switch
            {
                "Polski" => pl,
                "Русский" => ru,
                "Deutsch" => de,
                "Español" => es,
                _ => en
            };
        }

        /* ============== UPDATE CHECK (GitHub API) ============== */

        private async Task CheckForUpdatesAsync()
        {
            try
            {
                using var http = new HttpClient();
                http.DefaultRequestHeaders.UserAgent.ParseAdd("W11-Tweaker-Tool/0.1 (+https://github.com/eccko/CleanCommitAI)");
                http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");

                using var resp = await http.GetAsync(LatestApiUrl);
                if (!resp.IsSuccessStatusCode) { UpdateBadge.Visibility = Visibility.Collapsed; return; }

                using var stream = await resp.Content.ReadAsStreamAsync();
                using var doc = await JsonDocument.ParseAsync(stream);
                string? tag = doc.RootElement.TryGetProperty("tag_name", out var t) ? t.GetString() : null;
                if (string.IsNullOrWhiteSpace(tag)) { UpdateBadge.Visibility = Visibility.Collapsed; return; }

                _latestTag = tag;
                var latest = ParseVersionFromTag(tag);
                if (latest != null && latest > CurrentVersion)
                {
                    _updateAvailable = true;
                    UpdateBadge.Visibility = Visibility.Visible;
                }
                else
                {
                    _updateAvailable = false;
                    UpdateBadge.Visibility = Visibility.Collapsed;
                }
            }
            catch
            {
                UpdateBadge.Visibility = Visibility.Collapsed;
            }
        }

        private static Version? ParseVersionFromTag(string tag)
        {
            var sb = new StringBuilder();
            foreach (char c in tag)
                if (char.IsDigit(c) || c == '.') sb.Append(c);

            string cleaned = sb.ToString();
            if (string.IsNullOrWhiteSpace(cleaned)) return null;

            var parts = cleaned.Split('.', StringSplitOptions.RemoveEmptyEntries).ToList();
            while (parts.Count < 3) parts.Add("0");
            cleaned = string.Join(".", parts.Take(3));

            return Version.TryParse(cleaned, out var v) ? v : null;
        }

        private void UpdateUpdateBadgeText()
        {
            // tylko lokalizacja napisu; widoczność kontroluje CheckForUpdatesAsync
            if (_updateAvailable)
            {
                UpdateBadge.Visibility = Visibility.Visible;
                UpdateBadgeText.Text = Localize("New!", "Nowa!", "Новое!", "Neu!", "¡Nuevo!");
            }
            else
            {
                UpdateBadge.Visibility = Visibility.Collapsed;
            }
        }

        /* ============== LINK (releases) ============== */
        private void Hyperlink_RequestNavigate(object sender, RequestNavigateEventArgs e)
        {
            try
            {
                Process.Start(new ProcessStartInfo(ReleasesUrl) { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                Log("⚠ " + ex.Message);
            }
            e.Handled = true;
        }
    }
}
