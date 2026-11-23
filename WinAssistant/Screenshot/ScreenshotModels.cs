using System.Windows;
using System.Windows.Media;

namespace WinAssistant.Screenshot;

internal enum ScreenshotTool
{
    Rectangle,
    Arrow,
    Pen,
    Text,
    Mosaic
}

internal enum ResizeHandle
{
    TopLeft,
    Top,
    TopRight,
    Right,
    BottomRight,
    Bottom,
    BottomLeft,
    Left
}

internal enum InteractionKind
{
    None,
    Candidate,
    Selecting,
    Moving,
    Resizing,
    Annotating,
    Pen,
    MovingAnnotation
}

internal enum SizeInputStage
{
    Width,
    Height
}

internal abstract record Annotation(Color Color, double StrokeWidth);

internal sealed record RectangleAnnotation(Point Start, Point End, Color Stroke, double Width)
    : Annotation(Stroke, Width);

internal sealed record ArrowAnnotation(Point Start, Point End, Color Stroke, double Width)
    : Annotation(Stroke, Width);

internal sealed record PenAnnotation(IReadOnlyList<Point> Points, Color Stroke, double Width)
    : Annotation(Stroke, Width);

// Bounds and font size are physical desktop pixels, fixed at text creation.
internal sealed record TextAnnotation(string Text, Rect Bounds, Color Foreground, double FontSize, FontWeight FontWeight)
    : Annotation(Foreground, 0);

internal sealed record MosaicAnnotation(IReadOnlyList<Point> Points, double BrushSize)
    : Annotation(Colors.Transparent, 0);
