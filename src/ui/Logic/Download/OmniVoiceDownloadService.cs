using System;
using System.IO;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace Nikse.SubtitleEdit.Logic.Download;

public interface IOmniVoiceDownloadService
{
    Task DownloadModels(string modelsFolder, IProgress<float>? progress, Action<string>? titleProgress, CancellationToken cancellationToken);

    Task DownloadEngine(Stream stream, IProgress<float>? progress, CancellationToken cancellationToken);
}

public class OmniVoiceDownloadService : IOmniVoiceDownloadService
{
    private readonly HttpClient _httpClient;

    public const string ModelBaseFileName = "omnivoice-base-Q8_0.gguf";
    public const string ModelTokenizerFileName = "omnivoice-tokenizer-F32.gguf";

    private const string ModelBaseUrl = "https://huggingface.co/Serveurperso/OmniVoice-GGUF/resolve/main/omnivoice-base-Q8_0.gguf";
    private const string ModelTokenizerUrl = "https://huggingface.co/Serveurperso/OmniVoice-GGUF/resolve/main/omnivoice-tokenizer-F32.gguf";

    private const string WindowsUrl = "https://github.com/SubtitleEdit/support-files/releases/download/omnivoice-26-06/omnivoice-win64-cpu.zip";
    private const string MacOsUrl = "https://github.com/SubtitleEdit/support-files/releases/download/omnivoice-26-06/omnivoice-macos-universal-cpu-metal.zip";

    public OmniVoiceDownloadService(HttpClient httpClient)
    {
        _httpClient = httpClient;
    }

    public async Task DownloadModels(string modelsFolder, IProgress<float>? progress, Action<string>? titleProgress, CancellationToken cancellationToken)
    {
        var basePath = Path.Combine(modelsFolder, ModelBaseFileName);
        var tokenizerPath = Path.Combine(modelsFolder, ModelTokenizerFileName);
        var needBase = !File.Exists(basePath);
        var needTokenizer = !File.Exists(tokenizerPath);
        var total = (needBase ? 1 : 0) + (needTokenizer ? 1 : 0);
        var step = 0;

        if (needBase)
        {
            step++;
            titleProgress?.Invoke($"Downloading OmniVoice TTS models ({step}/{total}): {ModelBaseFileName}");
            await DownloadHelper.DownloadFileAsync(_httpClient, ModelBaseUrl, basePath, progress, cancellationToken);
        }
        if (needTokenizer)
        {
            step++;
            titleProgress?.Invoke($"Downloading OmniVoice TTS models ({step}/{total}): {ModelTokenizerFileName}");
            await DownloadHelper.DownloadFileAsync(_httpClient, ModelTokenizerUrl, tokenizerPath, progress, cancellationToken);
        }
    }

    public async Task DownloadEngine(Stream stream, IProgress<float>? progress, CancellationToken cancellationToken)
    {
        await DownloadHelper.DownloadFileAsync(_httpClient, GetUrl(), stream, progress, cancellationToken);
    }

    private static string GetUrl()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return WindowsUrl;
        }

        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            return MacOsUrl;
        }

        throw new PlatformNotSupportedException();
    }
}
