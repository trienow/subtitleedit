using Avalonia.Controls;
using Avalonia.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Nikse.SubtitleEdit.Features.Shared;
using Nikse.SubtitleEdit.Features.Shared.PromptTextBox;
using Nikse.SubtitleEdit.Features.Video.TextToSpeech.Engines;
using Nikse.SubtitleEdit.Logic;
using Nikse.SubtitleEdit.Logic.Config;
using Nikse.SubtitleEdit.Logic.Media;
using System.IO;
using System.Threading.Tasks;

namespace Nikse.SubtitleEdit.Features.Video.TextToSpeech.VoiceSettings;

public partial class VoiceSettingsViewModel : ObservableObject
{
    [ObservableProperty] private string _voiceTestText;
    [ObservableProperty] private bool _isImportVoiceVisible;

    private ITtsEngine? _engine;
    private readonly IFileHelper _fileHelper;
    private readonly IWindowService _windowService;

    public Window? Window { get; set; }

    public bool OkPressed { get; private set; }
    public bool RefreshVoices { get; private set; }

    public VoiceSettingsViewModel(IFileHelper fileHelper, IWindowService windowService)
    {
        VoiceTestText = Se.Settings.Video.TextToSpeech.VoiceTestText;
        _fileHelper = fileHelper;
        _windowService = windowService;
    }

    [RelayCommand]
    private void Ok()
    {
        Se.Settings.Video.TextToSpeech.VoiceTestText = VoiceTestText;
        Se.SaveSettings();
        OkPressed = true;
        Window?.Close();
    }

    [RelayCommand]
    private async Task ImportVoice()
    {
        if (Window == null || _engine == null)
        {
            return;
        }

        var fileName = await _fileHelper.PickOpenFile(Window!, "Open audio file (for clone)", Se.Language.General.AudioFiles, "*.wav;*.mp3");
        if (string.IsNullOrEmpty(fileName))
        {
            return;
        }

        bool ok;
        if (_engine is OmniVoiceTtsCpp omniEngine)
        {
            // OmniVoice voice cloning needs both the WAV and a transcript of what is spoken in
            // it. Read it from a sibling .txt next to the source if present, otherwise prompt
            // the user — without a transcript the engine silently falls back to its default
            // voice, which surfaces as "custom voice does not work".
            var transcript = TryReadSiblingTranscript(fileName);
            if (string.IsNullOrWhiteSpace(transcript))
            {
                var result = await _windowService.ShowDialogAsync<PromptTextBoxWindow, PromptTextBoxViewModel>(Window!, vm =>
                {
                    vm.Initialize(
                        Se.Language.Video.TextToSpeech.VoiceCloneTranscriptTitle,
                        string.Empty,
                        500,
                        150);
                });

                if (!result.OkPressed || string.IsNullOrWhiteSpace(result.Text))
                {
                    return;
                }

                transcript = result.Text.Trim();
            }

            ok = omniEngine.ImportVoice(fileName, transcript);
        }
        else
        {
            ok = _engine.ImportVoice(fileName);
        }

        if (ok)
        {
            var fileNameOnly = Path.GetFileName(fileName);
            await MessageBox.Show(Window, Se.Language.Video.TextToSpeech.VoiceImportSuccessTitle, string.Format(Se.Language.Video.TextToSpeech.VoiceXImported, fileNameOnly));
            RefreshVoices = true;
        }
    }

    private static string? TryReadSiblingTranscript(string audioFileName)
    {
        var siblingTextFile = Path.ChangeExtension(audioFileName, ".txt");
        if (!File.Exists(siblingTextFile))
        {
            return null;
        }

        try
        {
            return File.ReadAllText(siblingTextFile);
        }
        catch
        {
            return null;
        }
    }

    [RelayCommand]
    private void RefreshVoiceList()
    {
        Se.Settings.Video.TextToSpeech.VoiceTestText = VoiceTestText;
        Se.SaveSettings();
        RefreshVoices = true;
        OkPressed = true;
        Window?.Close();
    }

    [RelayCommand]
    private void Cancel()
    {
        Window?.Close();
    }

    internal void OnKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            e.Handled = true;
            Window?.Close();
        }
    }

    internal void Initialize(ITtsEngine engine)
    {
        _engine = engine;
        IsImportVoiceVisible = engine.GetType() == typeof(Qwen3TtsCpp) || engine.GetType() == typeof(ChatterboxTtsCpp) || engine.GetType() == typeof(OmniVoiceTtsCpp);
    }
}