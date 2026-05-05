using Nikse.SubtitleEdit.Core.Common;
using Nikse.SubtitleEdit.Features.Video.SpeechToText.Engines;
using Nikse.SubtitleEdit.Features.Video.TextToSpeech.Voices;
using Nikse.SubtitleEdit.Logic.Config;
using Nikse.SubtitleEdit.Logic.Download;
using Nikse.SubtitleEdit.Logic.Media;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Nikse.SubtitleEdit.Features.Video.TextToSpeech.Engines;

/// <summary>
/// Chatterbox TTS via the existing CrispASR install (shared with the speech-to-text feature).
/// Spawns `crispasr --server --backend chatterbox -m auto` and POSTs to the OpenAI-compatible
/// /v1/audio/speech endpoint. Requires CrispASR v0.6.0 or newer.
/// Chatterbox has one baked default voice; "voices" listed beyond Default come from
/// WAVs imported via <see cref="ImportVoice"/>. The full reference-WAV path is sent per-request
/// as the `voice` field — runtime WAV cloning is wired upstream in CrispASR's chatterbox backend.
/// </summary>
public class ChatterboxTtsCpp : ITtsEngine
{
    public string Name => "Chatterbox TTS";
    public string Description => "via CrispASR (one baked voice + clone via Import voice)";
    public bool HasLanguageParameter => false;
    public bool HasApiKey => false;
    public bool HasRegion => false;
    public bool HasModel => false;
    public bool HasKeyFile => false;

    private const string BackendName = "chatterbox";

    private static readonly HttpClient HttpClient = new()
    {
        Timeout = TimeSpan.FromMinutes(5),
    };
    private static readonly SemaphoreSlim ServerLock = new(1, 1);
    private static Process? _serverProcess;
    private static int _serverPort;
    private static bool _processExitHooked;

    private static string ServerBaseUrl => $"http://127.0.0.1:{_serverPort}";

    public Task<bool> IsInstalled(string? region)
    {
        return Task.FromResult(File.Exists(GetCrispAsrExecutable()));
    }

    public override string ToString() => Name;

    /// <summary>
    /// Path to the crispasr executable installed by the speech-to-text feature.
    /// Chatterbox piggy-backs on the same install so users don't download two copies.
    /// </summary>
    public static string GetCrispAsrExecutable()
    {
        return new CrispAsrCohere().GetExecutable();
    }

    /// <summary>
    /// Returns true when the installed crispasr executable matches a known
    /// chatterbox-capable release (currently v0.6.0+ — earlier builds neither
    /// recognise --backend chatterbox nor expose the /v1/audio/speech endpoint).
    /// Returns true when the hash is unknown so we don't false-positive on
    /// custom local builds.
    /// </summary>
    public static bool IsCrispAsrChatterboxCapable()
    {
        var exe = GetCrispAsrExecutable();
        if (!File.Exists(exe))
        {
            return false;
        }

        var folder = Path.GetDirectoryName(exe);
        var variant = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) && folder != null
            ? DownloadHashManager.DetectCrispAsrWindowsVariant(folder)
            : null;
        var key = DownloadHashManager.ResolveCrispAsrExecutableKey(variant);
        if (key == null)
        {
            return true;
        }

        var hash = DownloadHashManager.ComputeSha256(exe);
        if (hash == null)
        {
            return true;
        }

        // UpdateAvailable means the installed hash is a known *older* release —
        // demote those to "not chatterbox-capable" so the user is prompted to
        // re-download. UpToDate and Unknown both pass through.
        return DownloadHashManager.GetStatus(key, hash) != DownloadHashManager.UpdateStatus.UpdateAvailable;
    }

    public static string GetSetFolder()
    {
        if (!Directory.Exists(Se.TextToSpeechFolder))
        {
            Directory.CreateDirectory(Se.TextToSpeechFolder);
        }

        var folder = Path.Combine(Se.TextToSpeechFolder, "Chatterbox");
        if (!Directory.Exists(folder))
        {
            Directory.CreateDirectory(folder);
        }

        return folder;
    }

    public static string GetSetVoicesFolder()
    {
        var voicesFolder = Path.Combine(GetSetFolder(), "voices");
        if (!Directory.Exists(voicesFolder))
        {
            Directory.CreateDirectory(voicesFolder);
        }

        return voicesFolder;
    }

    public Task<Voice[]> GetVoices(string language)
    {
        var result = new List<Voice>
        {
            new Voice(new ChatterboxVoice("Default", string.Empty)),
        };

        var voicesFolder = GetSetVoicesFolder();
        foreach (var file in Directory.GetFiles(voicesFolder, "*.wav"))
        {
            var name = Path.GetFileNameWithoutExtension(file).Replace('_', ' ');
            result.Add(new Voice(new ChatterboxVoice(name, file)));
        }

        return Task.FromResult(result.ToArray());
    }

    public bool IsVoiceInstalled(Voice voice) => true;

    public Task<string[]> GetRegions() => Task.FromResult(Array.Empty<string>());

    public Task<string[]> GetModels() => Task.FromResult(Array.Empty<string>());

    public Task<TtsLanguage[]> GetLanguages(Voice voice, string? model) => Task.FromResult(Array.Empty<TtsLanguage>());

    public Task<Voice[]> RefreshVoices(string language, CancellationToken cancellationToken)
    {
        return GetVoices(language);
    }

    public async Task<TtsResult> Speak(
        string text,
        string outputFolder,
        Voice voice,
        TtsLanguage? language,
        string? region,
        string? model,
        CancellationToken cancellationToken)
    {
        if (voice.EngineVoice is not ChatterboxVoice chatterboxVoice)
        {
            throw new ArgumentException("Voice is not a ChatterboxVoice");
        }

        await EnsureServerRunningAsync(cancellationToken);

        var outputFileName = Path.Combine(GetSetFolder(), Guid.NewGuid() + ".wav");
        var inputText = Utilities.UnbreakLine(text);

        // Per /v1/audio/speech: send full reference WAV path as the `voice` field.
        // Empty string falls back to the model's baked default voice.
        var payload = new Dictionary<string, object>
        {
            ["input"] = inputText,
            ["response_format"] = "wav",
        };
        if (!string.IsNullOrEmpty(chatterboxVoice.FilePath))
        {
            payload["voice"] = chatterboxVoice.FilePath;
        }

        var body = JsonSerializer.Serialize(payload);
        using var content = new StringContent(body, Encoding.UTF8, "application/json");
        using var response = await HttpClient.PostAsync($"{ServerBaseUrl}/v1/audio/speech", content, cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            var errorBody = await SafeReadErrorAsync(response, cancellationToken);
            Se.LogError($"Chatterbox TTS server error {(int)response.StatusCode} {response.StatusCode} - "
                + $"Voice: {chatterboxVoice}, Text: {text}, Body: {errorBody}");
            throw new InvalidOperationException(
                $"Chatterbox TTS synthesis failed ({(int)response.StatusCode}): {errorBody}");
        }

        await using (var fileStream = File.Create(outputFileName))
        await using (var contentStream = await response.Content.ReadAsStreamAsync(cancellationToken))
        {
            await contentStream.CopyToAsync(fileStream, cancellationToken);
        }

        return new TtsResult(outputFileName, text);
    }

    private static async Task<string> SafeReadErrorAsync(HttpResponseMessage response, CancellationToken ct)
    {
        try
        {
            return await response.Content.ReadAsStringAsync(ct);
        }
        catch (Exception ex)
        {
            return $"<failed to read error body: {ex.Message}>";
        }
    }

    private static async Task EnsureServerRunningAsync(CancellationToken ct)
    {
        if (_serverProcess is { HasExited: false } && _serverPort != 0)
        {
            return;
        }

        await ServerLock.WaitAsync(ct);
        try
        {
            if (_serverProcess is { HasExited: false } && _serverPort != 0)
            {
                return;
            }

            if (_serverProcess != null)
            {
                StopServerInternal();
            }

            var exe = GetCrispAsrExecutable();
            if (!File.Exists(exe))
            {
                throw new FileNotFoundException(
                    "CrispASR executable not found. Install CrispASR via Video → Audio to text first.", exe);
            }

            var port = FindFreeLoopbackPort();
            var psi = new ProcessStartInfo
            {
                WorkingDirectory = Path.GetDirectoryName(exe) ?? GetSetFolder(),
                FileName = exe,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardError = true,
                RedirectStandardOutput = true,
            };
            psi.ArgumentList.Add("--server");
            psi.ArgumentList.Add("--backend");
            psi.ArgumentList.Add(BackendName);
            psi.ArgumentList.Add("-m");
            psi.ArgumentList.Add("auto");
            psi.ArgumentList.Add("--host");
            psi.ArgumentList.Add("127.0.0.1");
            psi.ArgumentList.Add("--port");
            psi.ArgumentList.Add(port.ToString());

            var process = Process.Start(psi)
                ?? throw new InvalidOperationException("Failed to start crispasr (chatterbox)");

            var stderrBuffer = new StringBuilder();
            process.ErrorDataReceived += (_, e) =>
            {
                if (e.Data != null) lock (stderrBuffer) stderrBuffer.AppendLine(e.Data);
            };
            process.OutputDataReceived += (_, e) =>
            {
                if (e.Data != null) lock (stderrBuffer) stderrBuffer.AppendLine(e.Data);
            };
            process.BeginErrorReadLine();
            process.BeginOutputReadLine();

            _serverProcess = process;
            _serverPort = port;
            HookProcessExitOnce();

            // First-run model auto-download (~880 MB) needs a generous timeout.
            var deadline = DateTime.UtcNow.AddMinutes(15);
            while (DateTime.UtcNow < deadline)
            {
                ct.ThrowIfCancellationRequested();
                if (process.HasExited)
                {
                    var tail = SnapshotStderr(stderrBuffer);
                    _serverProcess = null;
                    _serverPort = 0;
                    if (LooksLikeOutdatedCrispAsr(tail))
                    {
                        throw new InvalidOperationException(
                            "Chatterbox requires CrispASR v0.6.0 or newer. Re-download CrispASR via "
                            + "Video → Audio to text → Engine settings → Re-download, then try again.");
                    }
                    throw new InvalidOperationException(
                        $"crispasr (chatterbox) exited during startup (code {process.ExitCode}). Output: {tail}");
                }
                if (await ProbeHealthAsync(port, TimeSpan.FromSeconds(2), ct))
                {
                    return;
                }
                await Task.Delay(TimeSpan.FromSeconds(1), ct);
            }

            var lastOutput = SnapshotStderr(stderrBuffer);
            StopServerInternal();
            throw new TimeoutException(
                $"crispasr (chatterbox) did not report healthy within 15 minutes. Last output: {lastOutput}");
        }
        finally
        {
            ServerLock.Release();
        }
    }

    private static bool LooksLikeOutdatedCrispAsr(string output)
    {
        // v0.5.x exits 0 and prints `error: unknown argument: ...` when it doesn't
        // recognise --voice-dir / --backend chatterbox. v0.6.x without the chatterbox
        // backend (e.g. ASR-only build) prints `unknown backend 'chatterbox'`.
        return output.Contains("unknown argument", StringComparison.Ordinal)
            || output.Contains("unknown backend", StringComparison.Ordinal);
    }

    private static string SnapshotStderr(StringBuilder buffer)
    {
        lock (buffer)
        {
            var s = buffer.ToString().TrimEnd();
            return s.Length > 2000 ? s[^2000..] : s;
        }
    }

    private static async Task<bool> ProbeHealthAsync(int port, TimeSpan timeout, CancellationToken ct)
    {
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(timeout);
            using var resp = await HttpClient.GetAsync($"http://127.0.0.1:{port}/health", cts.Token);
            return resp.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }

    private static int FindFreeLoopbackPort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            return ((IPEndPoint)listener.LocalEndpoint).Port;
        }
        finally
        {
            listener.Stop();
        }
    }

    private static void HookProcessExitOnce()
    {
        if (_processExitHooked) return;
        _processExitHooked = true;
        AppDomain.CurrentDomain.ProcessExit += (_, _) => StopServerInternal();
    }

    public static void StopServer()
    {
        ServerLock.Wait();
        try
        {
            StopServerInternal();
        }
        finally
        {
            ServerLock.Release();
        }
    }

    private static void StopServerInternal()
    {
        var p = _serverProcess;
        _serverProcess = null;
        _serverPort = 0;
        if (p == null) return;
        try
        {
            if (!p.HasExited)
            {
                p.Kill(entireProcessTree: true);
                p.WaitForExit(2000);
            }
        }
        catch
        {
            // best effort
        }
        finally
        {
            p.Dispose();
        }
    }

    private static string GetUniqueDestinationFileName(string folder, string baseName)
    {
        var candidate = Path.Combine(folder, baseName + ".wav");
        if (!File.Exists(candidate))
        {
            return candidate;
        }

        var number = 1;
        do
        {
            candidate = Path.Combine(folder, $"{baseName}_{number}.wav");
            number++;
        } while (File.Exists(candidate));

        return candidate;
    }

    public bool ImportVoice(string fileName)
    {
        if (string.IsNullOrEmpty(fileName) || !File.Exists(fileName))
        {
            return false;
        }

        var voicesFolder = GetSetVoicesFolder();
        var baseName = Path.GetFileNameWithoutExtension(fileName);
        var destinationFileName = GetUniqueDestinationFileName(voicesFolder, baseName);

        if (Path.GetExtension(fileName).Equals(".wav", StringComparison.OrdinalIgnoreCase))
        {
            File.Copy(fileName, destinationFileName, overwrite: false);
            return true;
        }

        var process = FfmpegGenerator.ConvertFormat(fileName, destinationFileName);
        if (OperatingSystem.IsWindows() || OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
        {
            _ = process.Start();
        }
        else
        {
            throw new PlatformNotSupportedException("Process.Start() is not supported on this platform.");
        }

        process.WaitForExit();

        return File.Exists(destinationFileName);
    }
}
