namespace WinAssistant.Translation;

public sealed class TranslationOptions
{
    public string StreamingModel { get; set; } = "";
    public Dictionary<string, string> AdditionalStreamingModels { get; set; } = new();
    public string TranslationModel { get; set; } = "";
    public string LmtTranslationModel { get; set; } = "";
    public bool UseLmtTranslation { get; set; }
    private int previewIntervalMs = DefaultPreviewIntervalMs;
    public int PreviewIntervalMs
    {
        get => previewIntervalMs;
        set => previewIntervalMs = PreviewIntervalsMs.Contains(value) ? value : DefaultPreviewIntervalMs;
    }
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
    [System.Text.Json.Serialization.JsonIgnore]
    public bool TranscribeOnly { get; set; }
    internal bool TwoWay { get; set; }
    public bool RecordAudio { get; set; } = true;
    public bool ShowSource { get; set; } = true;
    public int SourceFontSize { get; set; } = 13;
    public int TargetFontSize { get; set; } = 19;

    internal static readonly int[] FontSizes = [11, 13, 15, 17, 19, 22, 26, 32];
    private const int DefaultPreviewIntervalMs = 1500;
    internal static readonly int[] PreviewIntervalsMs = Enumerable.Range(1, 20).Select(step => step * 500).ToArray();
    internal int SelectedSourceFontSize => FontSizes.Contains(SourceFontSize) ? SourceFontSize : 13;
    internal int SelectedTargetFontSize => FontSizes.Contains(TargetFontSize) ? TargetFontSize : 19;

    internal static readonly (string Id, string Chinese)[] Languages =
    [
        ("Chinese", "中文"), ("English", "英语"), ("Japanese", "日语"),
        ("Korean", "韩语"), ("French", "法语"), ("German", "德语"), ("Spanish", "西班牙语")
    ];

    internal const string LlamaRelease = "https://github.com/ggml-org/llama.cpp/releases";

    internal string[] AvailableSourceLanguages => StreamingSpeechModel.Catalog
        .Where(model => PathFor(model).Length != 0).SelectMany(model => model.Languages).ToArray();
    internal string SelectedCorrectionModel => StreamingSpeechModel.Correction.Download.ResolvePath(CorrectionModel);
    internal string SelectedSpeakerModel => TranslationModelDownload.OnlineSpeaker.ResolvePath(OnlineSpeakerModel);
    internal string StandardTranslationPath => TranslationModelDownload.Llama.ResolvePath(TranslationModel);
    internal string LmtTranslationPath => TranslationModelDownload.Lmt.ResolvePath(LmtTranslationModel);
    internal string SelectedTranslationModel => UseLmtTranslation ? LmtTranslationPath : StandardTranslationPath;
    [System.Text.Json.Serialization.JsonIgnore]
    internal bool ImportedAudio { get; set; }
    internal string[] SessionSourceLanguages => ImportedAudio && SelectedCorrectionModel.Length != 0
        ? AvailableSourceLanguages.Concat(StreamingSpeechModel.Correction.Languages).Distinct().ToArray()
        : AvailableSourceLanguages;
    internal string[] AvailableTargetLanguages => TargetLanguagesFor(SelectedSourceLanguage);

    internal string[] TargetLanguagesFor(string source)
    {
        if (SelectedTranslationModel.Length == 0) return [];
        bool limitToHubs = UseLmtTranslation && source is not ("auto" or "Chinese" or "English");
        return SessionSourceLanguages.Where(id => Languages.Any(language => language.Id == id) &&
            (!limitToHubs || id is "Chinese" or "English")).ToArray();
    }
    internal bool CanTranslateTwoWay => SelectedTranslationModel.Length != 0 && ModelForSource("auto") is not null;
    internal string SelectedSourceLanguage => TwoWay ? "auto" : SelectLanguage(SourceLanguage, SessionSourceLanguages);
    internal string SelectedTargetLanguage => SelectLanguage(TargetLanguage, AvailableTargetLanguages, "English");

    internal string PathFor(StreamingSpeechModel model) => model.Id == StreamingSpeechModel.Correction.Id
        ? SelectedCorrectionModel : model.Download.ResolvePath(
            model.Id == "zh-en" ? StreamingModel : AdditionalStreamingModels?.GetValueOrDefault(model.Id));

    internal StreamingSpeechModel? ModelForSource(string source) =>
        ImportedAudio && StreamingSpeechModel.Correction.Languages.Contains(source) && SelectedCorrectionModel.Length != 0
            ? StreamingSpeechModel.Correction
            : StreamingSpeechModel.Catalog.FirstOrDefault(model => model.Languages.Contains(source) && PathFor(model).Length != 0);

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
