using System.Globalization;
using System.Runtime.InteropServices;
using System.Windows.Threading;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32;
using WinAssistant.Infrastructure;
using WinAssistant.Settings;
using Drawing = System.Drawing;
using Drawing2D = System.Drawing.Drawing2D;
using DrawingImaging = System.Drawing.Imaging;

namespace WinAssistant.Screenshot;

internal sealed class ScreenshotSession : IDisposable
{
    private enum ToolbarPicker
    {
        Color,
        Size
    }
    private const int ToolButtonCount = 5;
    private const int CompactPaletteButtonIndex = 5;
    private const int StrokeSizeButtonIndex = 6;
    private const int SizeButtonIndex = 7;
    private const int UndoButtonIndex = 8;
    private const int SaveButtonIndex = 9;
    private const int CaptureRecordButtonIndex = 10;
    private const int DesktopButtonIndex = 11;
    private const int RecordingStopButtonIndex = 12;
    private int FinishButtonIndex => recording is null ? DesktopButtonIndex : CaptureRecordButtonIndex;
    private int RecordButtonIndex => recording is null ? CaptureRecordButtonIndex : RecordingStopButtonIndex;
    private int ToolbarButtonCount => (recording is null ? DesktopButtonIndex : RecordingStopButtonIndex) + 1;

    private readonly IReadOnlyList<CapturedMonitor> monitors;
    private readonly IReadOnlyList<WindowCandidate> windowCandidates;
    private readonly Action onClosed;
    private readonly Action onRecordingStarted;
    private readonly List<OverlayWindow> windows = [];
    private readonly Dictionary<(CapturedMonitor Monitor, Rect Bounds), OverlayWindow> recordingInputs = [];
    private readonly List<Annotation> annotations = [];
    private readonly List<Point> penPoints = [];
    private readonly double[] strokeWidths = [1, 2, 3, 5, 8, 12];
    private readonly double[] fontSizes = [14, 18, 24, 32, 44, 60];
    private readonly double[] mosaicBrushSizes = [12, 20, 32, 48, 72, 96];
    private Drawing.Bitmap? desktopBitmap;
    private Drawing.Bitmap? desktopMosaicBitmap;
    private readonly Dictionary<CapturedMonitor, BitmapSource> mosaicSources = [];
    private readonly Dictionary<double, Cursor> brushCursors = [];
    private readonly Rect virtualBounds;
    private readonly Color[] quickColors =
    [
        Color.FromRgb(255, 0, 0),
        Color.FromRgb(255, 242, 0),
        Color.FromRgb(56, 189, 248),
        Color.FromRgb(128, 128, 128)
    ];
    private readonly Color[] additionalColors =
    [
        Color.FromRgb(112, 130, 56),
        Color.FromRgb(0, 0, 0),
        Color.FromRgb(255, 255, 255),
        Color.FromRgb(138, 43, 226),
        Color.FromRgb(255, 140, 0)
    ];

    private readonly bool englishToolbar = ConfigurationStore.Shared.Configuration.Audio.Language == "en-US";
    private int feedbackButton = -1;
    private long feedbackStart;
    private int feedbackGeneration;
    private Rect? selection;
    private Rect? hoverCandidate;
    private ScreenshotTool? activeTool;
    private Annotation? previewAnnotation;
    private InteractionKind interaction;
    private Point interactionOrigin;
    private Rect interactionSelection;
    private ResizeHandle resizeHandle;
    private Color selectedColor;
    private double selectedStrokeWidth;
    private double selectedFontSize;
    private double selectedMosaicBrushSize = 32;
    private FontWeight selectedFontWeight;
    private Point? textInputOrigin;
    private string textInputBuffer = "";
    private Rect textInputBounds;
    private double textInputFontSize;
    private ToolbarPicker? activePicker;
    private int? pickerSelectionIndex;
    private Rect? pickerAnchorOverride;
    private int movingAnnotationIndex = -1;
    private Annotation? movingAnnotation;
    private bool showsMoreColors;
    private SizeInputStage? sizeInputStage;
    private string sizeInputBuffer = "";
    private int? pendingInputWidth;
    private string? sizeInputError;
    private bool isClosed;
    private RegionRecording? recording;
    private DispatcherTimer? recordingTimer;
    private Drawing.Bitmap? recordingDesktop;
    private Drawing.Bitmap? recordingOutput;
    private Task<BitmapSource?>? recordingFrame;
    private Dictionary<TextAnnotation, BitmapSource> recordingText = [];
    private BitmapSource? recordingMosaic;
    private Rect recordingBounds;
    private nint recordingForegroundWindow;

    internal ScreenshotSession(
        IReadOnlyList<CapturedMonitor> monitors,
        IReadOnlyList<WindowCandidate> windowCandidates,
        Action onClosed,
        Action onRecordingStarted)
    {
        this.monitors = monitors;
        this.windowCandidates = windowCandidates;
        this.onClosed = onClosed;
        this.onRecordingStarted = onRecordingStarted;
        var defaults = ConfigurationStore.Shared.Configuration.ScreenshotDefaults;
        selectedColor = ParseColor(defaults.Color);
        selectedStrokeWidth = defaults.StrokeWidth;
        selectedFontSize = defaults.FontSize;
        selectedFontWeight = ParseFontWeight(defaults.FontWeight);
        virtualBounds = monitors
            .Select(monitor => monitor.Bounds)
            .Aggregate((current, next) => Rect.Union(current, next));
    }

    private Drawing.Bitmap DesktopBitmap => desktopBitmap ??= CreateDesktopBitmap();

    private Drawing.Bitmap DesktopMosaicBitmap => desktopMosaicBitmap ??= CreateMosaicBitmap(DesktopBitmap);

    private Drawing.Bitmap CreateDesktopBitmap()
    {
        var bitmap = new Drawing.Bitmap(
            Math.Max(1, (int)Math.Round(virtualBounds.Width)),
            Math.Max(1, (int)Math.Round(virtualBounds.Height)),
            DrawingImaging.PixelFormat.Format32bppArgb);
        try
        {
            using (var graphics = Drawing.Graphics.FromImage(bitmap))
            {
                graphics.Clear(Drawing.Color.Black);
                foreach (var monitor in monitors)
                {
                    graphics.DrawImageUnscaled(
                        monitor.Bitmap,
                        (int)Math.Round(monitor.Bounds.X - virtualBounds.X),
                        (int)Math.Round(monitor.Bounds.Y - virtualBounds.Y));
                }
            }
            return bitmap;
        }
        catch
        {
            bitmap.Dispose();
            throw;
        }
    }

    internal void Present()
    {
        foreach (var monitor in monitors)
        {
            var window = new OverlayWindow(this, monitor);
            windows.Add(window);
            window.Show();
        }

        var point = CursorPoint();
        var activeWindow = windows.FirstOrDefault(window => window.Monitor.Bounds.Contains(point))
            ?? windows.First();
        activeWindow.ActivateForInput();
        UpdateHover(point);
        UpdateCursor(point);
    }

    internal void Render(OverlaySurface surface, DrawingContext drawingContext)
    {
        var bounds = new Rect(0, 0, surface.ActualWidth, surface.ActualHeight);
        if (recording is null)
        {
            drawingContext.DrawImage(surface.Monitor.ImageSource, bounds);
            drawingContext.DrawRectangle(new SolidColorBrush(Color.FromArgb(122, 0, 0, 0)), null, bounds);
        }

        var effectiveSelection = selection ?? hoverCandidate;
        if (effectiveSelection is null)
        {
            if (surface.Monitor.Bounds.Contains(CursorPoint()))
            {
                DrawInstruction(surface, drawingContext);
            }
            return;
        }

        var visible = Rect.Intersect(effectiveSelection.Value, surface.Monitor.Bounds);
        if (!visible.IsEmpty)
        {
            drawingContext.PushClip(new RectangleGeometry(surface.GlobalToLocal(visible)));
            if (recording is null)
            {
                drawingContext.DrawImage(surface.Monitor.ImageSource, bounds);
            }
            DrawAnnotations(surface, drawingContext);
            drawingContext.Pop();
        }

        var localSelection = surface.GlobalToLocal(effectiveSelection.Value);
        drawingContext.DrawRectangle(
            null,
            new Pen(Brushes.SlateGray, selection is null ? 2 : 1.5),
            localSelection);

        if (selection is null) return;
        if (recording is null) DrawHandles(surface, drawingContext, selection.Value);
        DrawToolbar(surface, drawingContext);
        if (activePicker is not null) DrawActivePicker(surface, drawingContext);
        DrawSizeLabel(surface, drawingContext, selection.Value);
    }

    internal void MouseMoved(bool isDragging)
    {
        var point = CursorPoint();
        if (isDragging)
        {
            MouseDragged(point);
        }
        else if (selection is null && interaction == InteractionKind.None)
        {
            UpdateHover(point);
        }
        UpdateCursor(point);
    }

    internal void MouseDown(int clickCount)
    {
        var point = CursorPoint();
        if (textInputOrigin is not null)
        {
            CommitTextInput();
        }
        if (HandleMoreColorClick(point) || HandleToolbarClick(point))
        {
            Redraw();
            return;
        }
        if (recording is null && clickCount >= 2 && selection?.Contains(point) == true)
        {
            Finish();
            return;
        }

        showsMoreColors = false;
        CancelSizeInput();
        if (selection is null)
        {
            annotations.Clear();
            var candidate = CandidateAt(point);
            if (candidate is not null)
            {
                selection = IntersectVirtual(candidate.Bounds);
                interaction = InteractionKind.Candidate;
                interactionOrigin = point;
                interactionSelection = selection.Value;
            }
            else
            {
                selection = new Rect(point, new Size(0, 0));
                interaction = InteractionKind.Selecting;
                interactionOrigin = point;
            }
            Redraw();
            return;
        }

        if (recording is not null && (!selection.Value.Contains(point)
            || activeTool is null && AnnotationIndexAt(point) < 0)) return;

        var handle = recording is null ? HitHandle(point, selection.Value) : null;
        if (handle is not null)
        {
            interaction = InteractionKind.Resizing;
            resizeHandle = handle.Value;
            interactionOrigin = point;
            interactionSelection = selection.Value;
        }
        else if (AnnotationIndexAt(point) is var annotationIndex && annotationIndex >= 0)
        {
            movingAnnotationIndex = annotationIndex;
            movingAnnotation = annotations[annotationIndex];
            interactionOrigin = point;
            interaction = InteractionKind.MovingAnnotation;
        }
        else if (activeTool is null)
        {
            if (selection.Value.Contains(point))
            {
                interaction = InteractionKind.Moving;
                interactionOrigin = point;
                interactionSelection = selection.Value;
            }
            else
            {
                annotations.Clear();
                var candidate = CandidateAt(point);
                if (candidate is not null)
                {
                    selection = IntersectVirtual(candidate.Bounds);
                    interaction = InteractionKind.Candidate;
                    interactionOrigin = point;
                    interactionSelection = selection.Value;
                }
                else
                {
                    selection = new Rect(point, new Size(0, 0));
                    interaction = InteractionKind.Selecting;
                    interactionOrigin = point;
                }
            }
        }
        else if (selection.Value.Contains(point))
        {
            interactionOrigin = point;
            if (activeTool == ScreenshotTool.Text)
            {
                BeginTextInput(point);
            }
            else if (activeTool is ScreenshotTool.Pen or ScreenshotTool.Mosaic)
            {
                penPoints.Clear();
                penPoints.Add(point);
                if (activeTool == ScreenshotTool.Mosaic)
                    previewAnnotation = new MosaicAnnotation(
                        penPoints.ToArray(),
                        selectedMosaicBrushSize);
                interaction = InteractionKind.Pen;
            }
            else
            {
                interaction = InteractionKind.Annotating;
            }
        }
        UpdateCursor(point);
        Redraw();
    }

    internal void MouseUp()
    {
        if (activePicker is not null)
        {
            FinishPickerSelection();
            RestoreRecordingFocus();
            return;
        }
        if (textInputOrigin is null && previewAnnotation is not null)
        {
            annotations.Add(previewAnnotation);
        }
        if (textInputOrigin is null)
        {
            previewAnnotation = null;
        }
        penPoints.Clear();
        movingAnnotationIndex = -1;
        movingAnnotation = null;
        interaction = InteractionKind.None;
        if (recording is null && selection is { } selected
            && (selected.Width < 6 || selected.Height < 6))
        {
            selection = null;
            UpdateHover(CursorPoint());
        }
        UpdateCursor(CursorPoint());
        Redraw();
        RestoreRecordingFocus();
    }

    private void RestoreRecordingFocus()
    {
        if (recording is null || activeTool is not null || recordingForegroundWindow == nint.Zero) return;
        var foreground = NativeMethods.GetForegroundWindow();
        NativeMethods.GetWindowThreadProcessId(foreground, out var process);
        if (process == (uint)Environment.ProcessId)
            NativeMethods.SetForegroundWindow(recordingForegroundWindow);
        recordingForegroundWindow = nint.Zero;
    }

    internal bool KeyDown(Key key)
    {
        if (textInputOrigin is not null)
        {
            if (key == Key.Escape)
            {
                CancelTextInput();
                return true;
            }
            if (key == Key.Enter)
            {
                CommitTextInput();
                return true;
            }
            if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control) && key == Key.Z)
            {
                return false;
            }
        }
        if (key == Key.Escape)
        {
            Cancel();
            return true;
        }
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control) && key == Key.Z)
        {
            Undo();
            return true;
        }
        if (sizeInputStage is not null)
        {
            return HandleSizeInputKey(key);
        }
        if (key == Key.Enter)
        {
            Finish();
            return true;
        }
        return false;
    }

    internal void UpdateTextInput(string text, Rect bounds, double fontSize)
    {
        if (textInputOrigin is null) return;
        textInputBuffer = text;
        textInputBounds = bounds;
        textInputFontSize = fontSize;
    }

    internal void Cancel()
    {
        if (recording is not null) EndRecording();
        else Close();
    }

    private void BeginTextInput(Point point)
    {
        if (selection is not { } selected) return;
        IEnumerable<OverlayWindow> textWindows = recording is null ? windows : recordingInputs.Values;
        var target = textWindows.FirstOrDefault(window => window.Surface.DesktopBounds.Contains(point)
            && Rect.Intersect(selected, window.Monitor.Bounds) is { Width: > 0, Height: > 0 });
        if (target is null) return;
        textInputOrigin = point;
        textInputBuffer = "";
        interaction = InteractionKind.None;
        previewAnnotation = null;
        target.BeginTextEditor(point, selectedFontSize, selectedFontWeight, selectedColor, selected);
    }

    private void CommitTextInput()
    {
        if (textInputOrigin is null) return;
        if (!string.IsNullOrWhiteSpace(textInputBuffer))
        {
            annotations.Add(new TextAnnotation(
                textInputBuffer,
                textInputBounds,
                selectedColor,
                textInputFontSize,
                selectedFontWeight));
        }
        textInputOrigin = null;
        textInputBuffer = "";
        previewAnnotation = null;
        foreach (var window in windows.Concat(recordingInputs.Values)) window.EndTextEditor();
        Redraw();
    }

    private void CancelTextInput()
    {
        textInputOrigin = null;
        textInputBuffer = "";
        previewAnnotation = null;
        foreach (var window in windows.Concat(recordingInputs.Values)) window.EndTextEditor();
        Redraw();
    }

    public void Dispose()
    {
        Close();
    }

    private void MouseDragged(Point point)
    {
        if (activePicker is not null)
        {
            UpdatePickerSelection(point);
            return;
        }
        switch (interaction)
        {
            case InteractionKind.Candidate:
                if (Distance(point, interactionOrigin) >= 4)
                {
                    annotations.Clear();
                    selection = IntersectVirtual(Normalized(interactionOrigin, point));
                    interaction = InteractionKind.Selecting;
                }
                else
                {
                    selection = interactionSelection;
                }
                break;
            case InteractionKind.Selecting:
                selection = IntersectVirtual(Normalized(interactionOrigin, point));
                break;
            case InteractionKind.Moving:
                selection = ClampMovedSelection(new Rect(
                    interactionSelection.X + point.X - interactionOrigin.X,
                    interactionSelection.Y + point.Y - interactionOrigin.Y,
                    interactionSelection.Width,
                    interactionSelection.Height));
                break;
            case InteractionKind.Resizing:
                selection = IntersectVirtual(ResizeSelection(
                    interactionSelection,
                    resizeHandle,
                    point.X - interactionOrigin.X,
                    point.Y - interactionOrigin.Y));
                break;
            case InteractionKind.Annotating:
                if (selection is null) break;
                var constrained = ClampPoint(point, selection.Value);
                previewAnnotation = activeTool switch
                {
                    ScreenshotTool.Rectangle => new RectangleAnnotation(
                        interactionOrigin,
                        constrained,
                        selectedColor,
                        selectedStrokeWidth),
                    ScreenshotTool.Arrow => new ArrowAnnotation(
                        interactionOrigin,
                        constrained,
                        selectedColor,
                        selectedStrokeWidth),
                    _ => null
                };
                break;
            case InteractionKind.Pen:
                if (selection?.Contains(point) != true) break;
                if (penPoints.Count > 0 && Distance(point, penPoints[^1]) < 0.5) break;
                penPoints.Add(point);
                previewAnnotation = activeTool == ScreenshotTool.Mosaic
                    ? new MosaicAnnotation(penPoints.ToArray(), selectedMosaicBrushSize)
                    : new PenAnnotation(
                        penPoints.ToArray(),
                        selectedColor,
                        selectedStrokeWidth);
                break;
            case InteractionKind.MovingAnnotation:
                if (movingAnnotation is null
                    || movingAnnotationIndex < 0
                    || movingAnnotationIndex >= annotations.Count) break;
                annotations[movingAnnotationIndex] = TranslateAnnotation(
                    movingAnnotation,
                    point.X - interactionOrigin.X,
                    point.Y - interactionOrigin.Y);
                break;
        }
        Redraw();
    }

    private void UpdateHover(Point point)
    {
        hoverCandidate = CandidateAt(point)?.Bounds;
        Redraw();
    }

    private WindowCandidate? CandidateAt(Point point)
    {
        return windowCandidates.FirstOrDefault(candidate =>
            candidate.Bounds.Contains(point)
            && monitors.Any(monitor =>
                !Rect.Intersect(monitor.Bounds, candidate.Bounds).IsEmpty));
    }

    private void DrawInstruction(OverlaySurface surface, DrawingContext drawingContext)
    {
        var text = CreateText(
            englishToolbar ? "Click a window or drag to select an area   ·   Esc to cancel" : "单击窗口或拖动框选区域　·　Esc 取消",
            15,
            Brushes.White,
            FontWeights.Medium,
            surface);
        var background = new Rect(
            (surface.ActualWidth - text.Width) / 2 - 14,
            (surface.ActualHeight - text.Height) / 2 - 9,
            text.Width + 28,
            text.Height + 18);
        drawingContext.DrawRoundedRectangle(
            new SolidColorBrush(Color.FromArgb(184, 0, 0, 0)),
            null,
            background,
            8,
            8);
        drawingContext.DrawText(
            text,
            new Point(background.X + 14, background.Y + 9));
    }

    private void DrawAnnotations(OverlaySurface surface, DrawingContext drawingContext)
    {
        foreach (var annotation in annotations)
        {
            DrawAnnotation(surface, drawingContext, annotation);
        }
        if (previewAnnotation is not null)
        {
            DrawAnnotation(surface, drawingContext, previewAnnotation);
        }
    }

    private void DrawAnnotation(
        OverlaySurface surface,
        DrawingContext drawingContext,
        Annotation annotation)
    {
        var brush = new SolidColorBrush(annotation.Color);
        var pen = new Pen(brush, Math.Max(0.5, annotation.StrokeWidth))
        {
            StartLineCap = PenLineCap.Round,
            EndLineCap = PenLineCap.Round,
            LineJoin = PenLineJoin.Round
        };
        switch (annotation)
        {
            case RectangleAnnotation rectangle:
                drawingContext.DrawRectangle(
                    null,
                    pen,
                    surface.GlobalToLocal(Normalized(rectangle.Start, rectangle.End)));
                break;
            case ArrowAnnotation arrow:
                var start = surface.GlobalToLocal(arrow.Start);
                var end = surface.GlobalToLocal(arrow.End);
                drawingContext.DrawLine(pen, start, end);
                var angle = Math.Atan2(end.Y - start.Y, end.X - start.X);
                var arrowLength = 10 + annotation.StrokeWidth * 1.5;
                drawingContext.DrawLine(
                    pen,
                    end,
                    new Point(
                        end.X - arrowLength * Math.Cos(angle - Math.PI / 6),
                        end.Y - arrowLength * Math.Sin(angle - Math.PI / 6)));
                drawingContext.DrawLine(
                    pen,
                    end,
                    new Point(
                        end.X - arrowLength * Math.Cos(angle + Math.PI / 6),
                        end.Y - arrowLength * Math.Sin(angle + Math.PI / 6)));
                break;
            case PenAnnotation freehand when freehand.Points.Count >= 2:
                var geometry = new StreamGeometry();
                using (var context = geometry.Open())
                {
                    context.BeginFigure(
                        surface.GlobalToLocal(freehand.Points[0]),
                        false,
                        false);
                    context.PolyLineTo(
                        freehand.Points.Skip(1).Select(surface.GlobalToLocal).ToArray(),
                        true,
                        false);
                }
                geometry.Freeze();
                drawingContext.DrawGeometry(null, pen, geometry);
                break;
            case TextAnnotation text:
                var local = surface.GlobalToLocal(text.Bounds);
                drawingContext.PushTransform(new MatrixTransform(new Matrix(
                    local.Width / text.Bounds.Width, 0, 0, local.Height / text.Bounds.Height,
                    local.X, local.Y)));
                ScreenshotTextLayout.Draw(drawingContext, text);
                drawingContext.Pop();
                break;
            case MosaicAnnotation mosaic:
                DrawMosaic(surface, drawingContext, mosaic);
                break;
        }
    }

    private void DrawMosaic(
        OverlaySurface surface,
        DrawingContext drawingContext,
        MosaicAnnotation mosaic)
    {
        if (mosaic.Points.Count == 0) return;
        Geometry clip;
        if (mosaic.Points.Count == 1)
        {
            var center = surface.GlobalToLocal(mosaic.Points[0]);
            var radius = surface.GlobalToLocal(new Rect(
                surface.Monitor.Bounds.Left,
                surface.Monitor.Bounds.Top,
                mosaic.BrushSize / 2,
                mosaic.BrushSize / 2));
            clip = new EllipseGeometry(center, radius.Width, radius.Height);
        }
        else
        {
            var path = new StreamGeometry();
            using (var context = path.Open())
            {
                context.BeginFigure(surface.GlobalToLocal(mosaic.Points[0]), false, false);
                context.PolyLineTo(
                    mosaic.Points.Skip(1).Select(surface.GlobalToLocal).ToArray(),
                    true,
                    false);
            }
            path.Freeze();
            var localBrushSize = surface.GlobalToLocal(new Rect(
                surface.Monitor.Bounds.Left,
                surface.Monitor.Bounds.Top,
                mosaic.BrushSize,
                mosaic.BrushSize)).Width;
            clip = path.GetWidenedPathGeometry(new Pen(Brushes.Black, localBrushSize)
            {
                StartLineCap = PenLineCap.Round,
                EndLineCap = PenLineCap.Round,
                LineJoin = PenLineJoin.Round
            });
        }
        drawingContext.PushClip(clip);
        DrawMosaicSource(surface, drawingContext, MosaicBounds(mosaic.Points, mosaic.BrushSize));
        drawingContext.Pop();
    }

    private void DrawMosaicSource(
        OverlaySurface surface,
        DrawingContext drawingContext,
        Rect bounds)
    {
        if (Rect.Intersect(bounds, surface.Monitor.Bounds).IsEmpty) return;
        if (recording is not null)
        {
            if (recordingMosaic is not null)
            {
                var liveDrawing = new ImageDrawing(recordingMosaic, surface.GlobalToLocal(recordingBounds));
                RenderOptions.SetBitmapScalingMode(liveDrawing, BitmapScalingMode.NearestNeighbor);
                drawingContext.DrawDrawing(liveDrawing);
            }
            return;
        }
        if (!mosaicSources.TryGetValue(surface.Monitor, out var mosaicSource))
        {
            mosaicSource = CreateMosaicSource(surface.Monitor.Bitmap);
            mosaicSources.Add(surface.Monitor, mosaicSource);
        }
        var imageDrawing = new ImageDrawing(
            mosaicSource,
            surface.GlobalToLocal(surface.Monitor.Bounds));
        RenderOptions.SetBitmapScalingMode(imageDrawing, BitmapScalingMode.NearestNeighbor);
        drawingContext.DrawDrawing(imageDrawing);
    }

    private int AnnotationIndexAt(Point point)
    {
        for (var index = annotations.Count - 1; index >= 0; index--)
        {
            if (annotations[index] is MosaicAnnotation && activeTool is not null) continue;
            if (AnnotationHit(annotations[index], point)) return index;
        }
        return -1;
    }

    private bool AnnotationHit(Annotation annotation, Point point)
    {
        var scale = selection is null ? 1 : MonitorForSelection(selection.Value).Scale;
        var tolerance = Math.Max(6 * scale, annotation.StrokeWidth * scale / 2 + 4 * scale);
        switch (annotation)
        {
            case RectangleAnnotation rectangle:
                var rect = Normalized(rectangle.Start, rectangle.End);
                var nearHorizontal = point.X >= rect.Left - tolerance
                    && point.X <= rect.Right + tolerance
                    && (Math.Abs(point.Y - rect.Top) <= tolerance
                        || Math.Abs(point.Y - rect.Bottom) <= tolerance);
                var nearVertical = point.Y >= rect.Top - tolerance
                    && point.Y <= rect.Bottom + tolerance
                    && (Math.Abs(point.X - rect.Left) <= tolerance
                        || Math.Abs(point.X - rect.Right) <= tolerance);
                return nearHorizontal || nearVertical;
            case ArrowAnnotation arrow:
                if (DistanceToSegment(point, arrow.Start, arrow.End) <= tolerance) return true;
                var angle = Math.Atan2(arrow.End.Y - arrow.Start.Y, arrow.End.X - arrow.Start.X);
                var arrowLength = (10 + annotation.StrokeWidth * 1.5) * scale;
                var first = new Point(
                    arrow.End.X - arrowLength * Math.Cos(angle - Math.PI / 6),
                    arrow.End.Y - arrowLength * Math.Sin(angle - Math.PI / 6));
                var second = new Point(
                    arrow.End.X - arrowLength * Math.Cos(angle + Math.PI / 6),
                    arrow.End.Y - arrowLength * Math.Sin(angle + Math.PI / 6));
                return DistanceToSegment(point, arrow.End, first) <= tolerance
                    || DistanceToSegment(point, arrow.End, second) <= tolerance;
            case PenAnnotation pen:
                if (pen.Points.Count == 1) return Distance(point, pen.Points[0]) <= tolerance;
                for (var index = 1; index < pen.Points.Count; index++)
                {
                    if (DistanceToSegment(point, pen.Points[index - 1], pen.Points[index]) <= tolerance)
                        return true;
                }
                return false;
            case TextAnnotation text:
                var textRect = text.Bounds;
                textRect.Inflate(4 * scale, 4 * scale);
                return textRect.Contains(point);
            case MosaicAnnotation mosaic:
                var mosaicTolerance = mosaic.BrushSize / 2 + 4 * scale;
                if (mosaic.Points.Count == 1)
                    return Distance(point, mosaic.Points[0]) <= mosaicTolerance;
                for (var index = 1; index < mosaic.Points.Count; index++)
                {
                    if (DistanceToSegment(point, mosaic.Points[index - 1], mosaic.Points[index])
                        <= mosaicTolerance) return true;
                }
                return false;
            default:
                return false;
        }
    }

    private static double DistanceToSegment(Point point, Point start, Point end)
    {
        var dx = end.X - start.X;
        var dy = end.Y - start.Y;
        var lengthSquared = dx * dx + dy * dy;
        if (lengthSquared <= 0) return Distance(point, start);
        var position = Math.Clamp(
            ((point.X - start.X) * dx + (point.Y - start.Y) * dy) / lengthSquared,
            0,
            1);
        return Distance(point, new Point(start.X + position * dx, start.Y + position * dy));
    }

    private static Annotation TranslateAnnotation(Annotation annotation, double dx, double dy)
    {
        Point Moved(Point point) => new(point.X + dx, point.Y + dy);
        return annotation switch
        {
            RectangleAnnotation rectangle => new RectangleAnnotation(
                Moved(rectangle.Start), Moved(rectangle.End), rectangle.Color, rectangle.StrokeWidth),
            ArrowAnnotation arrow => new ArrowAnnotation(
                Moved(arrow.Start), Moved(arrow.End), arrow.Color, arrow.StrokeWidth),
            PenAnnotation pen => new PenAnnotation(
                pen.Points.Select(Moved).ToArray(), pen.Color, pen.StrokeWidth),
            TextAnnotation text => new TextAnnotation(
                text.Text, new Rect(Moved(text.Bounds.Location), text.Bounds.Size), text.Color, text.FontSize, text.FontWeight),
            MosaicAnnotation mosaic => new MosaicAnnotation(
                mosaic.Points.Select(Moved).ToArray(), mosaic.BrushSize),
            _ => annotation
        };
    }

    private void DrawHandles(
        OverlaySurface surface,
        DrawingContext drawingContext,
        Rect selected)
    {
        var scale = MonitorForSelection(selected).Scale;
        foreach (var handle in Enum.GetValues<ResizeHandle>())
        {
            var point = HandlePoint(handle, selected);
            if (!surface.Monitor.Bounds.InflateCopy(6 * scale).Contains(point)) continue;
            var local = surface.GlobalToLocal(point);
            drawingContext.DrawEllipse(
                Brushes.White,
                new Pen(SystemColors.HighlightBrush, 1),
                local,
                4,
                4);
        }
    }

    private void DrawToolbar(OverlaySurface surface, DrawingContext drawingContext)
    {
        if (selection is null) return;
        var target = MonitorForSelection(selection.Value);
        if (!ReferenceEquals(surface.Monitor, target)) return;

        var toolbar = surface.GlobalToLocal(ToolbarRect());
        drawingContext.DrawRoundedRectangle(
            Brushes.White,
            new Pen(Brushes.Gray, 0.7),
            toolbar,
            8,
            8);

        for (var index = 0; index < ToolbarButtonCount; index++)
        {
            var globalButton = ToolbarButtonRect(index);
            var button = surface.GlobalToLocal(globalButton);
            var inset = new Rect(button.X + 2, button.Y + 4, Math.Max(0, button.Width - 4), button.Height - 8);
            if (index == CompactPaletteButtonIndex || index == SizeButtonIndex || index == SaveButtonIndex)
                drawingContext.DrawLine(new Pen(Brushes.LightSlateGray, 0.7), new Point(button.Left, button.Top + 10), new Point(button.Left, button.Bottom - 10));
            if (index == FinishButtonIndex && recording is null)
                drawingContext.DrawRoundedRectangle(new SolidColorBrush(Color.FromRgb(253, 244, 165)), null, inset, 5, 5);
            if (index == RecordButtonIndex && recording is not null)
                drawingContext.DrawRoundedRectangle(Brushes.Maroon, null, inset, 5, 5);
            if (index < ToolButtonCount && activeTool == (ScreenshotTool)index
                || index == SizeButtonIndex && sizeInputStage is not null
                || recording is not null && index == DesktopButtonIndex && activeTool is null)
            {
                drawingContext.DrawRoundedRectangle(
                    new SolidColorBrush(Color.FromRgb(194, 202, 221)),
                    null,
                    new Rect(
                        button.X + 2,
                        button.Y + 3,
                        button.Width - 4,
                        button.Height - 6),
                    5,
                    5);
            }

            if (feedbackButton == index)
            {
                var remaining = Math.Clamp(1 - (Environment.TickCount64 - feedbackStart) / 130.0, 0, 1);
                drawingContext.DrawRoundedRectangle(new SolidColorBrush(Color.FromArgb((byte)(72 * remaining), 82, 92, 118)), null, inset, 5, 5);
            }
            if (index == CompactPaletteButtonIndex)
            {
                DrawCompactPalette(surface, drawingContext, button);
                continue;
            }
            if (index == StrokeSizeButtonIndex)
            {
                DrawStrokeSizeControl(surface, drawingContext, button);
                continue;
            }

            var title = ToolbarTitle(index);
            var brush = recording is not null && index is SizeButtonIndex or SaveButtonIndex
                ? Brushes.DimGray : recording is not null && index == RecordButtonIndex ? Brushes.White : Brushes.Black;
            DrawCenteredText(surface, drawingContext, title, button, brush);
        }

        if (showsMoreColors)
        {
            DrawMoreColorPalette(surface, drawingContext);
        }
    }

    private void DrawCompactPalette(
        OverlaySurface surface,
        DrawingContext drawingContext,
        Rect localSlot)
    {
        var capsule = new Rect(
            localSlot.X + 2,
            localSlot.Y + 4,
            localSlot.Width - 4,
            localSlot.Height - 8);
        drawingContext.DrawRoundedRectangle(
            new SolidColorBrush(Color.FromArgb(184, 242, 242, 242)),
            new Pen(activePicker == ToolbarPicker.Color ? SystemColors.HighlightBrush : Brushes.Gray,
                activePicker == ToolbarPicker.Color ? 1.5 : 0.7),
            capsule,
            6,
            6);
        drawingContext.DrawEllipse(
            new SolidColorBrush(selectedColor),
            new Pen(Brushes.Gray, 0.7),
            new Point(localSlot.X + 13, localSlot.Y + localSlot.Height / 2),
            6,
            6);
        DrawCenteredText(
            surface,
            drawingContext,
            "↕",
            new Rect(localSlot.Right - 18, localSlot.Y, 12, localSlot.Height),
            Brushes.DimGray,
            13);
    }

    private void DrawStrokeSizeControl(
        OverlaySurface surface,
        DrawingContext drawingContext,
        Rect slot)
    {
        var capsule = new Rect(slot.X + 2, slot.Y + 4, slot.Width - 4, slot.Height - 8);
        drawingContext.DrawRoundedRectangle(
            new SolidColorBrush(Color.FromArgb(184, 242, 242, 242)),
            new Pen(
                activePicker == ToolbarPicker.Size ? SystemColors.HighlightBrush : Brushes.Gray,
                activePicker == ToolbarPicker.Size ? 1.5 : 0.7),
            capsule,
            6,
            6);
        if (activeTool == ScreenshotTool.Text)
        {
            DrawCenteredText(
                surface,
                drawingContext,
                $"Aa {(int)selectedFontSize}",
                slot,
                Brushes.Black,
                11);
            return;
        }
        if (activeTool == ScreenshotTool.Mosaic)
        {
            DrawCenteredText(
                surface,
                drawingContext,
                $"● {(int)selectedMosaicBrushSize}",
                slot,
                Brushes.Black,
                11);
            return;
        }

        var samplePen = new Pen(new SolidColorBrush(selectedColor), Math.Min(9, selectedStrokeWidth))
        {
            StartLineCap = PenLineCap.Round,
            EndLineCap = PenLineCap.Round
        };
        drawingContext.DrawLine(
            samplePen,
            new Point(slot.X + 7, slot.Y + slot.Height / 2),
            new Point(slot.X + 25, slot.Y + slot.Height / 2));
        DrawCenteredText(
            surface,
            drawingContext,
            ((int)selectedStrokeWidth).ToString(CultureInfo.InvariantCulture),
            new Rect(slot.Right - 25, slot.Y, 20, slot.Height),
            Brushes.Black,
            11);
    }

    private Color[] PickerColors() => quickColors.Concat(additionalColors).ToArray();

    private double[] PickerSizeValues() =>
        (activeTool == ScreenshotTool.Text
            ? fontSizes
            : activeTool == ScreenshotTool.Mosaic ? mosaicBrushSizes : strokeWidths).Reverse().ToArray();

    private CapturedMonitor PickerMonitor(Rect anchor)
    {
        var center = new Point(anchor.Left + anchor.Width / 2, anchor.Top + anchor.Height / 2);
        return monitors.FirstOrDefault(monitor => monitor.Bounds.Contains(center))
            ?? (selection is { } selected ? MonitorForSelection(selected) : monitors[0]);
    }

    private void BeginPicker(ToolbarPicker picker, Rect? anchor = null)
    {
        activePicker = picker;
        pickerAnchorOverride = anchor;
        if (picker == ToolbarPicker.Color)
        {
            pickerSelectionIndex = 4;
        }
        else
        {
            var current = activeTool == ScreenshotTool.Text
                ? selectedFontSize
                : activeTool == ScreenshotTool.Mosaic ? selectedMosaicBrushSize : selectedStrokeWidth;
            pickerSelectionIndex = Array.IndexOf(PickerSizeValues(), current);
            if (pickerSelectionIndex.Value < 0) pickerSelectionIndex = 0;
        }
        Redraw();
    }

    private Color PickerColor(int row)
    {
        var colors = PickerColors();
        var current = Array.IndexOf(colors, selectedColor);
        if (current < 0) current = 0;
        var index = (current + 4 - row + colors.Length) % colors.Length;
        return colors[index];
    }

    private int PickerRowCount() =>
        activePicker == ToolbarPicker.Color ? PickerColors().Length : PickerSizeValues().Length;

    private int PickerCurrentRow()
    {
        if (activePicker == ToolbarPicker.Color) return 4;
        var current = activeTool == ScreenshotTool.Text
            ? selectedFontSize
            : activeTool == ScreenshotTool.Mosaic ? selectedMosaicBrushSize : selectedStrokeWidth;
        return Math.Max(0, Array.IndexOf(PickerSizeValues(), current));
    }

    private Rect PickerOptionRect(int row)
    {
        var buttonIndex = activePicker == ToolbarPicker.Color
            ? CompactPaletteButtonIndex
            : StrokeSizeButtonIndex;
        var button = pickerAnchorOverride ?? ToolbarButtonRect(buttonIndex);
        var monitor = PickerMonitor(button);
        var scale = monitor.Scale;
        var rowHeight = 24 * scale;
        var width = (activePicker == ToolbarPicker.Color ? 36 : 52) * scale;
        var currentRow = PickerCurrentRow();
        var centerY = button.Top + button.Height / 2 + (row - currentRow) * rowHeight;
        var topCenter = button.Top + button.Height / 2 - currentRow * rowHeight;
        var bottomCenter = button.Top + button.Height / 2
            + (PickerRowCount() - 1 - currentRow) * rowHeight;
        var upperOverflow = Math.Max(0, monitor.Bounds.Top + 8 * scale - (topCenter - rowHeight / 2));
        var lowerOverflow = Math.Max(0, bottomCenter + rowHeight / 2 - (monitor.Bounds.Bottom - 8 * scale));
        centerY += upperOverflow - lowerOverflow;
        return new Rect(
            button.Left + button.Width / 2 - width / 2,
            centerY - rowHeight / 2,
            width,
            rowHeight);
    }

    private void DrawActivePicker(OverlaySurface surface, DrawingContext drawingContext)
    {
        if (activePicker is null) return;
        var anchor = pickerAnchorOverride ?? ToolbarButtonRect(
            activePicker == ToolbarPicker.Color ? CompactPaletteButtonIndex : StrokeSizeButtonIndex);
        if (!ReferenceEquals(surface.Monitor, PickerMonitor(anchor))) return;
        var rows = PickerRowCount();
        var rects = Enumerable.Range(0, rows).Select(PickerOptionRect).ToArray();
        var rail = rects.Skip(1).Aggregate(rects[0], (current, next) => Rect.Union(current, next));
        rail.Inflate(4 * surface.Monitor.Scale, 4 * surface.Monitor.Scale);
        var localRail = surface.GlobalToLocal(rail);
        drawingContext.DrawRoundedRectangle(
            new SolidColorBrush(Color.FromArgb(252, 245, 245, 245)),
            new Pen(Brushes.Gray, 0.7),
            localRail,
            12,
            12);

        for (var row = 0; row < rows; row++)
        {
            var rect = surface.GlobalToLocal(rects[row]);
            rect.Inflate(-2, -2);
            if (pickerSelectionIndex == row)
            {
                drawingContext.DrawRoundedRectangle(
                    new SolidColorBrush(Color.FromRgb(194, 202, 221)),
                    null,
                    rect,
                    8,
                    8);
            }
            if (activePicker == ToolbarPicker.Color)
            {
                drawingContext.DrawEllipse(
                    new SolidColorBrush(PickerColor(row)),
                    new Pen(Brushes.Gray, 0.7),
                    new Point(rect.Left + rect.Width / 2, rect.Top + rect.Height / 2),
                    8,
                    8);
            }
            else
            {
                var value = PickerSizeValues()[row];
                if (activeTool == ScreenshotTool.Text)
                {
                    DrawCenteredText(surface, drawingContext, $"Aa {(int)value}", rect, Brushes.Black, 11);
                }
                else if (activeTool == ScreenshotTool.Mosaic)
                {
                    DrawCenteredText(surface, drawingContext, $"● {(int)value}", rect, Brushes.Black, 11);
                }
                else
                {
                    var pen = new Pen(new SolidColorBrush(selectedColor), Math.Min(10, value))
                    {
                        StartLineCap = PenLineCap.Round,
                        EndLineCap = PenLineCap.Round
                    };
                    drawingContext.DrawLine(
                        pen,
                        new Point(rect.Left + 8, rect.Top + rect.Height / 2),
                        new Point(rect.Right - 22, rect.Top + rect.Height / 2));
                    DrawCenteredText(
                        surface,
                        drawingContext,
                        ((int)value).ToString(CultureInfo.InvariantCulture),
                        new Rect(rect.Right - 20, rect.Top, 16, rect.Height),
                        Brushes.Black,
                        10);
                }
            }
        }
    }

    private void UpdatePickerSelection(Point point)
    {
        var rows = Enumerable.Range(0, PickerRowCount());
        pickerSelectionIndex = rows.MinBy(row => Math.Abs(PickerOptionRect(row).Top
            + PickerOptionRect(row).Height / 2 - point.Y));
        Redraw();
    }

    private void FinishPickerSelection()
    {
        if (activePicker is null || pickerSelectionIndex is null)
        {
            activePicker = null;
            pickerAnchorOverride = null;
            return;
        }
        if (activePicker == ToolbarPicker.Color)
        {
            selectedColor = PickerColor(pickerSelectionIndex.Value);
        }
        else
        {
            var value = PickerSizeValues()[pickerSelectionIndex.Value];
            if (activeTool == ScreenshotTool.Text) selectedFontSize = value;
            else if (activeTool == ScreenshotTool.Mosaic) selectedMosaicBrushSize = value;
            else selectedStrokeWidth = value;
        }
        activePicker = null;
        pickerSelectionIndex = null;
        pickerAnchorOverride = null;
        UpdateCursor(CursorPoint());
        Redraw();
    }

    private void DrawMoreColorPalette(
        OverlaySurface surface,
        DrawingContext drawingContext)
    {
        var palette = surface.GlobalToLocal(MoreColorPaletteRect());
        drawingContext.DrawRoundedRectangle(
            new SolidColorBrush(Color.FromArgb(252, 245, 245, 245)),
            new Pen(Brushes.Gray, 0.7),
            palette,
            8,
            8);
        for (var index = 0; index < additionalColors.Length; index++)
        {
            var swatch = surface.GlobalToLocal(MoreColorSwatchRect(index));
            drawingContext.DrawEllipse(
                new SolidColorBrush(additionalColors[index]),
                new Pen(
                    selectedColor == additionalColors[index]
                        ? SystemColors.HighlightBrush
                        : Brushes.Gray,
                    selectedColor == additionalColors[index] ? 2.5 : 0.7),
                new Point(swatch.X + swatch.Width / 2, swatch.Y + swatch.Height / 2),
                swatch.Width / 2,
                swatch.Height / 2);
        }
    }

    private void DrawSizeLabel(
        OverlaySurface surface,
        DrawingContext drawingContext,
        Rect selected)
    {
        var target = MonitorContainingTopLeft(selected);
        if (!ReferenceEquals(surface.Monitor, target)) return;

        string text;
        if (sizeInputStage == SizeInputStage.Width)
        {
            var input = string.IsNullOrEmpty(sizeInputBuffer) ? "…" : sizeInputBuffer;
            text = englishToolbar ? $"Width: {input} px · Enter for height" : $"正在输入宽度：{input} px  按回车继续";
        }
        else if (sizeInputStage == SizeInputStage.Height)
        {
            var input = string.IsNullOrEmpty(sizeInputBuffer) ? "…" : sizeInputBuffer;
            text = englishToolbar ? $"Height: {input} px · Enter to apply" : $"宽度 {pendingInputWidth ?? (int)Math.Round(selected.Width)} px · 正在输入高度：{input} px  按回车应用";
        }
        else if (recording is { } capture)
        {
            text = (englishToolbar ? "Recording " : "录像 ") + $"{capture.Width} × {capture.Height}";
        }
        else
        {
            text = $"{Math.Max(1, (int)Math.Round(selected.Width))} × {Math.Max(1, (int)Math.Round(selected.Height))}";
        }
        if (!string.IsNullOrWhiteSpace(sizeInputError))
        {
            text += $" · {sizeInputError}";
        }

        var formatted = CreateText(
            text,
            11,
            string.IsNullOrEmpty(sizeInputError) ? Brushes.White : Brushes.Gold,
            FontWeights.Medium,
            surface,
            "Consolas");
        var scale = target.Scale;
        var width = Math.Min(
            (formatted.Width + 12) * scale,
            target.Bounds.Width - 16 * scale);
        var x = Math.Clamp(
            selected.X,
            target.Bounds.Left + 8 * scale,
            target.Bounds.Right - width - 8 * scale);
        var desiredY = selected.Y - (formatted.Height + 12) * scale;
        var y = desiredY >= target.Bounds.Top + 8 * scale
            ? desiredY
            : selected.Y + 6 * scale;
        var global = new Rect(
            x,
            y,
            width,
            (formatted.Height + 6) * scale);
        var local = surface.GlobalToLocal(global);
        drawingContext.DrawRoundedRectangle(
            new SolidColorBrush(Color.FromArgb(184, 0, 0, 0)),
            null,
            local,
            4,
            4);
        drawingContext.DrawText(
            formatted,
            new Point(local.X + 6, local.Y + 3));
    }

    private bool HandleToolbarClick(Point point)
    {
        if (selection is null || !ToolbarRect().Contains(point)) return false;
        for (var index = 0; index < ToolbarButtonCount; index++)
        {
            if (!ToolbarButtonRect(index).Contains(point)) continue;
            PulseToolbar(index);
            if (index != SizeButtonIndex) CancelSizeInput();
            if (recording is not null && index < ToolButtonCount && activeTool == (ScreenshotTool)index)
            {
                activeTool = null;
                showsMoreColors = false;
                return true;
            }
            switch (index)
            {
                case 0:
                case 1:
                case 2:
                    var tool = (ScreenshotTool)index;
                    activeTool = activeTool == tool ? null : tool;
                    showsMoreColors = false;
                    break;
                case 3:
                    activeTool = ScreenshotTool.Text;
                    showsMoreColors = false;
                    BeginPicker(ToolbarPicker.Size, ToolbarButtonRect(index));
                    break;
                case 4:
                    activeTool = ScreenshotTool.Mosaic;
                    showsMoreColors = false;
                    BeginPicker(ToolbarPicker.Size, ToolbarButtonRect(index));
                    break;
                case StrokeSizeButtonIndex:
                    showsMoreColors = false;
                    BeginPicker(ToolbarPicker.Size);
                    break;
                case CompactPaletteButtonIndex:
                    BeginPicker(ToolbarPicker.Color);
                    break;
                case SizeButtonIndex:
                    showsMoreColors = false;
                    ToggleSizeInput();
                    break;
                case UndoButtonIndex:
                    Undo();
                    showsMoreColors = false;
                    break;
                case SaveButtonIndex:
                    showsMoreColors = false;
                    Save();
                    break;
                case var button when button == FinishButtonIndex:
                    Finish();
                    break;
                case CaptureRecordButtonIndex:
                    StartRecording();
                    break;
                case DesktopButtonIndex:
                    activeTool = null;
                    showsMoreColors = false;
                    break;
                case RecordingStopButtonIndex:
                    EndRecording();
                    break;
            }
            return true;
        }
        return true;
    }

    private bool HandleMoreColorClick(Point point)
    {
        if (!showsMoreColors || !MoreColorPaletteRect().Contains(point)) return false;
        for (var index = 0; index < additionalColors.Length; index++)
        {
            var hitArea = MoreColorSwatchRect(index);
            hitArea.Inflate(4, 4);
            if (!hitArea.Contains(point)) continue;
            selectedColor = additionalColors[index];
            showsMoreColors = false;
            CancelSizeInput();
            return true;
        }
        return true;
    }

    private void HandleCompactPaletteClick(Point point)
    {
        for (var index = 0; index < quickColors.Length; index++)
        {
            if (!CompactQuickColorRect(index).Contains(point)) continue;
            selectedColor = quickColors[index];
            showsMoreColors = false;
            return;
        }
        if (CompactPaletteMoreRect().Contains(point))
        {
            showsMoreColors = !showsMoreColors;
        }
    }

    private async void PulseToolbar(int index)
    {
        if (!SystemParameters.ClientAreaAnimation) return;
        var generation = ++feedbackGeneration;
        feedbackButton = index;
        feedbackStart = Environment.TickCount64;
        do
        {
            foreach (var window in windows) window.Surface.InvalidateVisual();
            await Task.Delay(20);
            if (isClosed || generation != feedbackGeneration) return;
        } while (Environment.TickCount64 - feedbackStart < 130);
        feedbackButton = -1;
        foreach (var window in windows) window.Surface.InvalidateVisual();
    }

    private string ToolbarTitle(int index)
    {
        return index switch
        {
            0 => englishToolbar ? "Rect" : "矩形",
            1 => englishToolbar ? "Arrow" : "箭头",
            2 => englishToolbar ? "Pen" : "画笔",
            3 => englishToolbar ? "Text ▾" : "文字 ▾",
            4 => englishToolbar ? "Mosaic ▾" : "马赛克 ▾",
            SizeButtonIndex => englishToolbar ? "Pixels" : "像素",
            UndoButtonIndex => englishToolbar ? "Undo" : "撤销",
            SaveButtonIndex => englishToolbar ? "Save" : "保存",
            CaptureRecordButtonIndex when recording is null => englishToolbar ? "Record" : "录屏",
            DesktopButtonIndex when recording is null => englishToolbar ? "Done" : "完成",
            CaptureRecordButtonIndex => englishToolbar ? "Finish" : "完成",
            DesktopButtonIndex => englishToolbar ? "Interact" : "操作桌面",
            RecordingStopButtonIndex => englishToolbar ? "Stop" : "停止",
            _ => ""
        };
    }

    private double ButtonWidth(int index) => index switch
    {
        CompactPaletteButtonIndex => 44,
        StrokeSizeButtonIndex => 48,
        4 or DesktopButtonIndex => 66,
        _ => englishToolbar ? 54 : 46
    };

    private double ToolbarWidth => Enumerable.Range(0, ToolbarButtonCount).Sum(ButtonWidth);

    private Rect ToolbarRect()
    {
        if (selection is null) return Rect.Empty;
        var selected = selection.Value;
        var monitor = MonitorForSelection(selected);
        var scale = monitor.Scale;
        var width = Math.Min(ToolbarWidth * scale,
            monitor.Bounds.Width - 16 * scale);
        var height = 42 * scale;
        var x = Math.Clamp(
            selected.Right - width,
            monitor.Bounds.Left + 8 * scale,
            monitor.Bounds.Right - width - 8 * scale);
        var below = selected.Bottom + 8 * scale;
        var y = below + height <= monitor.Bounds.Bottom - 8 * scale
            ? below
            : selected.Top - height - 8 * scale;
        y = Math.Clamp(
            y,
            monitor.Bounds.Top + 8 * scale,
            monitor.Bounds.Bottom - height - 8 * scale);
        return new Rect(x, y, width, height);
    }

    private Rect ToolbarButtonRect(int index)
    {
        var toolbar = ToolbarRect();
        var scale = toolbar.Width / ToolbarWidth;
        var x = toolbar.X;
        for (var current = 0; current < index; current++)
        {
            x += ButtonWidth(current) * scale;
        }
        return new Rect(x, toolbar.Y, ButtonWidth(index) * scale,
            toolbar.Height);
    }

    private Rect CompactQuickColorRect(int index)
    {
        var button = ToolbarButtonRect(CompactPaletteButtonIndex);
        var monitor = selection is null ? monitors[0] : MonitorForSelection(selection.Value);
        var scale = monitor.Scale;
        const double cellSize = 13;
        var column = index % 2;
        var row = index / 2;
        return new Rect(
            button.X + (4 + column * cellSize) * scale,
            button.Y + button.Height / 2 + (row == 0 ? 0 : -cellSize * scale),
            cellSize * scale,
            cellSize * scale);
    }

    private Rect CompactPaletteMoreRect()
    {
        var button = ToolbarButtonRect(CompactPaletteButtonIndex);
        var monitor = selection is null ? monitors[0] : MonitorForSelection(selection.Value);
        var scale = monitor.Scale;
        return new Rect(
            button.X + 33 * scale,
            button.Y + 4 * scale,
            button.Width - 36 * scale,
            button.Height - 8 * scale);
    }

    private Rect MoreColorPaletteRect()
    {
        var toolbar = ToolbarRect();
        var monitor = selection is null ? monitors[0] : MonitorForSelection(selection.Value);
        var scale = monitor.Scale;
        const int columns = 5;
        var rows = (additionalColors.Length + columns - 1) / columns;
        var width = (columns * 22 + 4) * scale;
        var height = (rows * 22 + 4) * scale;
        var button = ToolbarButtonRect(CompactPaletteButtonIndex);
        var x = Math.Clamp(
            button.X + button.Width / 2 - width / 2,
            monitor.Bounds.Left + 8 * scale,
            monitor.Bounds.Right - width - 8 * scale);
        var below = toolbar.Bottom + 6 * scale;
        var y = below + height <= monitor.Bounds.Bottom - 8 * scale
            ? below
            : toolbar.Top - height - 6 * scale;
        return new Rect(x, y, width, height);
    }

    private Rect MoreColorSwatchRect(int index)
    {
        var palette = MoreColorPaletteRect();
        var monitor = selection is null ? monitors[0] : MonitorForSelection(selection.Value);
        var scale = monitor.Scale;
        const int columns = 5;
        var column = index % columns;
        var row = index / columns;
        return new Rect(
            palette.X + (6 + column * 22) * scale,
            palette.Y + (6 + row * 22) * scale,
            14 * scale,
            14 * scale);
    }

    private void ToggleSizeInput()
    {
        if (recording is not null) return;
        if (sizeInputStage is not null)
        {
            CancelSizeInput();
        }
        else
        {
            sizeInputStage = SizeInputStage.Width;
            sizeInputBuffer = "";
            pendingInputWidth = null;
            sizeInputError = null;
        }
    }

    private void CancelSizeInput()
    {
        sizeInputStage = null;
        sizeInputBuffer = "";
        pendingInputWidth = null;
        sizeInputError = null;
    }

    private bool HandleSizeInputKey(Key key)
    {
        if (key == Key.Back)
        {
            if (string.IsNullOrEmpty(sizeInputBuffer)
                && sizeInputStage == SizeInputStage.Height
                && pendingInputWidth is not null)
            {
                sizeInputStage = SizeInputStage.Width;
                sizeInputBuffer = pendingInputWidth.Value.ToString(CultureInfo.InvariantCulture);
                pendingInputWidth = null;
            }
            else if (sizeInputBuffer.Length > 0)
            {
                sizeInputBuffer = sizeInputBuffer[..^1];
            }
            sizeInputError = null;
            Redraw();
            return true;
        }
        if (key == Key.Enter)
        {
            CommitSizeInput();
            return true;
        }

        var digit = key switch
        {
            >= Key.D0 and <= Key.D9 => (int)key - (int)Key.D0,
            >= Key.NumPad0 and <= Key.NumPad9 => (int)key - (int)Key.NumPad0,
            _ => -1
        };
        if (digit >= 0)
        {
            if (sizeInputBuffer.Length < 6)
            {
                sizeInputBuffer += ((int)digit).ToString(CultureInfo.InvariantCulture);
                sizeInputError = null;
                Redraw();
            }
            else
            {
                System.Media.SystemSounds.Beep.Play();
            }
        }
        return true;
    }

    private void CommitSizeInput()
    {
        if (!int.TryParse(sizeInputBuffer, out var value) || value <= 0)
        {
            sizeInputError = englishToolbar ? "Enter a number greater than zero" : "请输入大于 0 的数字";
            System.Media.SystemSounds.Beep.Play();
            Redraw();
            return;
        }
        if (sizeInputStage == SizeInputStage.Width)
        {
            if (value > virtualBounds.Width)
            {
                sizeInputError = englishToolbar ? $"Maximum width: {(int)virtualBounds.Width} px" : $"宽度最大 {(int)virtualBounds.Width} px";
                System.Media.SystemSounds.Beep.Play();
                Redraw();
                return;
            }
            pendingInputWidth = value;
            sizeInputStage = SizeInputStage.Height;
            sizeInputBuffer = "";
            sizeInputError = null;
            Redraw();
            return;
        }
        if (value > virtualBounds.Height)
        {
            sizeInputError = englishToolbar ? $"Maximum height: {(int)virtualBounds.Height} px" : $"高度最大 {(int)virtualBounds.Height} px";
            System.Media.SystemSounds.Beep.Play();
            Redraw();
            return;
        }
        if (pendingInputWidth is not null)
        {
            ApplySelectionSize(pendingInputWidth.Value, value);
        }
    }

    private void ApplySelectionSize(int width, int height)
    {
        if (selection is null)
        {
            CancelSizeInput();
            return;
        }
        var selected = selection.Value;
        var x = Math.Clamp(selected.X, virtualBounds.Left, virtualBounds.Right - width);
        var y = Math.Clamp(selected.Y, virtualBounds.Top, virtualBounds.Bottom - height);
        selection = new Rect(x, y, width, height);
        hoverCandidate = null;
        interaction = InteractionKind.None;
        CancelSizeInput();
        UpdateCursor(CursorPoint());
        Redraw();
    }

    private void Undo()
    {
        if (annotations.Count == 0) return;
        annotations.RemoveAt(annotations.Count - 1);
        Redraw();
    }

    private void Save()
    {
        if (recording is not null) return;
        using var output = CreateEditedBitmap();
        if (output is null) return;

        var dialog = new SaveFileDialog
        {
            Title = "保存截图",
            FileName = $"截图 {DateTime.Now:yyyy-MM-dd HH.mm.ss}.png",
            DefaultExt = ".png",
            AddExtension = true,
            Filter = "PNG 图片 (*.png)|*.png",
            OverwritePrompt = true
        };

        HideOverlays();
        bool? response;
        try
        {
            response = dialog.ShowDialog();
        }
        catch (Exception error)
        {
            MessageBox.Show(
                $"无法打开保存窗口：{error.Message}",
                "DeskMosaic",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            RestoreOverlays();
            return;
        }
        if (response != true)
        {
            RestoreOverlays();
            return;
        }
        try
        {
            output.Save(dialog.FileName, DrawingImaging.ImageFormat.Png);
            Close();
        }
        catch (Exception error)
        {
            MessageBox.Show(
                $"无法保存截图：{error.Message}",
                "DeskMosaic",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            RestoreOverlays();
        }
    }

    private void Finish()
    {
        if (recording is not null)
        {
            EndRecording();
            return;
        }
        using var output = CreateEditedBitmap();
        if (output is null) return;

        var bitmapSource = ToBitmapSource(output);
        Exception? clipboardError = null;
        for (var attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                Clipboard.Clear();
                Clipboard.SetImage(bitmapSource);
                clipboardError = null;
                break;
            }
            catch (Exception error)
            {
                clipboardError = error;
                Thread.Sleep(30);
            }
        }
        Close();
        if (clipboardError is not null)
        {
            MessageBox.Show(
                $"图片已生成，但写入剪贴板失败：{clipboardError.Message}",
                "DeskMosaic",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
    }

    private Drawing.Bitmap? CreateEditedBitmap()
    {
        if (selection is not { } selected || selected.Width < 2 || selected.Height < 2)
            return null;
        var x = Math.Clamp(
            (int)Math.Round(selected.X),
            (int)virtualBounds.Left,
            (int)virtualBounds.Right - 1);
        var y = Math.Clamp(
            (int)Math.Round(selected.Y),
            (int)virtualBounds.Top,
            (int)virtualBounds.Bottom - 1);
        var width = Math.Clamp(
            (int)Math.Round(selected.Width),
            1,
            (int)virtualBounds.Right - x);
        var height = Math.Clamp(
            (int)Math.Round(selected.Height),
            1,
            (int)virtualBounds.Bottom - y);

        var output = new Drawing.Bitmap(
            width,
            height,
            DrawingImaging.PixelFormat.Format32bppArgb);
        try
        {
            using var graphics = Drawing.Graphics.FromImage(output);
            graphics.SmoothingMode = Drawing2D.SmoothingMode.AntiAlias;
            graphics.DrawImage(
                DesktopBitmap,
                new Drawing.Rectangle(0, 0, width, height),
                new Drawing.Rectangle(
                    x - (int)virtualBounds.Left,
                    y - (int)virtualBounds.Top,
                    width,
                    height),
                Drawing.GraphicsUnit.Pixel);
            DrawAnnotationsToBitmap(graphics, x, y);
            return output;
        }
        catch
        {
            output.Dispose();
            throw;
        }
    }

    private void HideOverlays()
    {
        foreach (var window in windows)
        {
            window.Hide();
        }
    }

    private void RestoreOverlays()
    {
        foreach (var window in windows)
        {
            window.ShowActivated = false;
            window.Show();
        }
        var point = CursorPoint();
        var activeWindow = windows.FirstOrDefault(window => window.Monitor.Bounds.Contains(point))
            ?? windows.FirstOrDefault();
        activeWindow?.ActivateForInput();
        UpdateCursor(point);
        Redraw();
    }

    private void DrawAnnotationsToBitmap(Drawing.Graphics graphics, int originX, int originY,
        IReadOnlyList<Annotation>? snapshot = null, double? snapshotScale = null,
        Drawing.Bitmap? liveMosaic = null, IReadOnlyDictionary<TextAnnotation, BitmapSource>? renderedText = null)
    {
        var scale = snapshotScale ?? (selection is null ? 1 : MonitorForSelection(selection.Value).Scale);
        using var pen = new Drawing.Pen(Drawing.Color.Red, 1)
        {
            StartCap = Drawing2D.LineCap.Round,
            EndCap = Drawing2D.LineCap.Round,
            LineJoin = Drawing2D.LineJoin.Round
        };
        foreach (var annotation in snapshot ?? annotations) Draw(annotation);

        void Draw(Annotation annotation)
        {
            pen.Color = ToDrawingColor(annotation.Color);
            pen.Width = Math.Max(1, (float)(annotation.StrokeWidth * scale));
            switch (annotation)
            {
                case MosaicAnnotation mosaic:
                    DrawMosaicToBitmap(graphics, mosaic, originX, originY, liveMosaic);
                    break;
                case RectangleAnnotation rectangle:
                    var rect = Normalized(rectangle.Start, rectangle.End);
                    graphics.DrawRectangle(
                        pen,
                        (float)(rect.X - originX),
                        (float)(rect.Y - originY),
                        (float)rect.Width,
                        (float)rect.Height);
                    break;
                case ArrowAnnotation arrow:
                    var start = new Drawing.PointF(
                        (float)(arrow.Start.X - originX),
                        (float)(arrow.Start.Y - originY));
                    var end = new Drawing.PointF(
                        (float)(arrow.End.X - originX),
                        (float)(arrow.End.Y - originY));
                    graphics.DrawLine(pen, start, end);
                    var angle = Math.Atan2(end.Y - start.Y, end.X - start.X);
                    var arrowLength = (10 + annotation.StrokeWidth * 1.5) * scale;
                    graphics.DrawLine(
                        pen,
                        end,
                        new Drawing.PointF(
                            (float)(end.X - arrowLength * Math.Cos(angle - Math.PI / 6)),
                            (float)(end.Y - arrowLength * Math.Sin(angle - Math.PI / 6))));
                    graphics.DrawLine(
                        pen,
                        end,
                        new Drawing.PointF(
                            (float)(end.X - arrowLength * Math.Cos(angle + Math.PI / 6)),
                            (float)(end.Y - arrowLength * Math.Sin(angle + Math.PI / 6))));
                    break;
                case PenAnnotation freehand when freehand.Points.Count >= 2:
                    graphics.DrawLines(
                        pen,
                        freehand.Points.Select(point => new Drawing.PointF(
                            (float)(point.X - originX),
                            (float)(point.Y - originY))).ToArray());
                    break;
                case TextAnnotation text:
                    // Export the same WPF layout as the editor/overlay, including
                    // wrapping, font fallback and physical size on high-DPI screens.
                    var rendered = renderedText is null ? ScreenshotTextLayout.Render(text) : renderedText[text];
                    using (var bitmap = new Drawing.Bitmap(rendered.PixelWidth, rendered.PixelHeight,
                               DrawingImaging.PixelFormat.Format32bppPArgb))
                    {
                        var data = bitmap.LockBits(new Drawing.Rectangle(0, 0, bitmap.Width, bitmap.Height),
                            DrawingImaging.ImageLockMode.WriteOnly, DrawingImaging.PixelFormat.Format32bppPArgb);
                        try { rendered.CopyPixels(Int32Rect.Empty, data.Scan0, data.Stride * data.Height, data.Stride); }
                        finally { bitmap.UnlockBits(data); }
                        graphics.DrawImageUnscaled(bitmap,
                            (int)Math.Round(text.Bounds.Left - originX),
                            (int)Math.Round(text.Bounds.Top - originY));
                    }
                    break;
            }
        }
    }

    private void DrawMosaicToBitmap(
        Drawing.Graphics graphics,
        MosaicAnnotation mosaic,
        int originX,
        int originY,
        Drawing.Bitmap? liveMosaic)
    {
        if (mosaic.Points.Count == 0) return;
        var rect = Rect.Intersect(MosaicBounds(mosaic.Points, mosaic.BrushSize), virtualBounds);
        if (rect.IsEmpty || rect.Width < 1 || rect.Height < 1) return;
        var graphicsState = graphics.Save();
        using (var path = new Drawing2D.GraphicsPath())
        {
            if (mosaic.Points.Count == 1)
            {
                var point = mosaic.Points[0];
                path.AddEllipse(
                    (float)(point.X - originX - mosaic.BrushSize / 2),
                    (float)(point.Y - originY - mosaic.BrushSize / 2),
                    (float)mosaic.BrushSize,
                    (float)mosaic.BrushSize);
            }
            else
            {
                path.AddLines(mosaic.Points.Select(point => new Drawing.PointF(
                    (float)(point.X - originX),
                    (float)(point.Y - originY))).ToArray());
                using var brushPen = new Drawing.Pen(Drawing.Color.Black, (float)mosaic.BrushSize)
                {
                    StartCap = Drawing2D.LineCap.Round,
                    EndCap = Drawing2D.LineCap.Round,
                    LineJoin = Drawing2D.LineJoin.Round
                };
                path.Widen(brushPen);
            }
            graphics.SetClip(path, Drawing2D.CombineMode.Intersect);
        }
        var previousInterpolation = graphics.InterpolationMode;
        var previousPixelOffset = graphics.PixelOffsetMode;
        graphics.InterpolationMode = Drawing2D.InterpolationMode.NearestNeighbor;
        graphics.PixelOffsetMode = Drawing2D.PixelOffsetMode.Half;
        var mosaicBitmap = liveMosaic ?? DesktopMosaicBitmap;
        var destination = liveMosaic is null ? virtualBounds : recordingBounds;
        graphics.DrawImage(
            mosaicBitmap,
            new Drawing.Rectangle(
                (int)destination.Left - originX, (int)destination.Top - originY,
                (int)destination.Width, (int)destination.Height),
            new Drawing.Rectangle(0, 0, mosaicBitmap.Width, mosaicBitmap.Height),
            Drawing.GraphicsUnit.Pixel);
        graphics.InterpolationMode = previousInterpolation;
        graphics.PixelOffsetMode = previousPixelOffset;
        graphics.Restore(graphicsState);
    }

    private static BitmapSource ToBitmapSource(Drawing.Bitmap bitmap)
    {
        var handle = bitmap.GetHbitmap();
        try
        {
            var source = System.Windows.Interop.Imaging.CreateBitmapSourceFromHBitmap(
                handle,
                nint.Zero,
                Int32Rect.Empty,
                BitmapSizeOptions.FromEmptyOptions());
            source.Freeze();
            return source;
        }
        finally
        {
            NativeMethods.DeleteObject(handle);
        }
    }

    private static BitmapSource CreateMosaicSource(Drawing.Bitmap source)
    {
        using var mosaic = CreateMosaicBitmap(source);
        return ToBitmapSource(mosaic);
    }

    private static Drawing.Bitmap CreateMosaicBitmap(Drawing.Bitmap source)
    {
        const int blockSize = 10;
        var output = new Drawing.Bitmap(
            Math.Max(1, (int)Math.Ceiling(source.Width / (double)blockSize)),
            Math.Max(1, (int)Math.Ceiling(source.Height / (double)blockSize)),
            DrawingImaging.PixelFormat.Format32bppArgb);
        using var graphics = Drawing.Graphics.FromImage(output);
        graphics.CompositingMode = Drawing2D.CompositingMode.SourceCopy;
        graphics.InterpolationMode = Drawing2D.InterpolationMode.HighQualityBilinear;
        graphics.DrawImage(
            source,
            new Drawing.Rectangle(0, 0, output.Width, output.Height),
            new Drawing.Rectangle(0, 0, source.Width, source.Height),
            Drawing.GraphicsUnit.Pixel);
        return output;
    }

    private void StartRecording()
    {
        if (selection is not { Width: >= 2, Height: >= 2 } region) return;
        var settings = ConfigurationStore.Shared.Configuration.RegionRecording;
        var x = (int)Math.Round(region.X);
        var y = (int)Math.Round(region.Y);
        var width = Math.Max(2, (int)Math.Round(region.Right) - x);
        var height = Math.Max(2, (int)Math.Round(region.Bottom) - y);
        try
        {
            recordingBounds = new Rect(x, y, width, height);
            recordingDesktop = new Drawing.Bitmap(width, height, DrawingImaging.PixelFormat.Format32bppRgb);
            var capture = new RegionRecording(settings, width, height);
            recording = capture;
            _ = ObserveRecordingAsync(capture);
            if (capture.Width != width || capture.Height != height)
                recordingOutput = new Drawing.Bitmap(capture.Width, capture.Height, DrawingImaging.PixelFormat.Format32bppRgb);
            interaction = InteractionKind.None;
            activeTool = null;
            showsMoreColors = false;
            activePicker = null;
            foreach (var window in windows) window.MakeRecordingVisual();
            onRecordingStarted();
            recordingTimer = new DispatcherTimer(DispatcherPriority.Background)
            {
                Interval = TimeSpan.FromSeconds(1d / settings.FrameRate)
            };
            recordingTimer.Tick += (_, _) => RecordFrame();
            recordingTimer.Start();
            Redraw();
            RecordFrame();
        }
        catch (Exception error)
        {
            if (recording is not null) EndRecording();
            else
            {
                recordingDesktop?.Dispose();
                recordingDesktop = null;
            }
            ShowRecordingError(error.Message);
        }
    }

    private void RecordFrame()
    {
        if (recording is not { } capture || recordingDesktop is not { } desktop
            || !capture.TryReserveFrame()) return;
        try
        {
            var snapshot = new List<Annotation>(annotations);
            if (previewAnnotation is not null) snapshot.Add(previewAnnotation);
            if (textInputOrigin is not null && !string.IsNullOrWhiteSpace(textInputBuffer)
                && textInputBounds is { Width: > 0, Height: > 0 })
                snapshot.Add(new TextAnnotation(textInputBuffer, textInputBounds, selectedColor,
                    textInputFontSize, selectedFontWeight));

            // Render WPF text on the UI thread only when it changes. The frozen
            // bitmaps can then be composited with desktop frames on a worker.
            var text = snapshot.OfType<TextAnnotation>().Distinct().ToDictionary(annotation => annotation,
                annotation => recordingText.TryGetValue(annotation, out var cached)
                    ? cached : ScreenshotTextLayout.Render(annotation));
            recordingText = text;
            var output = recordingOutput ?? desktop;
            var scale = MonitorForSelection(selection!.Value).Scale;
            var task = Task.Run(() => CaptureRecordingFrame(capture, desktop, output, snapshot, scale, text));
            recordingFrame = task;
            _ = ShowRecordingFrameAsync(task);
        }
        catch (Exception error)
        {
            capture.ReleaseFrame();
            EndRecording();
            ShowRecordingError(error.Message);
        }
    }

    private BitmapSource? CaptureRecordingFrame(RegionRecording capture, Drawing.Bitmap desktop, Drawing.Bitmap output,
        IReadOnlyList<Annotation> snapshot, double scale, IReadOnlyDictionary<TextAnnotation, BitmapSource> text)
    {
        try
        {
            var x = (int)recordingBounds.X;
            var y = (int)recordingBounds.Y;
            BitmapSource? mosaicPreview = null;
            using (var graphics = Drawing.Graphics.FromImage(desktop))
            {
                if (!monitors.Any(monitor => monitor.Bounds.Contains(recordingBounds)))
                    graphics.Clear(Drawing.Color.Black);
                foreach (var monitor in monitors)
                {
                    var visible = Rect.Intersect(recordingBounds, monitor.Bounds);
                    if (visible.IsEmpty || visible.Width <= 0 || visible.Height <= 0) continue;
                    graphics.CopyFromScreen((int)visible.X, (int)visible.Y, (int)visible.X - x,
                        (int)visible.Y - y, new Drawing.Size((int)visible.Width, (int)visible.Height),
                        Drawing.CopyPixelOperation.SourceCopy);
                }
                using var mosaic = snapshot.Any(annotation => annotation is MosaicAnnotation)
                    ? CreateMosaicBitmap(desktop) : null;
                if (mosaic is not null) mosaicPreview = ToBitmapSource(mosaic);
                graphics.SmoothingMode = Drawing2D.SmoothingMode.AntiAlias;
                DrawAnnotationsToBitmap(graphics, x, y, snapshot, scale, mosaic, text);
            }

            if (!ReferenceEquals(output, desktop))
            {
                using var graphics = Drawing.Graphics.FromImage(output);
                graphics.CompositingMode = Drawing2D.CompositingMode.SourceCopy;
                graphics.InterpolationMode = Drawing2D.InterpolationMode.HighQualityBicubic;
                graphics.PixelOffsetMode = Drawing2D.PixelOffsetMode.Half;
                graphics.DrawImage(desktop, 0, 0, output.Width, output.Height);
            }
            var data = output.LockBits(new Drawing.Rectangle(0, 0, output.Width, output.Height),
                DrawingImaging.ImageLockMode.ReadOnly, DrawingImaging.PixelFormat.Format32bppRgb);
            try
            {
                var rowSize = output.Width * 4;
                if (data.Stride == rowSize)
                    Marshal.Copy(data.Scan0, capture.Frame, 0, capture.Frame.Length);
                else
                    for (var row = 0; row < output.Height; row++)
                        Marshal.Copy(data.Scan0 + row * data.Stride, capture.Frame, row * rowSize, rowSize);
            }
            finally { output.UnlockBits(data); }
            capture.SubmitFrame();
            return mosaicPreview;
        }
        catch
        {
            capture.ReleaseFrame();
            throw;
        }
    }

    private async Task ShowRecordingFrameAsync(Task<BitmapSource?> frame)
    {
        try
        {
            var mosaic = await frame;
            if (isClosed || frame != recordingFrame || mosaic is null && recordingMosaic is null) return;
            recordingMosaic = mosaic;
            foreach (var window in windows) window.Surface.InvalidateVisual();
        }
        catch (Exception error)
        {
            if (isClosed) return;
            EndRecording();
            ShowRecordingError(error.Message);
        }
    }

    private async Task StopRecordingAsync(RegionRecording capture)
    {
        try
        {
            if (recordingFrame is { } pending)
            {
                try { await pending; }
                catch { /* The recording observer reports frame errors. */ }
            }
            await capture.StopAsync();
        }
        finally
        {
            recordingDesktop?.Dispose();
            recordingOutput?.Dispose();
        }
    }

    private void EndRecording() => Close();

    private async Task ObserveRecordingAsync(RegionRecording capture)
    {
        try
        {
            var path = await capture.Completion;
            new RecordingSavedWindow(path).ShowDialog();
        }
        catch (Exception error)
        {
            if (!isClosed) Close();
            ShowRecordingError(error.Message);
        }
    }

    private static void ShowRecordingError(string message)
    {
        MessageBox.Show(message, "区域录屏失败", MessageBoxButton.OK, MessageBoxImage.Error);
    }

    private void Close()
    {
        if (isClosed) return;
        isClosed = true;
        recordingTimer?.Stop();
        recordingTimer = null;
        if (recording is { } capture) _ = StopRecordingAsync(capture);
        recording = null;
        foreach (var window in windows.Concat(recordingInputs.Values))
        {
            window.Surface.Cursor = Cursors.Arrow;
            window.Close();
        }
        recordingInputs.Clear();
        windows.Clear();
        foreach (var cursor in brushCursors.Values) cursor.Dispose();
        brushCursors.Clear();
        foreach (var monitor in monitors)
        {
            monitor.Dispose();
        }
        desktopBitmap?.Dispose();
        desktopMosaicBitmap?.Dispose();
        onClosed();
    }

    internal bool RecordingInputShouldActivate()
    {
        var foreground = NativeMethods.GetForegroundWindow();
        NativeMethods.GetWindowThreadProcessId(foreground, out var process);
        if (foreground != nint.Zero && process != (uint)Environment.ProcessId)
            recordingForegroundWindow = foreground;

        var point = CursorPoint();
        return !ToolbarButtonRect(DesktopButtonIndex).Contains(point)
            && !ToolbarButtonRect(RecordingStopButtonIndex).Contains(point);
    }

    private void UpdateRecordingInputs()
    {
        if (isClosed || recording is null || selection is not { } selected) return;

        var regions = new HashSet<(CapturedMonitor, Rect)>();
        if (activeTool is not null)
        {
            foreach (var monitor in monitors)
            {
                var region = Rect.Intersect(selected, monitor.Bounds);
                if (region is { Width: > 0, Height: > 0 }) regions.Add((monitor, region));
            }
        }

        var toolbar = ToolbarRect();
        var toolbarKey = (MonitorForSelection(selected), toolbar);
        regions.Add(toolbarKey);
        (CapturedMonitor, Rect)? paletteKey = null;
        if (showsMoreColors)
        {
            var palette = MoreColorPaletteRect();
            paletteKey = (PickerMonitor(palette), palette);
            regions.Add(paletteKey.Value);
        }
        // The press-and-drag picker already has mouse capture on its toolbar window.

        foreach (var key in recordingInputs.Keys.Where(key => !regions.Contains(key)).ToArray())
        {
            recordingInputs[key].Close();
            recordingInputs.Remove(key);
        }
        var addedInput = false;
        foreach (var (monitor, region) in regions)
        {
            if (recordingInputs.ContainsKey((monitor, region))) continue;
            var input = new OverlayWindow(this, monitor, region);
            recordingInputs.Add((monitor, region), input);
            input.Show();
            addedInput = true;
        }

        if (!addedInput) return;
        // The toolbar can overlap a full-display recording. Keep it above the
        // drawing surface so turning off tools never closes the clicked window.
        recordingInputs[toolbarKey].RaiseRecordingControl();
        if (paletteKey is { } paletteControl)
            recordingInputs[paletteControl].RaiseRecordingControl();
    }

    private void Redraw()
    {
        UpdateRecordingInputs();
        foreach (var window in windows)
        {
            window.Surface.InvalidateVisual();
        }
    }

    private void UpdateCursor(Point point)
    {
        IEnumerable<OverlayWindow> cursorWindows = recording is null ? windows : recordingInputs.Values.Reverse();
        var window = cursorWindows.FirstOrDefault(candidate => candidate.Surface.DesktopBounds.Contains(point));
        if (window is null) return;
        var cursor = Cursors.Cross;
        if (interaction == InteractionKind.Pen && selection?.Contains(point) == true)
        {
            cursor = BrushCursor(window);
        }
        else if (interaction == InteractionKind.Moving)
        {
            cursor = Cursors.SizeAll;
        }
        else if (interaction == InteractionKind.MovingAnnotation)
        {
            cursor = Cursors.Hand;
        }
        else if (interaction == InteractionKind.Resizing)
        {
            cursor = CursorForHandle(resizeHandle);
        }
        else if (activePicker is not null || showsMoreColors && MoreColorPaletteRect().Contains(point)
                 || selection is not null && ToolbarRect().Contains(point))
        {
            cursor = Cursors.Hand;
        }
        else if (selection is { } selected)
        {
            var handle = recording is null ? HitHandle(point, selected) : null;
            if (handle is not null)
            {
                cursor = CursorForHandle(handle.Value);
            }
            else if (AnnotationIndexAt(point) >= 0)
            {
                cursor = Cursors.Hand;
            }
            else if (recording is null && activeTool is null && selected.Contains(point))
            {
                cursor = Cursors.SizeAll;
            }
            else if (activeTool == ScreenshotTool.Text && selected.Contains(point))
            {
                cursor = Cursors.IBeam;
            }
            else if (activeTool is ScreenshotTool.Pen or ScreenshotTool.Mosaic && selected.Contains(point))
            {
                cursor = BrushCursor(window);
            }
        }
        window.Surface.Cursor = cursor;
    }

    private Cursor BrushCursor(OverlayWindow window)
    {
        // Pen widths are WPF DIPs; mosaic diameters already use desktop pixels.
        // Disable Cursor's default-size/DPI rescaling so its ring matches them.
        var diameter = activeTool == ScreenshotTool.Mosaic
            ? selectedMosaicBrushSize
            : selectedStrokeWidth * window.Monitor.Scale;
        if (!brushCursors.TryGetValue(diameter, out var cursor))
        {
            using var stream = new System.IO.MemoryStream(BrushCursorMask.Create(diameter));
            cursor = new Cursor(stream, false);
            brushCursors.Add(diameter, cursor);
        }
        return cursor;
    }

    private ResizeHandle? HitHandle(Point point, Rect selected)
    {
        var scale = MonitorForSelection(selected).Scale;
        var hitSize = 16 * scale;
        foreach (var handle in Enum.GetValues<ResizeHandle>())
        {
            var center = HandlePoint(handle, selected);
            if (new Rect(
                    center.X - hitSize / 2,
                    center.Y - hitSize / 2,
                    hitSize,
                    hitSize).Contains(point))
            {
                return handle;
            }
        }
        return null;
    }

    private static Point HandlePoint(ResizeHandle handle, Rect rect)
    {
        return handle switch
        {
            ResizeHandle.TopLeft => rect.TopLeft,
            ResizeHandle.Top => new Point(rect.X + rect.Width / 2, rect.Top),
            ResizeHandle.TopRight => rect.TopRight,
            ResizeHandle.Right => new Point(rect.Right, rect.Y + rect.Height / 2),
            ResizeHandle.BottomRight => rect.BottomRight,
            ResizeHandle.Bottom => new Point(rect.X + rect.Width / 2, rect.Bottom),
            ResizeHandle.BottomLeft => rect.BottomLeft,
            ResizeHandle.Left => new Point(rect.Left, rect.Y + rect.Height / 2),
            _ => rect.TopLeft
        };
    }

    private static Cursor CursorForHandle(ResizeHandle handle)
    {
        return handle switch
        {
            ResizeHandle.TopLeft or ResizeHandle.BottomRight => Cursors.SizeNWSE,
            ResizeHandle.TopRight or ResizeHandle.BottomLeft => Cursors.SizeNESW,
            ResizeHandle.Top or ResizeHandle.Bottom => Cursors.SizeNS,
            _ => Cursors.SizeWE
        };
    }

    private CapturedMonitor MonitorForSelection(Rect selected)
    {
        return monitors.MaxBy(monitor => Area(Rect.Intersect(monitor.Bounds, selected)))
            ?? monitors[0];
    }

    private CapturedMonitor MonitorContainingTopLeft(Rect selected)
    {
        var point = new Point(selected.Left + 1, selected.Top + 1);
        return monitors.FirstOrDefault(monitor => monitor.Bounds.Contains(point))
            ?? MonitorForSelection(selected);
    }

    private static double Area(Rect rect)
    {
        return rect.IsEmpty ? 0 : Math.Max(0, rect.Width) * Math.Max(0, rect.Height);
    }

    private Rect IntersectVirtual(Rect rect)
    {
        return Rect.Intersect(rect, virtualBounds);
    }

    private Rect ClampMovedSelection(Rect rect)
    {
        var x = Math.Clamp(rect.X, virtualBounds.Left, virtualBounds.Right - rect.Width);
        var y = Math.Clamp(rect.Y, virtualBounds.Top, virtualBounds.Bottom - rect.Height);
        return new Rect(x, y, rect.Width, rect.Height);
    }

    private static Rect ResizeSelection(
        Rect rect,
        ResizeHandle handle,
        double deltaX,
        double deltaY)
    {
        var left = rect.Left;
        var right = rect.Right;
        var top = rect.Top;
        var bottom = rect.Bottom;
        if (handle is ResizeHandle.TopLeft or ResizeHandle.Left or ResizeHandle.BottomLeft)
            left += deltaX;
        if (handle is ResizeHandle.TopRight or ResizeHandle.Right or ResizeHandle.BottomRight)
            right += deltaX;
        if (handle is ResizeHandle.TopLeft or ResizeHandle.Top or ResizeHandle.TopRight)
            top += deltaY;
        if (handle is ResizeHandle.BottomLeft or ResizeHandle.Bottom or ResizeHandle.BottomRight)
            bottom += deltaY;
        return Normalized(new Point(left, top), new Point(right, bottom));
    }

    private static Rect Normalized(Point first, Point second)
    {
        return new Rect(
            Math.Min(first.X, second.X),
            Math.Min(first.Y, second.Y),
            Math.Abs(second.X - first.X),
            Math.Abs(second.Y - first.Y));
    }

    private static Rect MosaicBounds(IReadOnlyList<Point> points, double brushSize)
    {
        if (points.Count == 0) return Rect.Empty;
        var left = points.Min(point => point.X) - brushSize / 2;
        var top = points.Min(point => point.Y) - brushSize / 2;
        var right = points.Max(point => point.X) + brushSize / 2;
        var bottom = points.Max(point => point.Y) + brushSize / 2;
        return new Rect(left, top, right - left, bottom - top);
    }

    private static Point ClampPoint(Point point, Rect rect)
    {
        return new Point(
            Math.Clamp(point.X, rect.Left, rect.Right),
            Math.Clamp(point.Y, rect.Top, rect.Bottom));
    }

    private static double Distance(Point first, Point second)
    {
        return Math.Sqrt(
            Math.Pow(first.X - second.X, 2)
            + Math.Pow(first.Y - second.Y, 2));
    }

    private static Drawing.Color ToDrawingColor(Color color)
    {
        return Drawing.Color.FromArgb(color.A, color.R, color.G, color.B);
    }

    private static Color ParseColor(string value)
    {
        try { return (Color)ColorConverter.ConvertFromString(value); }
        catch { return Color.FromRgb(255, 0, 0); }
    }

    private static FontWeight ParseFontWeight(string value) => value switch
    {
        "Regular" => FontWeights.Regular,
        "SemiBold" => FontWeights.SemiBold,
        "Bold" => FontWeights.Bold,
        _ => FontWeights.Medium
    };

    private static Point CursorPoint()
    {
        NativeMethods.GetCursorPos(out var point);
        return new Point(point.X, point.Y);
    }

    private static FormattedText CreateText(
        string text,
        double size,
        Brush brush,
        FontWeight weight,
        Visual visual,
        string fontFamily = "Segoe UI")
    {
        return new FormattedText(
            text,
            CultureInfo.CurrentUICulture,
            FlowDirection.LeftToRight,
            new Typeface(new FontFamily(fontFamily), FontStyles.Normal, weight, FontStretches.Normal),
            size,
            brush,
            VisualTreeHelper.GetDpi(visual).PixelsPerDip);
    }

    private static void DrawCenteredText(
        OverlaySurface surface,
        DrawingContext drawingContext,
        string text,
        Rect rect,
        Brush brush,
        double size = 12)
    {
        var formatted = CreateText(text, size, brush, FontWeights.Medium, surface);
        drawingContext.DrawText(
            formatted,
            new Point(
                rect.X + (rect.Width - formatted.Width) / 2,
                rect.Y + (rect.Height - formatted.Height) / 2));
    }
}

internal static class RectExtensions
{
    internal static Rect InflateCopy(this Rect rect, double amount)
    {
        rect.Inflate(amount, amount);
        return rect;
    }
}
