using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using ChatAppBlazor.Serialization;
#if ANDROID
using Android.Content;
using Microsoft.Maui.ApplicationModel;
#endif

namespace ChatAppBlazor.Services
{
    public record UpdateInfo(string Version, string DownloadUrl, string ReleaseNotes);

    public enum UpdateState
    {
        Idle,
        Downloading,
        Extracting,
        Relaunching,
        Failed
    }

    public class UpdateService : IDisposable
    {
        private readonly GrpcChannelProvider _channelProvider;
        private readonly HttpClient _http;
        private CancellationTokenSource? _cts;
        private Task? _pollingTask;
        private string? _dismissedVersion;

        public string CurrentVersion { get; set; } = "1.0.0";
        public UpdateInfo? AvailableUpdate { get; private set; }
        public bool HasUpdate => AvailableUpdate != null;
        public UpdateState State { get; private set; } = UpdateState.Idle;
        public double DownloadProgress { get; private set; }
        public string? ErrorMessage { get; private set; }

        public event Action? OnUpdateAvailable;
        public event Action? OnStateChanged;

        public UpdateService(GrpcChannelProvider channelProvider)
        {
            _channelProvider = channelProvider;
            _http = new HttpClient();
        }

        public void StartPolling(TimeSpan? interval = null)
        {
            _cts?.Cancel();
            _cts = new CancellationTokenSource();
            var token = _cts.Token;
            var delay = interval ?? TimeSpan.FromHours(1);

            _pollingTask = Task.Run(async () =>
            {
                await CheckForUpdateAsync();

                while (!token.IsCancellationRequested)
                {
                    try
                    {
                        await Task.Delay(delay, token);
                        await CheckForUpdateAsync();
                    }
                    catch (System.OperationCanceledException)
                    {
                        break;
                    }
                }
            }, token);
        }

        public async Task CheckForUpdateAsync()
        {
            try
            {
                var baseUrl = GetWebApiBaseUrl();
                var platform = GetUpdatePlatform();
                var response = await _http.GetAsync($"{baseUrl}/api/update?platform={Uri.EscapeDataString(platform)}");
                if (!response.IsSuccessStatusCode)
                {
                    return;
                }

                var json = await response.Content.ReadAsStringAsync();
                var info = JsonSerializer.Deserialize(json, AppJsonContext.Default.UpdateInfoDto);

                if (info == null)
                {
                    return;
                }

                if (IsNewerVersion(info.Version, CurrentVersion) && !string.IsNullOrWhiteSpace(info.DownloadUrl))
                {
                    if (string.Equals(_dismissedVersion, info.Version, StringComparison.OrdinalIgnoreCase))
                    {
                        return;
                    }

                    var isNewVersion = !string.Equals(AvailableUpdate?.Version, info.Version, StringComparison.OrdinalIgnoreCase);
                    AvailableUpdate = new UpdateInfo(info.Version, info.DownloadUrl, info.ReleaseNotes);

                    if (isNewVersion)
                    {
                        OnUpdateAvailable?.Invoke();
                    }
                }
            }
            catch
            {
                // Ignore polling errors and keep next poll running.
            }
        }

        public void DismissAvailableUpdate()
        {
            if (AvailableUpdate == null)
            {
                return;
            }

            _dismissedVersion = AvailableUpdate.Version;
            AvailableUpdate = null;
            ErrorMessage = null;
            DownloadProgress = 0;

            if (State != UpdateState.Idle)
            {
                SetState(UpdateState.Idle);
                return;
            }

            OnStateChanged?.Invoke();
        }

        public async Task DownloadAndInstallAsync()
        {
            if (AvailableUpdate == null || string.IsNullOrWhiteSpace(AvailableUpdate.DownloadUrl))
            {
                return;
            }

            try
            {
#if WINDOWS
                SetState(UpdateState.Downloading);
                DownloadProgress = 0;

                var zipPath = Path.Combine(Path.GetTempPath(), $"PrivateChat_update_{AvailableUpdate.Version}.zip");

                using (var response = await _http.GetAsync(AvailableUpdate.DownloadUrl, HttpCompletionOption.ResponseHeadersRead))
                {
                    response.EnsureSuccessStatusCode();
                    var total = response.Content.Headers.ContentLength ?? -1L;

                    await using var fs = new FileStream(zipPath, FileMode.Create, FileAccess.Write, FileShare.None);
                    await using var stream = await response.Content.ReadAsStreamAsync();

                    var buffer = new byte[81920];
                    long downloaded = 0;
                    int read;
                    while ((read = await stream.ReadAsync(buffer)) > 0)
                    {
                        await fs.WriteAsync(buffer.AsMemory(0, read));
                        downloaded += read;
                        if (total > 0)
                        {
                            DownloadProgress = (double)downloaded / total * 100;
                            OnStateChanged?.Invoke();
                        }
                    }
                }

                DownloadProgress = 100;
                SetState(UpdateState.Extracting);

                var installDir = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar);
                var exeName = Path.GetFileName(Environment.ProcessPath ?? "PrivateChatApp.exe");
                var scriptPath = Path.Combine(Path.GetTempPath(), "privatechat_updater.ps1");
                var logPath = Path.Combine(Path.GetTempPath(), "privatechat_updater.log");

                var script = string.Join(
                    Environment.NewLine,
                    new[]
                    {
                        "$ErrorActionPreference = 'Stop'",
                        $"$zipPath  = '{zipPath.Replace("'", "''")}'",
                        $"$destDir  = '{installDir.Replace("'", "''")}'",
                        $"$exeName  = '{exeName.Replace("'", "''")}'",
                        $"$appPid   = {Environment.ProcessId}",
                        $"$logPath  = '{logPath.Replace("'", "''")}'",
                        string.Empty,
                        "function Write-Log([string]$message) {",
                        "    Add-Content -Path $logPath -Value (\"$(Get-Date -Format o) \" + $message)",
                        "}",
                        string.Empty,
                        "$exePath = Join-Path $destDir $exeName",
                        "Write-Log 'Updater started'",
                        "try {",
                        "    $proc = Get-Process -Id $appPid -ErrorAction SilentlyContinue",
                        "    if ($proc) {",
                        "        Write-Log \"Waiting for app pid=$appPid to exit\"",
                        "        $proc.WaitForExit(30000) | Out-Null",
                        "    }",
                        "    Start-Sleep -Milliseconds 500",
                        string.Empty,
                        "    Write-Log \"Extracting $zipPath to $destDir\"",
                        "    Add-Type -AssemblyName System.IO.Compression.FileSystem",
                        "    $zip = [System.IO.Compression.ZipFile]::OpenRead($zipPath)",
                        "    foreach ($entry in $zip.Entries) {",
                        "        $targetPath = Join-Path $destDir $entry.FullName",
                        "        $targetDir  = Split-Path $targetPath -Parent",
                        "        if (-not (Test-Path $targetDir)) { New-Item -ItemType Directory -Path $targetDir | Out-Null }",
                        "        if ($entry.Name -ne '') {",
                        "            [System.IO.Compression.ZipFileExtensions]::ExtractToFile($entry, $targetPath, $true)",
                        "        }",
                        "    }",
                        "    $zip.Dispose()",
                        string.Empty,
                        "    Remove-Item $zipPath -Force -ErrorAction SilentlyContinue",
                        string.Empty,
                        "    Write-Log \"Relaunching $exePath\"",
                        "    Start-Process $exePath",
                        "}",
                        "catch {",
                        "    Write-Log (\"Updater failed: \" + $_.Exception.Message)",
                        "    if (Test-Path $exePath) {",
                        "        Start-Process $exePath",
                        "    }",
                        "}"
                    });

                await File.WriteAllTextAsync(scriptPath, script);

                SetState(UpdateState.Relaunching);

                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "powershell.exe",
                    Arguments = $"-NoProfile -NonInteractive -WindowStyle Hidden -ExecutionPolicy Bypass -File \"{scriptPath}\"",
                    UseShellExecute = true,
                    WindowStyle = System.Diagnostics.ProcessWindowStyle.Hidden
                });

                if (Application.Current is { } app && app.Windows.Count > 0)
                {
                    app.CloseWindow(app.Windows[0]);
                }

                Environment.Exit(0);
#elif ANDROID
                var rawUpdateUrl = AvailableUpdate.DownloadUrl.Trim();
                var normalizedUpdateUrl = rawUpdateUrl.Replace(" ", "%20", StringComparison.Ordinal);
                if (!Uri.TryCreate(rawUpdateUrl, UriKind.Absolute, out var updateUri) &&
                    !Uri.TryCreate(normalizedUpdateUrl, UriKind.Absolute, out updateUri))
                {
                    throw new InvalidOperationException("Update URL is invalid.");
                }

                if (!string.Equals(updateUri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException("Update URL must use HTTPS.");
                }

                SetState(UpdateState.Downloading);

                var opened = false;
                try
                {
                    await Browser.Default.OpenAsync(updateUri, BrowserLaunchMode.External);
                    opened = true;
                }
                catch
                {
                    // Fallbacks below.
                }

                if (!opened)
                {
                    opened = await Launcher.TryOpenAsync(updateUri);
                }

                if (!opened)
                {
                    var intent = new Intent(Intent.ActionView, Android.Net.Uri.Parse(updateUri.ToString()));
                    intent.AddCategory(Intent.CategoryBrowsable);
                    intent.AddFlags(ActivityFlags.NewTask);
                    Platform.AppContext.StartActivity(intent);
                    opened = true;
                }

                SetState(UpdateState.Idle);
#else
                if (!Uri.TryCreate(AvailableUpdate.DownloadUrl, UriKind.Absolute, out var updateUri))
                {
                    throw new InvalidOperationException("Update URL is invalid.");
                }

                if (!string.Equals(updateUri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException("Update URL must use HTTPS.");
                }

                SetState(UpdateState.Downloading);
                var opened = await Microsoft.Maui.ApplicationModel.Launcher.TryOpenAsync(updateUri);
                if (!opened)
                {
                    throw new InvalidOperationException("Could not open the platform update URL.");
                }

                SetState(UpdateState.Idle);
#endif
            }
            catch (Exception ex)
            {
                ErrorMessage = ex.Message;
                SetState(UpdateState.Failed);
            }
        }

        private void SetState(UpdateState state)
        {
            State = state;
            OnStateChanged?.Invoke();
        }

        private string GetWebApiBaseUrl()
        {
            var grpcUrl = _channelProvider.ServerUrl;
            if (!Uri.TryCreate(grpcUrl, UriKind.Absolute, out var uri))
            {
                return "https://localhost:5002";
            }

            return uri.GetLeftPart(UriPartial.Authority);
        }

        private static string GetUpdatePlatform()
        {
            if (OperatingSystem.IsAndroid()) return "android";
            if (OperatingSystem.IsWindows()) return "windows";
            if (OperatingSystem.IsLinux()) return "linux";
            if (OperatingSystem.IsIOS()) return "ios";
            if (OperatingSystem.IsMacCatalyst()) return "maccatalyst";
            return "windows";
        }

        private static bool IsNewerVersion(string remote, string local)
        {
            if (!Version.TryParse(remote, out var remoteVer)) return false;
            if (!Version.TryParse(local, out var localVer)) return false;
            return remoteVer > localVer;
        }

        public void Dispose()
        {
            _cts?.Cancel();
            _cts?.Dispose();
            _http.Dispose();
        }

        internal sealed class UpdateInfoDto
        {
            [JsonPropertyName("version")]
            public string Version { get; set; } = string.Empty;

            [JsonPropertyName("downloadUrl")]
            public string DownloadUrl { get; set; } = string.Empty;

            [JsonPropertyName("releaseNotes")]
            public string ReleaseNotes { get; set; } = string.Empty;
        }
    }
}
