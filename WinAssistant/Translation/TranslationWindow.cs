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
    private readonly StackPanel captions = new();
    private readonly ScrollViewer history;
    private readonly TextBlock status = new() { TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 195, VerticalAlignment = VerticalAlignment.Center };
    private readonly Border statusIndicator = new() { Width = 8, Height = 8, CornerRadius = new CornerRadius(4), Margin = new Thickness(0, 0, 7, 0), VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock empty = new() { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
    private Border? latest;
    private Brush Accent => (Brush)Resources["CaptionAccent"];
    private Brush Muted => (Brush)Resources["CaptionMuted"];
    private Brush Line => (Brush)Resources["CaptionLine"];
    private readonly Button start = new() { MinWidth = 56, Padding = new Thickness(10, 5, 10, 5) };
    private readonly Button startRecording = new();
    private readonly CheckBox transcribeOnly = new() { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 0, 0) };
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
    private readonly Dictionary<int, (TextBlock Original, TextBlock Translation, TextBox Speaker, TextBlock Time, TextBlock Review)> visibleRows = new();
    private readonly Dictionary<int, string> speakerNames = new();
    private CancellationTokenSource? session;
    private Task? sessionCompletion;
    private bool recordingSession;
    private bool identifiesSpeakers;
    private bool sourceOnlySession;

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
        start.Click += (_, _) => Toggle(false);
        startRecording.Click += (_, _) => Toggle(true);
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
        Resources.MergedDictionaries.Add(new ResourceDictionary
        {
            Source = new Uri("pack://application:,,,/Translation/TranslationWindowStyles.xaml", UriKind.Absolute)
        });
        var root = new DockPanel { Margin = new Thickness(12) };
        var header = new DockPanel { Margin = new Thickness(0, 0, 0, 8) };
        var toolbar = new StackPanel { Orientation = Orientation.Horizontal };
        header.Children.Add(toolbar);
        DockPanel.SetDock(header, Dock.Top);
        root.Children.Add(header);

        foreach (var picker in new[] { audioSource, sourceLanguage, targetLanguage })
            picker.Style = (Style)Resources["CaptionPicker"];
        foreach (var button in new[] { start, startRecording, swapLanguages })
            button.Style = (Style)Resources["CaptionButton"];
        swapLanguages.VerticalAlignment = VerticalAlignment.Center;
        start.Background = start.BorderBrush = Accent;
        start.Foreground = Brushes.White;
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
        audioSource.SelectionChanged += (_, _) =>
        {
            int index = audioSource.SelectedIndex;
            if (index < 0 || session is not null) return;
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
            var options = ConfigurationStore.Shared.Configuration.Translation;
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
        start.Margin = new Thickness(10, 0, 0, 0);
        startRecording.Margin = new Thickness(10, 0, 0, 0);
        startRecording.Content = T("开始并录音", "Start and record");
        startRecording.ToolTip = T("开始翻译并保存录音到下载文件夹", "Start translating and record to Downloads");
        languages.Children.Add(start);
        languages.Children.Add(startRecording);
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
        Content = root;
        start.Content = T("开始", "Start");
        SetStatus(T("就绪", "Ready"));

    }

    private void SetStatus(string text, bool listening = false, bool failed = false, string? details = null)
    {
        status.Text = text;
        statusIndicator.Background = failed || (listening && recordingSession) ? ColorBrush("#B33D42")
            : listening ? ColorBrush("#20A765") : Muted;
        status.ToolTip = statusIndicator.ToolTip = details ?? text;
        AutomationProperties.SetName(statusIndicator, text);
        if (captions.Children.Count == 0)
            empty.Text = listening ? T("等待语音…", "Waiting for speech…") : text;
    }

    private static Brush ColorBrush(string hex) => (Brush)new BrushConverter().ConvertFromString(hex)!;

    private void UpdateLanguageControls()
    {
        sourceLanguage.IsEnabled = session is null && sourceLanguage.Items.Count != 0;
        bool translating = transcribeOnly.IsChecked != true;
        transcribeOnly.IsEnabled = session is null;
        targetLanguage.IsEnabled = session is null && translating && targetLanguage.Items.Count != 0;
        startRecording.ToolTip = translating
            ? T("开始翻译并保存录音到下载文件夹", "Start translating and record to Downloads")
            : T("开始记录原文并保存录音到下载文件夹", "Start transcribing and record to Downloads");
        var options = ConfigurationStore.Shared.Configuration.Translation;
        swapLanguages.IsEnabled = session is null && translating && sourceLanguage.SelectedValue is string source &&
            targetLanguage.SelectedValue is string target &&
            options.AvailableSourceLanguages.Contains(target) && options.TargetLanguagesFor(target).Contains(source);
    }

    internal void Open()
    {
        var options = ConfigurationStore.Shared.Configuration.Translation;
        showSource.IsChecked = options.ShowSource;
        if (!IsVisible && session is null)
        {
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
            audioSource.SelectedIndex = index;
        }
        UpdateOriginals(showSource.IsChecked == true);
        Show();
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
    }

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

    private void Toggle(bool record)
    {
        if (session is not null) { Stop(); return; }
        sessionCompletion = RunSessionAsync(record);
    }

    private async Task RunSessionAsync(bool record)
    {
        using var current = new CancellationTokenSource();
        session = current;
        recordingSession = record;
        startRecording.IsEnabled = false;
        if (record) startRecording.Content = T("录音已开启", "Recording enabled");
        audioSource.IsEnabled = compute.IsEnabled = false;
        UpdateLanguageControls();
        var options = ConfigurationStore.Shared.Configuration.Translation.Copy();
        options.TranscribeOnly = transcribeOnly.IsChecked == true;
        sourceOnlySession = options.TranscribeOnly;
        identifiesSpeakers = options.SelectedSpeakerModel.Length != 0;
        options.SourceLanguage = sourceLanguage.SelectedValue as string ?? options.SelectedSourceLanguage;
        options.TargetLanguage = targetLanguage.SelectedValue as string ?? options.SelectedTargetLanguage;
        captions.Children.Clear();
        transcript.Clear();
        speakerNames.Clear();
        visibleRows.Clear();
        export.IsEnabled = false;
        latest = null;
        empty.Visibility = Visibility.Visible;
        start.Content = T("停止", "Stop");
        SetStatus(T("正在加载模型…", "Loading models…"));
        string result = T("已停止", "Stopped");
        bool failed = false;
        string? savedRecording = null;
        Action<string>? onSaved = record ? path => savedRecording = path : null;
        try
        {
            string listening = record ? T("正在录音", "Recording") :
                sourceOnlySession ? T("正在记录原文", "Transcribing") : T("正在监听", "Listening");
            await Task.Run(() => new TranslationEngine().RunAsync(options, backend => PostCaption(() =>
            {
                SetStatus(backend is null ? listening : listening + " · " + backend, listening: true);
            }), caption => PostCaption(() => ApplyCaption(caption)), current.Token, onSaved));
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
            if (savedRecording is not null && !failed) result = T("录音已保存", "Recording saved");
            SetStatus(result, failed: failed, details: savedRecording);
            session = null;
            recordingSession = false;
            startRecording.IsEnabled = true;
            startRecording.Content = T("开始并录音", "Start and record");
            audioSource.IsEnabled = compute.IsEnabled = true;
            UpdateLanguageControls();
            start.Content = T("开始", "Start");
        }

        void PostCaption(Action update) => _ = Dispatcher.InvokeAsync(() =>
        {
            if (session == current && !current.IsCancellationRequested) update();
        }, DispatcherPriority.Background);
    }

    private void ApplyCaption(TranslationCaption item)
    {
        bool follow = history.ScrollableHeight - history.VerticalOffset < 24;
        if (item.Id > transcript.Count)
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
                Text = item.Microphone ? T("麦克风", "Microphone") : T("电脑声音", "System audio"),
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
            var attribution = new WrapPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 4, 0, 0) };
            attribution.Children.Add(origin);
            attribution.Children.Add(speaker);
            attribution.Children.Add(editing);
            attribution.Children.Add(time);
            attribution.Children.Add(review);
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
            visibleRows.Add(item.Id, (original, translation, speaker, time, review));
            if (captions.Children.Count > 250)
            {
                captions.Children.RemoveAt(0);
                visibleRows.Remove(transcript[^251].Id);
            }
            empty.Visibility = Visibility.Collapsed;
        }
        else
        {
            transcript[item.Id - 1] = item;
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
            row.Translation.FontStyle = item.TranslationFinal ? FontStyles.Normal : FontStyles.Italic;
            row.Translation.Foreground = string.IsNullOrEmpty(item.Translation) ? Muted : Foreground;
            row.Original.Visibility = ShowOriginal(item, showSource.IsChecked == true)
                ? Visibility.Visible : Visibility.Collapsed;
        }
        if (follow) history.ScrollToEnd();
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
            var item = transcript[id - 1];
            row.Original.Visibility = ShowOriginal(item, visible) ? Visibility.Visible : Visibility.Collapsed;
        }
    }

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

            string heading = item.Microphone ? T("麦克风", "Microphone") : T("电脑声音", "System audio");
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
