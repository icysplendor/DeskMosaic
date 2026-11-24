using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Automation;
using System.Windows.Threading;
using System.Windows.Input;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Text;
using WinAssistant.Settings;

namespace WinAssistant.Translation;

internal sealed class TranslationWindow : Window
{
    private const int ImportSource = 3;
    private static readonly string[] AudioExtensions = [".wav", ".mp3", ".m4a", ".aac", ".flac", ".ogg", ".opus", ".aif", ".aiff", ".webm"];
    private string? audioFile;
    private bool changingSource;
    private bool fileSession;
    private bool fileCompleted;
    private readonly Button fileNotice = new() { Visibility = Visibility.Collapsed, HorizontalAlignment = HorizontalAlignment.Left };
    private readonly StackPanel captions = new();
    private readonly ScrollViewer history;
    private readonly TextBlock status = new() { TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 195, VerticalAlignment = VerticalAlignment.Center };
    private readonly Border statusIndicator = new() { Width = 8, Height = 8, CornerRadius = new CornerRadius(4), Margin = new Thickness(0, 0, 7, 0), VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock empty = new() { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
    private Border? latest;
    private Brush Accent => (Brush)Resources["CaptionAccent"];
    private Brush Muted => (Brush)Resources["CaptionMuted"];
    private Brush Line => (Brush)Resources["CaptionLine"];
    private Brush Danger => (Brush)Resources["CaptionDanger"];
    private readonly Button start = new() { MinWidth = 80 };
    private readonly Button twoWay = new() { Width = 80 };
    private readonly Button clear = new() { IsEnabled = false };
    private readonly CheckBox transcribeOnly = new() { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 0, 0) };
    private readonly CheckBox recordAudio = new() { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 0, 0) };
    private readonly CheckBox showSource = new() { VerticalAlignment = VerticalAlignment.Center };
    private readonly ComboBox audioSource = new() { MinWidth = 132, VerticalAlignment = VerticalAlignment.Center };
    private readonly ComboBox sourceLanguage = new() { MinWidth = 105, SelectedValuePath = "Tag" };
    private readonly ComboBox targetLanguage = new() { MinWidth = 105, SelectedValuePath = "Tag" };
    private readonly Button swapLanguages = new() { Padding = new Thickness(8, 3, 8, 3), VerticalAlignment = VerticalAlignment.Bottom };
    private readonly ComboBox compute = new() { MinWidth = 85, SelectedValuePath = "Tag" };
    private readonly ComboBox originalSize = new() { Width = 53 };
    private readonly ComboBox translationSize = new() { Width = 53 };
    private readonly Button export = new() { IsEnabled = false };
    private readonly List<TranslationCaption> transcript = new();
    private readonly Dictionary<int, (TextBlock Original, TextBlock Translation, TextBox Speaker, TextBlock Time, TextBlock Review, TextBlock Cache)> visibleRows = new();
    private readonly Dictionary<int, string> speakerNames = new();
    private CancellationTokenSource? session;
    private Task? sessionCompletion;
    private bool recordingSession;
    private bool identifiesSpeakers;
    private bool sourceOnlySession;
    private bool listening;
    private bool speechReady;
    private bool twoWaySession;
    private int clearedThroughCaption;

    internal TranslationWindow(Action openSettings)
    {
        Title = T("录写/翻译", "Transcribe / Translate");
        Width = 820;
        Height = 490;
        MinWidth = 700;
        MinHeight = 310;
        FontSize = 13;
        Topmost = true;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        history = new ScrollViewer { Content = captions, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        start.Click += (_, _) => Toggle(twoWayMode: false);
        twoWay.Click += (_, _) => Toggle(twoWayMode: true);
        clear.Click += (_, _) => ConfirmClearTranscript();
        showSource.Click += (_, _) =>
        {
            bool visible = showSource.IsChecked == true;
            ConfigurationStore.Shared.Update(config => config.Translation.ShowSource = visible);
            UpdateOriginals(visible);
        };
        Closing += async (_, args) =>
        {
            if (!recordingSession) { Stop(); return; }
            args.Cancel = true;
            await StopAndWaitAsync();
            if (IsVisible) Close();
        };
        Build(openSettings);
    }

    private void Build(Action openSettings)
    {
        var dark = ConfigurationStore.Shared.Configuration.Audio.Theme.Equals("Dark", StringComparison.OrdinalIgnoreCase);
        Background = ColorBrush(dark ? "#111B2B" : "#F6F8FC");
        Resources["CaptionText"] = Foreground = ColorBrush(dark ? "#ECF2FA" : "#172235");
        Resources["CaptionSurface"] = ColorBrush(dark ? "#152033" : "#FFFFFF");
        Resources["CaptionMuted"] = ColorBrush(dark ? "#AABBD0" : "#52627A");
        Resources["CaptionLine"] = ColorBrush(dark ? "#334158" : "#D9E1EC");
        Resources["CaptionHighlight"] = ColorBrush(dark ? "#1E3554" : "#EAF2FE");
        Resources["CaptionAccent"] = ColorBrush("#246BFE");
        Resources["CaptionDanger"] = ColorBrush("#B33D4C");
        Resources.MergedDictionaries.Add(new ResourceDictionary
        {
            Source = new Uri("pack://application:,,,/Translation/TranslationWindowStyles.xaml", UriKind.Absolute)
        });
        var root = new DockPanel { Margin = new Thickness(12) };
        var header = new DockPanel { Margin = new Thickness(0, 0, 0, 8) };
        var toolbar = new StackPanel { Orientation = Orientation.Horizontal };
        DockPanel.SetDock(clear, Dock.Right);
        header.Children.Add(clear);
        header.Children.Add(toolbar);
        DockPanel.SetDock(header, Dock.Top);
        root.Children.Add(header);

        foreach (var picker in new[] { audioSource, sourceLanguage, targetLanguage })
            picker.Style = (Style)Resources["CaptionPicker"];
        foreach (var button in new[] { start, twoWay, clear, swapLanguages })
            button.Style = (Style)Resources["CaptionButton"];
        swapLanguages.VerticalAlignment = VerticalAlignment.Center;
        start.Background = start.BorderBrush = Accent;
        start.Foreground = Brushes.White;
        twoWay.Background = twoWay.BorderBrush = ColorBrush("#6642B8");
        twoWay.Foreground = Brushes.White;
        twoWay.Content = T("互译", "Two-way");
        ToolTipService.SetShowOnDisabled(twoWay, true);
        clear.Content = T("清屏", "Clear");
        clear.ToolTip = T("清空当前字幕", "Clear current captions");
        showSource.Foreground = Foreground;
        showSource.Content = T("显示原文", "Show source");
        showSource.ToolTip = T("译文定稿后继续显示原文", "Keep the source visible after the translation is final");
        showSource.Margin = new Thickness(4, 0, 8, 0);
        var settings = new Button();
        StyleToolbarIcon(settings, "\uE713", T("录写/翻译设置", "Transcribe / Translate settings"));
        settings.Click += (_, _) => { Hide(); Stop(); openSettings(); };
        StyleToolbarIcon(export, "\uE74E", T("导出全文", "Export transcript"));
        export.Margin = new Thickness(0, 0, 6, 0);
        export.Click += (_, _) => OpenExportMenu();
        audioSource.Margin = new Thickness(0, 0, 6, 0);
        audioSource.ToolTip = T("音源", "Audio source");
        AutomationProperties.SetName(audioSource, T("音源", "Audio source"));
        toolbar.Children.Add(audioSource);
        var footer = new DockPanel { Margin = new Thickness(0, 8, 0, 0) };
        DockPanel.SetDock(footer, Dock.Bottom);
        var state = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0) };
        status.Foreground = Muted;
        state.Children.Add(statusIndicator);
        state.Children.Add(status);
        DockPanel.SetDock(state, Dock.Right);
        footer.Children.Add(state);
        root.Children.Add(footer);
        audioSource.Items.Add(T("电脑声音", "System audio"));
        audioSource.Items.Add(T("麦克风", "Microphone"));
        audioSource.Items.Add(T("电脑声＋麦克风", "System + mic"));
        audioSource.Items.Add(T("导入音频", "Import audio"));
        audioSource.SelectionChanged += (_, _) =>
        {
            int index = audioSource.SelectedIndex;
            if (index < 0 || session is not null || changingSource) return;
            if (index == ImportSource)
            {
                ChooseAudioFile();
                return;
            }
            if (audioFile is not null) SetAudioFile(null, index);
            var options = ConfigurationStore.Shared.Configuration.Translation;
            bool system = index is 0 or 2;
            bool microphone = index is 1 or 2;
            if (options.SystemAudio == system && options.Microphone == microphone) return;
            ConfigurationStore.Shared.Update(config =>
            {
                config.Translation.SystemAudio = system;
                config.Translation.Microphone = microphone;
            });
        };
        sourceLanguage.SelectionChanged += (_, _) =>
        {
            var options = SessionOptions();
            if (options.UseLmtTranslation && sourceLanguage.SelectedValue is string source)
            {
                var available = options.TargetLanguagesFor(source);
                var selected = TranslationOptions.SelectLanguage(targetLanguage.SelectedValue as string, available, "English");
                FillLanguagePicker(targetLanguage, available, selected);
            }
            UpdateLanguageControls();
        };
        targetLanguage.SelectionChanged += (_, _) => UpdateLanguageControls();
        swapLanguages.Content = "⇄";
        swapLanguages.ToolTip = T("交换语言", "Swap languages");
        AutomationProperties.SetName(swapLanguages, T("交换语言", "Swap languages"));
        swapLanguages.Margin = new Thickness(8, 0, 8, 0);
        swapLanguages.Click += (_, _) =>
        {
            var source = sourceLanguage.SelectedValue;
            sourceLanguage.SelectedValue = targetLanguage.SelectedValue;
            targetLanguage.SelectedValue = source;
        };
        AutomationProperties.SetName(sourceLanguage, T("源语言", "Source language"));
        AutomationProperties.SetName(targetLanguage, T("目标语言", "Target language"));
        sourceLanguage.ToolTip = T("源语言", "Source language");
        targetLanguage.ToolTip = T("目标语言", "Target language");
        var languages = new StackPanel { Orientation = Orientation.Horizontal };
        languages.Children.Add(sourceLanguage);
        languages.Children.Add(swapLanguages);
        languages.Children.Add(targetLanguage);
        transcribeOnly.Foreground = Foreground;
        transcribeOnly.Content = T("仅记录", "Transcribe only");
        transcribeOnly.ToolTip = T("仅记录原文字幕", "Record source captions only");
        transcribeOnly.Click += (_, _) => UpdateLanguageControls();
        languages.Children.Add(transcribeOnly);
        recordAudio.Foreground = Foreground;
        recordAudio.Content = T("录音", "Record");
        ToolTipService.SetShowOnDisabled(recordAudio, true);
        recordAudio.Click += (_, _) =>
            ConfigurationStore.Shared.Update(config => config.Translation.RecordAudio = recordAudio.IsChecked == true);
        languages.Children.Add(recordAudio);
        start.Margin = new Thickness(10, 0, 0, 0);
        twoWay.Margin = new Thickness(8, 0, 0, 0);
        languages.Children.Add(start);
        languages.Children.Add(twoWay);
        footer.Children.Add(languages);

        foreach (var picker in new[] { compute, originalSize, translationSize })
            picker.Style = (Style)Resources["CaptionPicker"];
        compute.Items.Add(new ComboBoxItem { Tag = "auto", Content = T("自动", "Automatic") });
        compute.Items.Add(new ComboBoxItem { Tag = "cpu", Content = "CPU" });
        compute.Items.Add(new ComboBoxItem { Tag = "gpu", Content = T("优先 GPU", "Prefer GPU") });
        compute.ToolTip = T("自动优先用 Vulkan GPU 生成译文。无可用 GPU 时使用 CPU。",
            "Automatic prefers Vulkan GPU for translation, with CPU fallback.");
        compute.SelectionChanged += (_, _) =>
        {
            if (compute.SelectedValue is string mode && ConfigurationStore.Shared.Configuration.Translation.Compute != mode)
                ConfigurationStore.Shared.Update(config => config.Translation.Compute = mode);
        };
        originalSize.ItemsSource = translationSize.ItemsSource = TranslationOptions.FontSizes;
        originalSize.SelectionChanged += (_, _) => SaveFontSizes();
        translationSize.SelectionChanged += (_, _) => SaveFontSizes();
        compute.Margin = new Thickness(0, 0, 6, 0);
        AutomationProperties.SetName(compute, T("推理设备", "Compute"));
        toolbar.Children.Add(compute);
        AddPreference(T("原文", "Source"), T("原文字号", "Source size"), originalSize);
        AddPreference(T("译文", "Target"), T("译文字号", "Translation size"), translationSize);
        toolbar.Children.Add(showSource);
        toolbar.Children.Add(export);
        toolbar.Children.Add(settings);

        var captionSurface = new Grid();
        captions.Margin = new Thickness(8);
        captionSurface.Children.Add(history);
        empty.Text = T("开始后，字幕会显示在这里", "Captions will appear when you start");
        empty.Foreground = Muted;
        captionSurface.Children.Add(empty);
        root.Children.Add(new Border
        {
            Background = (Brush)Resources["CaptionSurface"], BorderBrush = Line,
            BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(8), Child = captionSurface
        });

        void StyleToolbarIcon(Button button, string glyph, string label)
        {
            button.Style = (Style)Resources["CaptionButton"];
            button.Content = glyph;
            button.FontFamily = new FontFamily("Segoe MDL2 Assets");
            button.Width = 34;
            button.Padding = new Thickness(0);
            button.ToolTip = label;
            AutomationProperties.SetName(button, label);
        }

        void AddPreference(string title, string description, ComboBox picker)
        {
            var field = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 6, 0), ToolTip = description };
            AutomationProperties.SetName(picker, description);
            field.Children.Add(new Label
            {
                Content = title, Target = picker, Foreground = Muted,
                VerticalAlignment = VerticalAlignment.Center, Padding = new Thickness(0, 0, 5, 0)
            });
            field.Children.Add(picker);
            toolbar.Children.Add(field);
        }
        fileNotice.Style = (Style)Resources["CaptionButton"];
        fileNotice.MaxWidth = 640;
        fileNotice.Margin = new Thickness(0, 0, 0, 8);
        fileNotice.Click += (_, _) => ChooseAudioFile();
        DockPanel.SetDock(fileNotice, Dock.Top);
        root.Children.Insert(2, fileNotice);
        AllowDrop = true;
        PreviewDragOver += (_, args) =>
        {
            args.Effects = session is null && DropFile(args.Data) is not null ? DragDropEffects.Copy : DragDropEffects.None;
            args.Handled = true;
        };
        PreviewDrop += (_, args) =>
        {
            if (session is null && DropFile(args.Data) is { } file) SetAudioFile(file, reselected: true);
            args.Handled = true;
        };
        Content = root;
        SetStatus(T("就绪", "Ready"));

    }

    private void SetStatus(string text, bool listening = false, bool failed = false, string? details = null)
    {
        this.listening = listening;
        status.Text = text;
        statusIndicator.Background = failed || (listening && recordingSession) ? ColorBrush("#B33D42")
            : listening ? ColorBrush("#20A765") : Muted;
        status.ToolTip = statusIndicator.ToolTip = details ?? text;
        AutomationProperties.SetName(statusIndicator, text);
        if (captions.Children.Count == 0)
            empty.Text = speechReady && listening ? T("等待语音…", "Waiting for speech…") : text;
    }

    private static Brush ColorBrush(string hex) => (Brush)new BrushConverter().ConvertFromString(hex)!;

    private void UpdateLanguageControls()
    {
        sourceLanguage.IsEnabled = session is null && sourceLanguage.Items.Count != 0;
        bool translating = transcribeOnly.IsChecked != true;
        transcribeOnly.IsEnabled = session is null;
        recordAudio.IsEnabled = session is null && audioFile is null;
        recordAudio.IsChecked = audioFile is null && ConfigurationStore.Shared.Configuration.Translation.RecordAudio;
        recordAudio.ToolTip = audioFile is null
            ? T("开始时录音，结束后保存 WAV 到下载文件夹", "Record on start and save a WAV to Downloads")
            : T("导入音频时无法录音", "Recording is unavailable for imported audio");
        fileNotice.IsEnabled = session is null;
        targetLanguage.IsEnabled = session is null && translating && targetLanguage.Items.Count != 0;
        var options = SessionOptions();
        bool twoWayReady = options.CanTranslateTwoWay;
        twoWay.Visibility = session is null && !fileCompleted ? Visibility.Visible : Visibility.Collapsed;
        twoWay.IsEnabled = session is null && translating && twoWayReady;
        twoWay.ToolTip = !translating
            ? T("互译需要关闭「仅记录」", "Turn off Transcribe only to use two-way translation")
            : twoWayReady ? T("开始中英互译，按段落自动选择方向", "Start two-way translation and choose a direction per paragraph")
                : T("需要中英流式识别模型和文本翻译模型", "Requires Chinese/English streaming recognition and a translation model");
        start.IsEnabled = !fileCompleted;
        start.Content = fileCompleted ? T("音频执行完毕", "Audio processing complete")
            : session is null ? T("开始", "Start")
            : twoWaySession ? T("互译结束", "End two-way translation") : T("翻译结束", "End translation");
        start.Background = start.BorderBrush = session is null ? Accent : Danger;
        swapLanguages.IsEnabled = session is null && translating && sourceLanguage.SelectedValue is string source &&
            targetLanguage.SelectedValue is string target &&
            options.SessionSourceLanguages.Contains(target) && options.TargetLanguagesFor(target).Contains(source);
    }

    internal void Open()
    {
        var options = ConfigurationStore.Shared.Configuration.Translation;
        showSource.IsChecked = options.ShowSource;
        if (!IsVisible && session is null)
        {
            SetAudioFile(null);
            transcribeOnly.IsChecked = false;
            FillLanguagePicker(sourceLanguage, options.AvailableSourceLanguages, options.SelectedSourceLanguage);
            FillLanguagePicker(targetLanguage, options.AvailableTargetLanguages, options.SelectedTargetLanguage);
            UpdateLanguageControls();
        }
        originalSize.SelectedItem = options.SelectedSourceFontSize;
        translationSize.SelectedItem = options.SelectedTargetFontSize;
        if (session is null)
        {
            compute.SelectedValue = options.Compute is "auto" or "cpu" or "gpu" ? options.Compute : "auto";
            var index = (options.SystemAudio, options.Microphone) switch
            {
                (true, true) => 2,
                (true, false) => 0,
                (false, true) => 1,
                _ => -1
            };
            audioSource.SelectedIndex = audioFile is null ? index : ImportSource;
        }
        UpdateLanguageControls();
        UpdateOriginals(showSource.IsChecked == true);
        Show();
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
    }

    private TranslationOptions SessionOptions()
    {
        var options = ConfigurationStore.Shared.Configuration.Translation.Copy();
        options.ImportedAudio = audioFile is not null;
        return options;
    }

    private void ChooseAudioFile()
    {
        if (session is not null) return;
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = T("选择音频文件", "Choose an audio file"),
            Filter = T("音频文件", "Audio files") + "|" + string.Join(";", AudioExtensions.Select(ext => "*" + ext))
        };
        if (dialog.ShowDialog(this) == true) SetAudioFile(dialog.FileName, reselected: true);
        else SetAudioFile(audioFile);
    }

    private void SetAudioFile(string? file, int? liveIndex = null, bool reselected = false)
    {
        bool changed = audioFile != file || reselected;
        if (changed) fileCompleted = false;
        audioFile = file;
        var options = SessionOptions();
        changingSource = true;
        try
        {
            audioSource.SelectedIndex = file is not null ? ImportSource : liveIndex ?? ((options.SystemAudio, options.Microphone) switch
            {
                (true, true) => 2, (true, false) => 0, (false, true) => 1, _ => -1
            });
        }
        finally { changingSource = false; }
        fileNotice.Visibility = file is null ? Visibility.Collapsed : Visibility.Visible;
        if (file is not null)
        {
            fileNotice.Content = T("已选择 · ", "Selected · ") + Path.GetFileName(file);
            fileNotice.ToolTip = file + "\n" + T("点击更换音频文件", "Click to choose another audio file");
        }
        if (!changed) return;
        var source = TranslationOptions.SelectLanguage(sourceLanguage.SelectedValue as string, options.SessionSourceLanguages);
        var target = TranslationOptions.SelectLanguage(targetLanguage.SelectedValue as string, options.TargetLanguagesFor(source), "English");
        FillLanguagePicker(sourceLanguage, options.SessionSourceLanguages, source);
        FillLanguagePicker(targetLanguage, options.TargetLanguagesFor(source), target);
        UpdateLanguageControls();
        SetStatus(file is null ? T("就绪", "Ready") : T("已准备好处理音频", "Ready to process audio"));
    }

    private static string? DropFile(System.Windows.IDataObject data) =>
        data.GetData(DataFormats.FileDrop) is string[] { Length: 1 } files && File.Exists(files[0]) &&
        AudioExtensions.Contains(Path.GetExtension(files[0]), StringComparer.OrdinalIgnoreCase) ? files[0] : null;

    private void FillLanguagePicker(ComboBox picker, string[] available, string selected)
    {
        picker.Items.Clear();
        foreach (string id in available)
        {
            string label = id == "auto" ? T("自动识别", "Auto-detect")
                : T(TranslationOptions.Languages.First(language => language.Id == id).Chinese, id);
            picker.Items.Add(new ComboBoxItem { Tag = id, Content = label });
        }
        picker.SelectedValue = selected;
    }

    internal void Stop()
    {
        if (session is null || session.IsCancellationRequested) return;
        if (recordingSession) SetStatus(T("正在保存录音…", "Saving recording…"));
        session.Cancel();
    }

    internal async Task StopAndWaitAsync()
    {
        Stop();
        if (sessionCompletion is { } completion) await completion;
    }

    private void Toggle(bool twoWayMode)
    {
        if (session is not null) { Stop(); return; }
        if (!fileCompleted) sessionCompletion = RunSessionAsync(twoWayMode);
    }

    private async Task RunSessionAsync(bool twoWayMode)
    {
        using var current = new CancellationTokenSource();
        session = current;
        string? file = audioFile;
        fileSession = file is not null;
        bool record = file is null && recordAudio.IsChecked == true;
        recordingSession = record;
        twoWaySession = twoWayMode;
        audioSource.IsEnabled = compute.IsEnabled = false;
        UpdateLanguageControls();
        var options = ConfigurationStore.Shared.Configuration.Translation.Copy();
        options.TranscribeOnly = transcribeOnly.IsChecked == true;
        options.TwoWay = twoWayMode;
        sourceOnlySession = options.TranscribeOnly;
        identifiesSpeakers = options.SelectedSpeakerModel.Length != 0;
        options.SourceLanguage = sourceLanguage.SelectedValue as string ?? options.SelectedSourceLanguage;
        options.TargetLanguage = targetLanguage.SelectedValue as string ?? options.SelectedTargetLanguage;
        clearedThroughCaption = 0;
        ResetTranscript();
        speakerNames.Clear();
        SetStatus(T("正在加载模型…", "Loading models…"));
        string result = T("已停止", "Stopped");
        bool failed = false;
        bool completed = false;
        string? savedRecording = null;
        Action<string>? onSaved = record ? path => savedRecording = path : null;
        try
        {
            string listening = twoWayMode
                ? (record ? T("互译并录音", "Two-way + recording") : T("正在互译", "Translating both ways"))
                : record ? T("正在录音", "Recording") :
                    sourceOnlySession ? T("正在记录原文", "Transcribing") : T("正在监听", "Listening");
            speechReady = false;
            await Task.Run(() => new TranslationEngine().RunAsync(options, () => PostCaption(() =>
            {
                if (!speechReady)
                    SetStatus(record ? T("正在录音 · 加载模型…", "Recording · Loading models…") :
                        T("正在采集 · 加载模型…", "Capturing audio · Loading models…"), listening: true);
            }), backend => PostCaption(() =>
            {
                speechReady = !fileSession;
                if (fileSession)
                    SetStatus(backend is null ? T("正在识别音频文件…", "Transcribing audio file…")
                        : T("正在处理音频 · 逐段翻译", "Processing audio · Translating") + " · " + backend, listening: true);
                else
                    SetStatus(backend is null ? listening : listening + " · " + backend, listening: true);
            }), caption => PostCaption(() => ApplyCaption(caption)), current.Token, onSaved, file));
            completed = fileSession;
        }
        catch (Exception error) when (current.IsCancellationRequested && error is not TranslationRecordingException) { }
        catch (Exception error)
        {
            result = error.Message;
            failed = true;
        }
        finally
        {
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Background);
            if (sourceOnlySession && current.IsCancellationRequested)
                foreach (var item in transcript.Where(item => !item.SourceFinal).ToArray())
                    ApplyCaption(item with { SourceFinal = true });
            foreach (var item in transcript.Where(item => item.Translation is null).ToArray())
                ApplyCaption(item with { Translation = "" });
            fileCompleted = completed && !current.IsCancellationRequested;
            if (fileCompleted) result = T("音频处理完成", "Audio processing complete");
            if (savedRecording is not null && !failed) result = T("录音已保存", "Recording saved");
            SetStatus(result, failed: failed, details: savedRecording);
            session = null;
            recordingSession = false;
            twoWaySession = false;
            audioSource.IsEnabled = compute.IsEnabled = true;
            UpdateLanguageControls();
        }

        void PostCaption(Action update) => _ = Dispatcher.InvokeAsync(() =>
        {
            if (session == current && !current.IsCancellationRequested) update();
        }, DispatcherPriority.Background);
    }

    private void ApplyCaption(TranslationCaption item)
    {
        int index = item.Id - clearedThroughCaption - 1;
        if (index < 0 || index > transcript.Count) return;
        bool follow = history.ScrollableHeight - history.VerticalOffset < 24;
        if (index == transcript.Count)
        {
            transcript.Add(item);
            var options = ConfigurationStore.Shared.Configuration.Translation;
            var original = new TextBlock
            {
                Text = item.Source, TextWrapping = TextWrapping.Wrap,
                FontSize = options.SelectedSourceFontSize, Foreground = Muted, Margin = new Thickness(0, 0, 0, 3)
            };
            var translation = new TextBlock
            {
                FontSize = options.SelectedTargetFontSize, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap
            };
            var origin = new TextBlock
            {
                Text = SourceLabel(item),
                FontSize = 11, Foreground = Muted, VerticalAlignment = VerticalAlignment.Center
            };
            var editing = new TextBlock
            {
                Text = T("编辑中", "Editing"), FontSize = 11, Foreground = Accent, VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(5, 0, 0, 0), Visibility = Visibility.Collapsed
            };
            var speaker = CreateSpeakerLabel(editing);
            var time = new TextBlock
            {
                FontSize = 11, Foreground = Muted, VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(9, 0, 0, 0), ToolTip = T("这段音频的起止时间", "Start and end time of this passage")
            };
            Typography.SetNumeralAlignment(time, FontNumeralAlignment.Tabular);
            var review = new TextBlock
            {
                FontSize = 11, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(9, 0, 0, 0)
            };
            var cache = new TextBlock
            {
                FontSize = 11, Foreground = Muted, VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(9, 0, 0, 0),
                ToolTip = T("本次翻译复用的输入 token 比例", "Share of prompt tokens reused for this translation")
            };
            Typography.SetNumeralAlignment(cache, FontNumeralAlignment.Tabular);
            var attribution = new WrapPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 4, 0, 0) };
            attribution.Children.Add(origin);
            attribution.Children.Add(speaker);
            attribution.Children.Add(editing);
            attribution.Children.Add(time);
            attribution.Children.Add(review);
            attribution.Children.Add(cache);
            var card = new StackPanel();
            card.Children.Add(original);
            card.Children.Add(translation);
            card.Children.Add(attribution);
            if (latest is not null)
            {
                latest.Background = Brushes.Transparent;
                latest.BorderBrush = Line;
                latest.BorderThickness = new Thickness(0, 0, 0, 1);
            }
            latest = new Border
            {
                Child = card, Padding = new Thickness(13, 10, 13, 10),
                Background = (Brush)Resources["CaptionHighlight"], BorderBrush = Accent,
                BorderThickness = new Thickness(3, 0, 0, 0), CornerRadius = new CornerRadius(6)
            };
            captions.Children.Add(latest);
            visibleRows.Add(item.Id, (original, translation, speaker, time, review, cache));
            if (captions.Children.Count > 250)
            {
                captions.Children.RemoveAt(0);
                visibleRows.Remove(transcript[^251].Id);
            }
            empty.Visibility = Visibility.Collapsed;
            clear.IsEnabled = true;
        }
        else
        {
            transcript[index] = item;
        }
        export.IsEnabled |= item.SourceFinal;
        if (visibleRows.TryGetValue(item.Id, out var row))
        {
            if (row.Speaker.IsReadOnly)
            {
                row.Speaker.Tag = item.SpeakerId;
                bool unknown = identifiesSpeakers && item.SourceFinal && item.SpeakerId == 0;
                row.Speaker.Visibility = item.SpeakerId > 0 || unknown ? Visibility.Visible : Visibility.Collapsed;
                row.Speaker.Text = unknown ? T("说话人待确认", "Speaker unconfirmed") : SpeakerName(item.SpeakerId);
                row.Speaker.Cursor = unknown ? Cursors.Arrow : Cursors.Hand;
                if (unknown) row.Speaker.ToolTip = T("这句话的声音不足以可靠区分说话人", "This phrase cannot be assigned to a speaker reliably");
                else row.Speaker.ToolTip = T("双击修改说话人姓名", "Double-click to rename the speaker");
            }
            row.Time.Text = item.TimeRange;
            row.Review.Visibility = item.Review == CaptionReview.None ? Visibility.Collapsed : Visibility.Visible;
            row.Review.Foreground = item.Review == CaptionReview.Revised ? Accent : Muted;
            row.Review.Text = item.Review switch
            {
                CaptionReview.Pending => T("复核中", "Reviewing"),
                CaptionReview.Kept => T("已复核 · 保留原文", "Reviewed · Original kept"),
                CaptionReview.Revised => T("已修正", "Revised"),
                CaptionReview.Disabled => T("未复核 · 功能已关闭", "Not reviewed · Disabled"),
                CaptionReview.ModelMissing => T("未复核 · 缺少 1920 ms 模型", "Not reviewed · 1920 ms model missing"),
                CaptionReview.LanguageUnsupported => T("未复核 · 源语言不支持", "Not reviewed · Unsupported source language"),
                CaptionReview.ModelPreparing => T("未复核 · 模型正在准备", "Not reviewed · Model preparing"),
                CaptionReview.ModelFailed => T("未复核 · 模型加载失败", "Not reviewed · Model failed to load"),
                CaptionReview.NoReviewableText => T("未复核 · 无可复核文字", "Not reviewed · No reviewable text"),
                CaptionReview.AudioUnaligned => T("未复核 · 音频边界不明确", "Not reviewed · Unclear audio boundaries"),
                CaptionReview.OutsideLimits => T("未复核 · 段落长度超出范围", "Not reviewed · Passage length out of range"),
                CaptionReview.QueueFull => T("未复核 · 队列已满", "Not reviewed · Queue full"),
                CaptionReview.Unavailable => T("未复核 · 状态不可用", "Not reviewed · Status unavailable"),
                _ => ""
            };
            row.Cache.Visibility = item.CacheHitPercent.HasValue ? Visibility.Visible : Visibility.Collapsed;
            row.Cache.Text = item.CacheHitPercent is int percent ? T($"缓存命中 {percent}%", $"Cache hit {percent}%") : "";
            row.Original.Text = item.Source;
            row.Original.Foreground = string.IsNullOrEmpty(item.Translation) ? Foreground : Muted;
            row.Translation.Visibility = sourceOnlySession ? Visibility.Collapsed : Visibility.Visible;
            row.Translation.ToolTip = item.SourceFinal && item.TranslationFinal ? null : T("字幕更新中", "Caption may change");
            row.Translation.Text = item.Translation switch
            {
                null => T("正在翻译…", "Translating…"),
                "" => T("未生成译文", "Translation unavailable"),
                _ => item.Translation
            };
            row.Translation.Foreground = string.IsNullOrEmpty(item.Translation) ? Muted : Foreground;
            row.Original.Visibility = ShowOriginal(item, showSource.IsChecked == true)
                ? Visibility.Visible : Visibility.Collapsed;
        }
        if (follow) history.ScrollToEnd();
    }

    private void ResetTranscript()
    {
        captions.Children.Clear();
        transcript.Clear();
        visibleRows.Clear();
        clear.IsEnabled = export.IsEnabled = false;
        latest = null;
        empty.Text = speechReady && listening ? T("等待语音…", "Waiting for speech…") : status.Text;
        empty.Visibility = Visibility.Visible;
        history.ScrollToHome();
    }

    private void ConfirmClearTranscript()
    {
        if (transcript.Count == 0) return;
        string title = T("清空当前字幕？", "Clear current captions?");
        var dialog = new Window
        {
            Owner = this, Title = title, WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Width = 420, SizeToContent = SizeToContent.Height, ResizeMode = ResizeMode.NoResize,
            WindowStyle = WindowStyle.None, AllowsTransparency = true, Background = Brushes.Transparent,
            ShowInTaskbar = false, Foreground = Foreground, FontSize = FontSize
        };
        dialog.Resources["CaptionAccent"] = Accent;
        var panel = new StackPanel();
        var header = new DockPanel();
        var icon = new Border
        {
            Width = 40, Height = 40, CornerRadius = new CornerRadius(10), VerticalAlignment = VerticalAlignment.Top,
            Background = ColorBrush("#22B33D4C"), Margin = new Thickness(0, 0, 14, 0),
            Child = new TextBlock
            {
                Text = "\uE74D", FontFamily = new FontFamily("Segoe MDL2 Assets"), FontSize = 17,
                Foreground = Danger, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center
            }
        };
        DockPanel.SetDock(icon, Dock.Left);
        header.Children.Add(icon);
        var message = new StackPanel();
        message.Children.Add(new TextBlock { Text = title, FontSize = 16, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap });
        message.Children.Add(new TextBlock
        {
            Text = T("已显示的原文和译文将从窗口和后续导出中移除。录音和已保存的文件会保留。",
                "Visible captions and translations will be removed from the window and future exports. Audio recordings and saved files are kept."),
            TextWrapping = TextWrapping.Wrap, Foreground = Muted, Margin = new Thickness(0, 6, 0, 0)
        });
        header.Children.Add(message);
        panel.Children.Add(header);
        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 18, 0, 0)
        };
        var cancel = new Button
        {
            Content = T("取消", "Cancel"), IsCancel = true, IsDefault = true, Foreground = Foreground,
            Background = (Brush)Resources["CaptionSurface"], BorderBrush = Line, Style = (Style)Resources["CaptionButton"]
        };
        var confirm = new Button
        {
            Content = T("清空字幕", "Clear captions"), Margin = new Thickness(8, 0, 0, 0), Foreground = Brushes.White,
            Background = Danger, BorderBrush = Danger, Style = (Style)Resources["CaptionButton"]
        };
        confirm.Click += (_, _) => dialog.DialogResult = true;
        buttons.Children.Add(cancel);
        buttons.Children.Add(confirm);
        panel.Children.Add(buttons);
        dialog.Content = new Border
        {
            Background = (Brush)Resources["CaptionSurface"], BorderBrush = Line, BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(12), Padding = new Thickness(20), Child = panel
        };
        dialog.Loaded += (_, _) => cancel.Focus();
        if (dialog.ShowDialog() != true || transcript.Count == 0) return;
        clearedThroughCaption = transcript[^1].Id;
        ResetTranscript();
    }

    private string SpeakerName(int id) =>
        speakerNames.GetValueOrDefault(id) ?? $"{T("说话人", "Speaker")} {id}";

    private TextBox CreateSpeakerLabel(TextBlock editing)
    {
        var label = new TextBox
        {
            IsReadOnly = true, Focusable = false, BorderThickness = new Thickness(0), Background = Brushes.Transparent,
            Padding = new Thickness(1, 0, 1, 0), Margin = new Thickness(8, 0, 0, 0),
            Foreground = Foreground, FontSize = 11, MaxWidth = 160, Visibility = Visibility.Collapsed,
            Cursor = Cursors.Hand, ToolTip = T("双击修改说话人姓名", "Double-click to rename the speaker")
        };
        string previousName = "";
        label.VerticalContentAlignment = VerticalAlignment.Center;
        label.PreviewMouseLeftButtonDown += (_, args) =>
        {
            if (args.ClickCount != 2 || !label.IsReadOnly || label.Tag is not int id || id <= 0) return;
            previousName = SpeakerName(id);
            label.IsReadOnly = false;
            label.Focusable = true;
            label.Background = (Brush)Resources["CaptionSurface"];
            label.BorderBrush = Accent;
            label.BorderThickness = new Thickness(1);
            label.Width = 148;
            label.Padding = new Thickness(5, 1, 5, 1);
            label.Cursor = Cursors.IBeam;
            editing.Visibility = Visibility.Visible;
            label.ToolTip = T("正在编辑 · 回车保存，Esc 取消", "Editing · Enter to save, Esc to cancel");
            label.Focus();
            label.SelectAll();
            args.Handled = true;
        };
        label.TextChanged += (_, _) =>
        {
            if (label.IsReadOnly || label.Tag is not int id) return;
            string name = label.Text.Trim();
            if (name.Length == 0) speakerNames.Remove(id);
            else speakerNames[id] = name;
            foreach (var row in visibleRows.Values)
            {
                if (row.Speaker != label && row.Speaker.Tag is int other && other == id && row.Speaker.IsReadOnly)
                    row.Speaker.Text = SpeakerName(id);
            }
        };
        label.PreviewKeyDown += (_, args) =>
        {
            if (label.IsReadOnly || args.Key is not (Key.Enter or Key.Escape)) return;
            if (args.Key == Key.Escape) label.Text = previousName;
            EndEdit();
            Keyboard.ClearFocus();
            args.Handled = true;
        };
        label.LostKeyboardFocus += (_, _) => EndEdit();
        return label;

        void EndEdit()
        {
            if (label.IsReadOnly || label.Tag is not int id) return;
            label.IsReadOnly = true;
            label.Focusable = false;
            label.Background = Brushes.Transparent;
            label.BorderThickness = new Thickness(0);
            label.Width = double.NaN;
            label.Padding = new Thickness(1, 0, 1, 0);
            label.Cursor = Cursors.Hand;
            editing.Visibility = Visibility.Collapsed;
            label.ToolTip = T("双击修改说话人姓名", "Double-click to rename the speaker");
            label.Text = SpeakerName(id);
        }
    }

    private void SaveFontSizes()
    {
        if (originalSize.SelectedItem is not int original || translationSize.SelectedItem is not int translation) return;
        var options = ConfigurationStore.Shared.Configuration.Translation;
        if (options.SourceFontSize != original || options.TargetFontSize != translation)
        {
            ConfigurationStore.Shared.Update(config =>
            {
                config.Translation.SourceFontSize = original;
                config.Translation.TargetFontSize = translation;
            });
        }
        foreach (var row in visibleRows.Values)
        {
            row.Original.FontSize = original;
            row.Translation.FontSize = translation;
        }
    }

    private void UpdateOriginals(bool visible)
    {
        foreach (var (id, row) in visibleRows)
        {
            var item = transcript[id - clearedThroughCaption - 1];
            row.Original.Visibility = ShowOriginal(item, visible) ? Visibility.Visible : Visibility.Collapsed;
        }
    }

    private string SourceLabel(TranslationCaption item) => fileSession ? T("导入音频", "Imported audio")
        : item.Microphone ? T("麦克风", "Microphone") : T("电脑声音", "System audio");

    private bool ShowOriginal(TranslationCaption item, bool keepSource) =>
        sourceOnlySession ||
        !item.TranslationFinal || (keepSource || string.IsNullOrEmpty(item.Translation)) && item.Translation != item.Source;

    private void OpenExportMenu()
    {
        var menu = new ContextMenu { PlacementTarget = export, Placement = PlacementMode.Bottom };
        if (!sourceOnlySession) AddOption(T("原文和译文", "Source and translation"), true, true);
        AddOption(T("仅原文", "Source only"), true, false);
        if (!sourceOnlySession) AddOption(T("仅译文", "Translation only"), false, true);
        menu.IsOpen = true;

        void AddOption(string label, bool includeSource, bool includeTranslation)
        {
            var option = new MenuItem
            {
                Header = label,
                IsEnabled = includeSource || transcript.Any(item => item.SourceFinal && item.TranslationFinal && !string.IsNullOrEmpty(item.Translation))
            };
            option.Click += (_, _) => Export(includeSource, includeTranslation);
            menu.Items.Add(option);
        }
    }

    private void Export(bool includeSource, bool includeTranslation)
    {
        var sections = new List<string>();
        foreach (var item in transcript.Where(item => item.SourceFinal))
        {
            string source = includeSource ? item.Source : "";
            string translation = includeTranslation && item.TranslationFinal ? item.Translation ?? "" : "";
            if (source.Length == 0 && translation.Length == 0) continue;

            string heading = SourceLabel(item);
            if (item.SpeakerId > 0)
            {
                string speakerId = $"{T("说话人", "Speaker")} {item.SpeakerId}";
                string speaker = speakerNames.TryGetValue(item.SpeakerId, out var name)
                    ? $"{name.ReplaceLineEndings(" ")} ({speakerId})" : speakerId;
                heading += " · " + speaker;
            }
            else if (identifiesSpeakers)
            {
                heading += " · " + T("说话人待确认", "Speaker unconfirmed");
            }

            var section = new StringBuilder($"## {MarkdownText(heading)}");
            if (source.Length > 0)
            {
                if (includeTranslation) section.Append($"\n\n**{T("原文", "Source")}**");
                section.Append($"\n\n{MarkdownText(source)}");
            }
            if (translation.Length > 0)
            {
                if (includeSource) section.Append($"\n\n**{T("译文", "Translation")}**");
                section.Append($"\n\n{MarkdownText(translation)}");
            }
            sections.Add(section.ToString());
        }
        if (sections.Count == 0) return;
        string text = $"# {T("录写/翻译", "Transcribe / Translate")}\n\n{string.Join("\n\n", sections)}\n";

        var picker = new Microsoft.Win32.SaveFileDialog
        {
            Filter = T("Markdown 文档 (*.md)|*.md", "Markdown document (*.md)|*.md"),
            DefaultExt = ".md",
            FileName = $"transcript-{DateTime.Now:yyyyMMdd-HHmmss}.md"
        };
        if (picker.ShowDialog(this) != true) return;
        var temporary = $"{picker.FileName}.{Guid.NewGuid():N}.part";
        try
        {
            try
            {
                File.WriteAllText(temporary, text);
                File.Move(temporary, picker.FileName, overwrite: true);
            }
            finally { File.Delete(temporary); }
        }
        catch (Exception error)
        {
            MessageBox.Show(this, error.Message, T("导出失败", "Export failed"), MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private static string MarkdownText(string value)
    {
        var escaped = new StringBuilder(value.Length);
        foreach (char character in value)
        {
            if ("\\`*_{}[]<>#|".Contains(character)) escaped.Append('\\');
            escaped.Append(character);
        }
        return escaped.ToString();
    }

    private static string T(string chinese, string english) =>
        ConfigurationStore.Shared.Configuration.Audio.Language == "en-US" ? english : chinese;
}
