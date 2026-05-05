using System;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace Nikse.SubtitleEdit.Logic.Download;

public interface IChatterboxTtsCppDownloadService
{
    Task DownloadModels(string modelsFolder, IProgress<float>? progress, Action<string>? titleProgress, CancellationToken cancellationToken);
}

public class ChatterboxTtsCppDownloadService : IChatterboxTtsCppDownloadService
{
    private readonly HttpClient _httpClient;

    // Chatterbox is a two-GGUF runtime: T3 AR talker + S3Gen flow-matching codec.
    // We download the q8_0 variants directly so we can hand explicit paths to crispasr
    // (its `-m auto` codec auto-discovery only finds *-s3gen-f16.gguf, not q8_0).
    public const string T3ModelFileName = "chatterbox-t3-q8_0.gguf";
    public const string S3GenModelFileName = "chatterbox-s3gen-q8_0.gguf";

    private const string T3ModelUrl = "https://huggingface.co/cstr/chatterbox-GGUF/resolve/main/chatterbox-t3-q8_0.gguf";
    private const string S3GenModelUrl = "https://huggingface.co/cstr/chatterbox-GGUF/resolve/main/chatterbox-s3gen-q8_0.gguf";

    public ChatterboxTtsCppDownloadService(HttpClient httpClient)
    {
        _httpClient = httpClient;
    }

    public async Task DownloadModels(string modelsFolder, IProgress<float>? progress, Action<string>? titleProgress, CancellationToken cancellationToken)
    {
        var t3Path = Path.Combine(modelsFolder, T3ModelFileName);
        var s3genPath = Path.Combine(modelsFolder, S3GenModelFileName);
        var needT3 = !File.Exists(t3Path);
        var needS3Gen = !File.Exists(s3genPath);
        var total = (needT3 ? 1 : 0) + (needS3Gen ? 1 : 0);
        var step = 0;

        if (needT3)
        {
            step++;
            titleProgress?.Invoke($"Downloading Chatterbox TTS models ({step}/{total}): {T3ModelFileName}");
            await DownloadHelper.DownloadFileAsync(_httpClient, T3ModelUrl, t3Path, progress, cancellationToken);
        }
        if (needS3Gen)
        {
            step++;
            titleProgress?.Invoke($"Downloading Chatterbox TTS models ({step}/{total}): {S3GenModelFileName}");
            await DownloadHelper.DownloadFileAsync(_httpClient, S3GenModelUrl, s3genPath, progress, cancellationToken);
        }
    }
}
