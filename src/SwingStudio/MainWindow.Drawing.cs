using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using SwingStudio.Session;

namespace SwingStudio;

public partial class MainWindow
{
    private const string DrawYellow = "#FFE08A";

    private string? _drawTool;
    private bool _drawOnA;
    private string _colorA = DrawYellow;
    private string _colorB = DrawYellow;
    private readonly List<SwingStroke> _drawings = [];
    private readonly List<double> _anglePoints = [];
    private bool _dragging;
    private double _dragX;
    private double _dragY;
    private bool _hasPreview;
    private double _previewX;
    private double _previewY;

    private void DrawColor_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button || button.Tag is not string color)
        {
            return;
        }

        var isA = IsCameraA(button);
        if (isA)
        {
            _colorA = color;
        }
        else
        {
            _colorB = color;
        }

        UpdateDrawButtons();
    }

    private void DrawTool_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button || button.Tag is not string tool || !HasTake())
        {
            return;
        }

        var isA = button.Name.StartsWith("CameraA", StringComparison.Ordinal);
        if (_drawTool == tool && _drawOnA == isA)
        {
            ExitDrawMode();
            return;
        }

        _drawTool = tool;
        _drawOnA = isA;
        _anglePoints.Clear();
        _dragging = false;
        _hasPreview = false;
        CameraADraw.IsHitTestVisible = isA;
        CameraBDraw.IsHitTestVisible = !isA;
        if (_playing)
        {
            PausePlayback();
        }
        else if (!_showingTake)
        {
            PauseAtStart();
        }

        UpdateDrawButtons();
    }

    private void DrawUndo_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button)
        {
            return;
        }

        var camera = IsCameraA(button) ? "A" : "B";
        var index = _drawings.FindLastIndex(stroke => stroke.Camera == camera);
        if (index < 0)
        {
            return;
        }

        _drawings.RemoveAt(index);
        PersistDrawings();
        RedrawDrawings();
        UpdateDrawButtons();
    }

    private void Draw_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not Canvas canvas || !IsActiveCanvas(canvas))
        {
            return;
        }

        var image = ImageFor(canvas);
        if (!TryNormalize(image, canvas, e.GetPosition(canvas), out var nx, out var ny, clamp: false))
        {
            return;
        }

        if (_drawTool == "angle")
        {
            _anglePoints.Add(nx);
            _anglePoints.Add(ny);
            if (_anglePoints.Count >= 6)
            {
                Commit(new SwingStroke
                {
                    Camera = _drawOnA ? "A" : "B",
                    Tool = "angle",
                    Color = ActiveColor(),
                    SessionMs = DisplayedSessionTime(),
                    Points = [.. _anglePoints]
                });
                _anglePoints.Clear();
            }

            RedrawDrawings();
            return;
        }

        _dragging = true;
        _dragX = nx;
        _dragY = ny;
        _previewX = nx;
        _previewY = ny;
        _hasPreview = true;
        canvas.CaptureMouse();
        RedrawDrawings();
    }

    private void Draw_MouseMove(object sender, MouseEventArgs e)
    {
        if (!_dragging || sender is not Canvas canvas || !IsActiveCanvas(canvas))
        {
            return;
        }

        if (!TryNormalize(ImageFor(canvas), canvas, e.GetPosition(canvas), out _previewX, out _previewY, clamp: true))
        {
            return;
        }

        _hasPreview = true;
        RedrawDrawings();
    }

    private void Draw_MouseUp(object sender, MouseButtonEventArgs e)
    {
        if (!_dragging || sender is not Canvas canvas || !IsActiveCanvas(canvas))
        {
            return;
        }

        TryNormalize(ImageFor(canvas), canvas, e.GetPosition(canvas), out _previewX, out _previewY, clamp: true);
        canvas.ReleaseMouseCapture();
        _dragging = false;
        var image = ImageFor(canvas);
        if (!TryImageRect(image, canvas, out _, out _, out var width, out var height))
        {
            _hasPreview = false;
            RedrawDrawings();
            return;
        }

        var start = ToCanvas(image, canvas, _dragX, _dragY);
        var end = ToCanvas(image, canvas, _previewX, _previewY);
        var distance = Distance(end, start);
        if (distance >= 4 && _drawTool is "line" or "circle")
        {
            var points = _drawTool == "line"
                ? new List<double> { _dragX, _dragY, _previewX, _previewY }
                : new List<double> { _dragX, _dragY, distance / width };
            Commit(new SwingStroke
            {
                Camera = _drawOnA ? "A" : "B",
                Tool = _drawTool,
                Color = ActiveColor(),
                SessionMs = DisplayedSessionTime(),
                Points = points
            });
        }

        _hasPreview = false;
        RedrawDrawings();
    }

    private void ExitDrawMode()
    {
        _drawTool = null;
        _anglePoints.Clear();
        _dragging = false;
        _hasPreview = false;
        CameraADraw.ReleaseMouseCapture();
        CameraBDraw.ReleaseMouseCapture();
        CameraADraw.IsHitTestVisible = false;
        CameraBDraw.IsHitTestVisible = false;
        UpdateDrawButtons();
    }

    private void LoadDrawings()
    {
        _drawings.Clear();
        _anglePoints.Clear();
        if (!string.IsNullOrEmpty(_loadedFolder) && SwingCatalog.ReadSession(_loadedFolder)?.Drawings is { } drawings)
        {
            _drawings.AddRange(drawings);
        }

        RedrawDrawings();
    }

    private void PersistDrawings()
    {
        if (string.IsNullOrEmpty(_loadedFolder))
        {
            return;
        }

        try
        {
            SwingCatalog.SaveDrawings(_loadedFolder, _drawings);
        }
        catch (Exception ex)
        {
            StatusDetail.Text = ex.Message;
        }
    }

    private void Commit(SwingStroke stroke)
    {
        _drawings.Add(stroke);
        PersistDrawings();
        UpdateDrawButtons();
    }

    private void RedrawDrawings()
    {
        if (_closing)
        {
            return;
        }

        DrawMarks(CameraADraw, CameraAImage, "A");
        DrawMarks(CameraBDraw, CameraBImage, "B");
    }

    private void DrawMarks(Canvas canvas, Image image, string camera)
    {
        canvas.Children.Clear();
        if (!_showingTake || !TryImageRect(image, canvas, out _, out _, out _, out _))
        {
            return;
        }

        var time = DisplayedSessionTime();
        foreach (var stroke in _drawings)
        {
            if (stroke.Camera == camera && stroke.SessionMs <= time + 0.05)
            {
                AddStroke(canvas, image, stroke);
            }
        }

        if (_drawTool is not null && (_drawOnA ? "A" : "B") == camera && PreviewStroke() is { } preview)
        {
            AddStroke(canvas, image, preview);
        }
    }

    private SwingStroke? PreviewStroke()
    {
        var camera = _drawOnA ? "A" : "B";
        if (_drawTool == "angle" && _anglePoints.Count >= 2)
        {
            return new SwingStroke
            {
                Camera = camera,
                Tool = "angle",
                Color = ActiveColor(),
                Points = [.. _anglePoints]
            };
        }

        if (!_dragging || !_hasPreview || _drawTool is not ("line" or "circle"))
        {
            return null;
        }

        if (!TryImageRect(ImageFor(_drawOnA), CanvasFor(_drawOnA), out _, out _, out var width, out _))
        {
            return null;
        }

        var start = ToCanvas(ImageFor(_drawOnA), CanvasFor(_drawOnA), _dragX, _dragY);
        var end = ToCanvas(ImageFor(_drawOnA), CanvasFor(_drawOnA), _previewX, _previewY);
        var points = _drawTool == "line"
            ? new List<double> { _dragX, _dragY, _previewX, _previewY }
            : new List<double> { _dragX, _dragY, Distance(end, start) / width };
        return new SwingStroke { Camera = camera, Tool = _drawTool, Color = ActiveColor(), Points = points };
    }

    private void AddStroke(Canvas canvas, Image image, SwingStroke stroke)
    {
        var brush = BrushFor(stroke.Color);
        if (stroke.Tool == "line" && stroke.Points.Count >= 4)
        {
            var start = ToCanvas(image, canvas, stroke.Points[0], stroke.Points[1]);
            var end = ToCanvas(image, canvas, stroke.Points[2], stroke.Points[3]);
            canvas.Children.Add(new Line
            {
                X1 = start.X,
                Y1 = start.Y,
                X2 = end.X,
                Y2 = end.Y,
                Stroke = brush,
                StrokeThickness = 2.5
            });
            return;
        }

        if (stroke.Tool == "circle" && stroke.Points.Count >= 3 && TryImageRect(image, canvas, out _, out _, out var width, out _))
        {
            var center = ToCanvas(image, canvas, stroke.Points[0], stroke.Points[1]);
            var radius = Math.Max(1, stroke.Points[2] * width);
            var ellipse = new Ellipse
            {
                Width = radius * 2,
                Height = radius * 2,
                Stroke = brush,
                StrokeThickness = 2.5
            };
            Canvas.SetLeft(ellipse, center.X - radius);
            Canvas.SetTop(ellipse, center.Y - radius);
            canvas.Children.Add(ellipse);
            return;
        }

        if (stroke.Tool != "angle" || stroke.Points.Count < 2)
        {
            return;
        }

        var vertex = ToCanvas(image, canvas, stroke.Points[0], stroke.Points[1]);
        Point first = default;
        if (stroke.Points.Count >= 4)
        {
            first = ToCanvas(image, canvas, stroke.Points[2], stroke.Points[3]);
            canvas.Children.Add(Segment(vertex, first, brush));
        }

        if (stroke.Points.Count >= 6)
        {
            var second = ToCanvas(image, canvas, stroke.Points[4], stroke.Points[5]);
            canvas.Children.Add(Segment(vertex, second, brush));
            var degrees = SmallerAngle(vertex, first, second);
            var label = new TextBlock
            {
                Text = degrees.ToString("0", System.Globalization.CultureInfo.InvariantCulture) + "°",
                Foreground = brush,
                FontSize = 14,
                FontWeight = FontWeights.SemiBold
            };
            Canvas.SetLeft(label, vertex.X + 8);
            Canvas.SetTop(label, vertex.Y - 22);
            canvas.Children.Add(label);
        }
    }

    private static Line Segment(Point start, Point end, Brush brush) => new()
    {
        X1 = start.X,
        Y1 = start.Y,
        X2 = end.X,
        Y2 = end.Y,
        Stroke = brush,
        StrokeThickness = 2.5
    };

    private void UpdateDrawButtons()
    {
        if (_closing)
        {
            return;
        }

        var hasTake = HasTake();
        foreach (var button in new[] { CameraACircle, CameraALine, CameraAAngle, CameraAUndo, CameraBCircle, CameraBLine, CameraBAngle, CameraBUndo })
        {
            button.IsEnabled = hasTake;
        }

        foreach (Button swatch in CameraAColors.Children)
        {
            swatch.IsEnabled = hasTake;
        }

        foreach (Button swatch in CameraBColors.Children)
        {
            swatch.IsEnabled = hasTake;
        }

        Highlight(CameraACircle, _drawTool == "circle" && _drawOnA);
        Highlight(CameraALine, _drawTool == "line" && _drawOnA);
        Highlight(CameraAAngle, _drawTool == "angle" && _drawOnA);
        Highlight(CameraBCircle, _drawTool == "circle" && !_drawOnA && _drawTool is not null);
        Highlight(CameraBLine, _drawTool == "line" && !_drawOnA);
        Highlight(CameraBAngle, _drawTool == "angle" && !_drawOnA);
        MarkSwatches(CameraAColors, _colorA);
        MarkSwatches(CameraBColors, _colorB);
        CameraAUndo.IsEnabled = hasTake && _drawings.Exists(stroke => stroke.Camera == "A");
        CameraBUndo.IsEnabled = hasTake && _drawings.Exists(stroke => stroke.Camera == "B");
    }

    private static void Highlight(Button button, bool selected)
    {
        button.Background = selected ? new SolidColorBrush(Color.FromRgb(58, 58, 58)) : Brushes.Transparent;
    }

    private static void MarkSwatches(StackPanel panel, string color)
    {
        foreach (Button swatch in panel.Children)
        {
            swatch.BorderBrush = string.Equals(swatch.Tag as string, color, StringComparison.OrdinalIgnoreCase)
                ? Brushes.White
                : Brushes.Transparent;
        }
    }

    private string ActiveColor() => _drawOnA ? _colorA : _colorB;

    private bool IsActiveCanvas(Canvas canvas) =>
        _drawTool is not null && (canvas == CameraADraw) == _drawOnA;

    private Image ImageFor(Canvas canvas) => canvas == CameraADraw ? CameraAImage : CameraBImage;

    private Image ImageFor(bool isA) => isA ? CameraAImage : CameraBImage;

    private Canvas CanvasFor(bool isA) => isA ? CameraADraw : CameraBDraw;

    private static bool IsCameraA(Button button) =>
        button.Parent is StackPanel panel && (panel.Name == "CameraAColors" || panel.Name == "CameraATools" || panel.Parent is StackPanel parent && parent.Name == "CameraATools");

    private static bool TryImageRect(Image image, Canvas canvas, out double x, out double y, out double width, out double height)
    {
        x = y = width = height = 0;
        if (image.Source is not BitmapSource bitmap || image.ActualWidth <= 1 || image.ActualHeight <= 1 || bitmap.PixelWidth <= 0 || bitmap.PixelHeight <= 0)
        {
            return false;
        }

        var origin = image.TranslatePoint(new Point(0, 0), canvas);
        var scale = Math.Min(image.ActualWidth / bitmap.PixelWidth, image.ActualHeight / bitmap.PixelHeight);
        width = bitmap.PixelWidth * scale;
        height = bitmap.PixelHeight * scale;
        x = origin.X + (image.ActualWidth - width) / 2;
        y = origin.Y + (image.ActualHeight - height) / 2;
        return width > 1 && height > 1;
    }

    private static bool TryNormalize(Image image, Canvas canvas, Point mouse, out double nx, out double ny, bool clamp)
    {
        nx = ny = 0;
        if (!TryImageRect(image, canvas, out var x, out var y, out var width, out var height))
        {
            return false;
        }

        nx = (mouse.X - x) / width;
        ny = (mouse.Y - y) / height;
        if (!clamp && (nx is < 0 or > 1 || ny is < 0 or > 1))
        {
            return false;
        }

        nx = Math.Clamp(nx, 0, 1);
        ny = Math.Clamp(ny, 0, 1);
        return true;
    }

    private static Point ToCanvas(Image image, Canvas canvas, double nx, double ny)
    {
        TryImageRect(image, canvas, out var x, out var y, out var width, out var height);
        return new Point(x + nx * width, y + ny * height);
    }

    private static double Distance(Point start, Point end) =>
        Math.Sqrt((end.X - start.X) * (end.X - start.X) + (end.Y - start.Y) * (end.Y - start.Y));

    private static double SmallerAngle(Point vertex, Point first, Point second)
    {
        var start = Math.Atan2(first.Y - vertex.Y, first.X - vertex.X);
        var end = Math.Atan2(second.Y - vertex.Y, second.X - vertex.X);
        var difference = Math.Abs(start - end);
        if (difference > Math.PI)
        {
            difference = 2 * Math.PI - difference;
        }

        return difference * 180 / Math.PI;
    }

    private static SolidColorBrush BrushFor(string hex)
    {
        var color = (Color)ColorConverter.ConvertFromString(hex);
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }
}
