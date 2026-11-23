namespace WinAssistant.Translation;

public sealed class TranslationOptions
{
    public string StreamingModel { get; set; } = "";
    public Dictionary<string, string> AdditionalStreamingModels { get; set; } = new();
    public string TranslationModel { get; set; } = "";
    public string LmtTranslationModel { get; set; } = "";
    public bool UseLmtTranslation { get; set; }
    public string CorrectionModel { get; set; } = "";
    public bool EnableCorrection { get; set; } = true;
    public bool DiagnosticsEnabled { get; set; }
    public string OnlineSpeakerModel { get; set; } = "";
    public string LlamaServer { get; set; } = "";
    public string Compute { get; set; } = "auto";
    // Existing configurations relied on automatic recognition and lack this field.
    public string? SourceLanguage { get; set; }
    public string TargetLanguage { get; set; } = "English";
    public string MicrophoneFilter { get; set; } = "gentle";
    public string MicrophonePause { get; set; } = "normal";
    public bool Microphone { get; set; }
    public bool SystemAudio { get; set; } = true;
    public bool TranscribeOnly { get; set; }
    public bool ShowSource { get; set; } = true;
    public int SourceFontSize { get; set; } = 13;
    public int TargetFontSize { get; set; } = 19;

    internal static readonly int[] FontSizes = [11, 13, 15, 17, 19, 22, 26, 32];
    internal int SelectedSourceFontSize => FontSizes.Contains(SourceFontSize) ? SourceFontSize : 13;
    internal int SelectedTargetFontSize => FontSizes.Contains(TargetFontSize) ? TargetFontSize : 19;

    internal static readonly (string Id, string Chinese)[] Languages =
    [
        ("Chinese", "中文"), ("English", "英语"), ("Japanese", "日语"),
        ("Korean", "韩语"), ("French", "法语"), ("German", "德语"), ("Spanish", "西班牙语")
    ];

    internal const string LlamaModelUrl = "https://huggingface.co/tencent/Hy-MT2-1.8B-GGUF/resolve/main/Hy-MT2-1.8B-Q4_K_M.gguf";
    internal const string LlamaRelease = "https://github.com/ggml-org/llama.cpp/releases";

    internal string[] AvailableSourceLanguages => StreamingSpeechModel.Catalog
        .Where(model => PathFor(model).Length != 0).SelectMany(model => model.Languages).ToArray();
    internal string SelectedCorrectionModel => StreamingSpeechModel.Correction.Download.ResolvePath(CorrectionModel);
    internal string SelectedSpeakerModel => TranslationModelDownload.OnlineSpeaker.ResolvePath(OnlineSpeakerModel);
    internal string StandardTranslationPath => TranslationModelDownload.Llama.ResolvePath(TranslationModel);
    internal string LmtTranslationPath => TranslationModelDownload.Lmt.ResolvePath(LmtTranslationModel);
    internal string SelectedTranslationModel => UseLmtTranslation ? LmtTranslationPath : StandardTranslationPath;
    internal string[] AvailableTargetLanguages => TargetLanguagesFor(SelectedSourceLanguage);

    internal string[] TargetLanguagesFor(string source)
    {
        if (SelectedTranslationModel.Length == 0) return [];
        bool limitToHubs = UseLmtTranslation && source is not ("auto" or "Chinese" or "English");
        return AvailableSourceLanguages.Where(id => Languages.Any(language => language.Id == id) &&
            (!limitToHubs || id is "Chinese" or "English")).ToArray();
    }
    internal string SelectedSourceLanguage => SelectLanguage(SourceLanguage, AvailableSourceLanguages);
    internal string SelectedTargetLanguage => SelectLanguage(TargetLanguage, AvailableTargetLanguages, "English");

    internal string PathFor(StreamingSpeechModel model) => model.Download.ResolvePath(
        model.Id == "zh-en" ? StreamingModel : AdditionalStreamingModels?.GetValueOrDefault(model.Id));

    internal StreamingSpeechModel? ModelForSource(string source) => StreamingSpeechModel.Catalog
        .FirstOrDefault(model => model.Languages.Contains(source) && PathFor(model).Length != 0);

    internal void SelectModel(StreamingSpeechModel model, string path)
    {
        if (model.Id == "zh-en") StreamingModel = path;
        else (AdditionalStreamingModels ??= new())[model.Id] = path;
    }

    internal static string SelectLanguage(string? requested, string[] available, string? fallback = null) =>
        requested is not null && available.Contains(requested) ? requested :
        fallback is not null && available.Contains(fallback) ? fallback : available.FirstOrDefault() ?? "";

    internal TranslationOptions Copy() => (TranslationOptions)MemberwiseClone();
}
