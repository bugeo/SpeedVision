using System.Globalization;
using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using TrafficSpeedAnalyzer.App.Localization;
using TrafficSpeedAnalyzer.App.ViewModels;
using TrafficSpeedAnalyzer.Core.Calibration;
using TrafficSpeedAnalyzer.Core.Detection;
using TrafficSpeedAnalyzer.Core.Geometry;
using TrafficSpeedAnalyzer.Core.Localization;
using LineSegment = TrafficSpeedAnalyzer.Core.Geometry.LineSegment;

namespace TrafficSpeedAnalyzer.Desktop.Controls;

/// <summary>
/// Visore del video con disegno delle linee, posizionamento dei punti, zoom (rotellina o pizzico sul trackpad)
/// e spostamento (tasto destro o centrale). Tutte le coordinate scambiate con il modello sono in pixel del
/// video, indipendenti da zoom e dimensione della finestra. Stesso comportamento del visore dell'app WPF;
/// qui fotogramma e sovrapposizioni sono disegnati direttamente, in pixel del video.
/// </summary>
public sealed class VideoViewport : UserControl
{
    private static readonly Color ColorA = Color.FromRgb(0x4C, 0xC2, 0xFF);
    private static readonly Color ColorB = Color.FromRgb(0xFF, 0xB5, 0x47);
    private static readonly Color ColorPoint = Color.FromRgb(0xFF, 0x5C, 0x8A);
    private static readonly Color ColorMarker = Color.FromRgb(0x5E, 0xE0, 0x9A);
    private static readonly Color ColorRoad = Color.FromRgb(0x2F, 0xCB, 0x79);
    private static readonly IBrush White = Brushes.White;
    private static readonly IBrush Black = Brushes.Black;

    private const double HandleHitPixels = 12;
    private const double LineHitPixels = 8;
    private const double MaxZoom = 16;

    private readonly Grid _root;
    private readonly Surface _surface;
    private readonly Border _zoomBadge;
    private readonly TextBlock _zoomText;
    private readonly Border _hintBadge;
    private readonly TextBlock _hintText;

    private Matrix _zoom = Matrix.Identity;

    // Disegno di una nuova linea
    private Point2? _drawStart;
    private Point _drawStartScreen;
    private bool _drawPressed;

    // Trascinamento di una linea esistente
    private DetectionLine? _dragLine;
    private int _dragEnd; // 0 = inizio, 1 = fine, 2 = intera linea
    private Point2 _dragOrigin;
    private LineSegment _dragInitial;
    private LineSegment? _dragPreview;

    // Modifica della strada: 0 = sposta tutto, 1–4 = lati, 5–8 = vertici,
    // 10+i = linea tra le corsie i intera, 20+i = sua estremità sinistra, 30+i = sua estremità destra.
    private int? _roadDrag;
    private Point2 _roadOrigin;
    private RoadLayout? _roadInitial;
    private RoadLayout? _roadPreview;

    // Spostamento della vista
    private bool _panning;
    private Point _panLast;

    private Point2? _mouse;

    /// <summary>Posizione del mouse anche fuori dall'immagine (per il punto di fuga).</summary>
    private Point2? _mouseAnywhere;

    public VideoViewport()
    {
        Focusable = true;
        _surface = new Surface(this) { IsHitTestVisible = false };
        _zoomText = new TextBlock { Foreground = White, FontSize = 12, VerticalAlignment = VerticalAlignment.Center };
        var reset = new Button { Margin = new Thickness(10, 0, 0, 0), Padding = new Thickness(8, 2), FontSize = 12, Cursor = new Cursor(StandardCursorType.Hand) };
        reset.Bind(ContentProperty, new Avalonia.Data.Binding(nameof(LocText.Value)) { Source = LocText.For("Adatta") });
        reset.Click += (_, _) => ResetZoom();
        _zoomBadge = new Border
        {
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(12),
            Padding = new Thickness(10, 4),
            CornerRadius = new CornerRadius(6),
            Background = new SolidColorBrush(Color.FromArgb(0xB0, 0x10, 0x14, 0x18)),
            IsVisible = false,
            Child = new StackPanel { Orientation = Orientation.Horizontal, Children = { _zoomText, reset } },
        };
        _hintText = new TextBlock { Foreground = White, FontSize = 13, TextWrapping = TextWrapping.Wrap, MaxWidth = 640 };
        _hintBadge = new Border
        {
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Bottom,
            Margin = new Thickness(0, 0, 0, 14),
            Padding = new Thickness(14, 7),
            CornerRadius = new CornerRadius(8),
            Background = new SolidColorBrush(Color.FromArgb(0xD0, 0x10, 0x14, 0x18)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(0x40, 0xFF, 0xFF, 0xFF)),
            BorderThickness = new Thickness(1),
            IsHitTestVisible = false,
            IsVisible = false,
            Child = _hintText,
        };
        _root = new Grid
        {
            ClipToBounds = true,
            Background = new SolidColorBrush(Color.FromRgb(0x0A, 0x0D, 0x12)),
            Children = { _surface, _zoomBadge, _hintBadge },
        };
        Content = _root;

        _root.SizeChanged += (_, _) => Redraw();
        _root.PointerPressed += OnPointerPressed;
        _root.PointerReleased += OnPointerReleased;
        _root.PointerMoved += OnPointerMoved;
        _root.PointerWheelChanged += OnWheel;
        _root.PointerExited += (_, _) =>
        {
            _mouse = null;
            Redraw();
        };
        _root.AddHandler(InputElement.PointerTouchPadGestureMagnifyEvent, OnMagnify);
    }

    // ───────────── Proprietà collegabili ─────────────

    public static readonly StyledProperty<IImage?> SourceProperty = AvaloniaProperty.Register<VideoViewport, IImage?>(nameof(Source));
    public static readonly StyledProperty<double> VideoWidthProperty = AvaloniaProperty.Register<VideoViewport, double>(nameof(VideoWidth), 1920);
    public static readonly StyledProperty<double> VideoHeightProperty = AvaloniaProperty.Register<VideoViewport, double>(nameof(VideoHeight), 1080);
    public static readonly StyledProperty<LineSegment?> LineAProperty = AvaloniaProperty.Register<VideoViewport, LineSegment?>(nameof(LineA));
    public static readonly StyledProperty<LineSegment?> LineBProperty = AvaloniaProperty.Register<VideoViewport, LineSegment?>(nameof(LineB));
    public static readonly StyledProperty<IReadOnlyList<OverlayPoint>?> PointsProperty = AvaloniaProperty.Register<VideoViewport, IReadOnlyList<OverlayPoint>?>(nameof(Points));
    public static readonly StyledProperty<IReadOnlyList<Point2>?> MarkersProperty = AvaloniaProperty.Register<VideoViewport, IReadOnlyList<Point2>?>(nameof(Markers));
    public static readonly StyledProperty<ViewportMode> ModeProperty = AvaloniaProperty.Register<VideoViewport, ViewportMode>(nameof(Mode));
    public static readonly StyledProperty<bool> EditLinesProperty = AvaloniaProperty.Register<VideoViewport, bool>(nameof(EditLines));
    public static readonly StyledProperty<ICommand?> LineEditedCommandProperty = AvaloniaProperty.Register<VideoViewport, ICommand?>(nameof(LineEditedCommand));
    public static readonly StyledProperty<ICommand?> PointPlacedCommandProperty = AvaloniaProperty.Register<VideoViewport, ICommand?>(nameof(PointPlacedCommand));
    public static readonly StyledProperty<ICommand?> ReferencePlacedCommandProperty = AvaloniaProperty.Register<VideoViewport, ICommand?>(nameof(ReferencePlacedCommand));
    public static readonly StyledProperty<Point2?> RefPointAProperty = AvaloniaProperty.Register<VideoViewport, Point2?>(nameof(RefPointA));
    public static readonly StyledProperty<Point2?> RefPointBProperty = AvaloniaProperty.Register<VideoViewport, Point2?>(nameof(RefPointB));
    public static readonly StyledProperty<Point2?> VanishingPointProperty = AvaloniaProperty.Register<VideoViewport, Point2?>(nameof(VanishingPoint));
    public static readonly StyledProperty<IReadOnlyList<OverlayBox>?> BoxesProperty = AvaloniaProperty.Register<VideoViewport, IReadOnlyList<OverlayBox>?>(nameof(Boxes));
    public static readonly StyledProperty<BoundingBox?> RegionProperty = AvaloniaProperty.Register<VideoViewport, BoundingBox?>(nameof(Region));
    public static readonly StyledProperty<bool> ShowRegionProperty = AvaloniaProperty.Register<VideoViewport, bool>(nameof(ShowRegion));
    public static readonly StyledProperty<RoadLayout?> RoadProperty = AvaloniaProperty.Register<VideoViewport, RoadLayout?>(nameof(Road));
    public static readonly StyledProperty<bool> EditRoadProperty = AvaloniaProperty.Register<VideoViewport, bool>(nameof(EditRoad));
    public static readonly StyledProperty<bool> EditRoadDividersProperty = AvaloniaProperty.Register<VideoViewport, bool>(nameof(EditRoadDividers));
    public static readonly StyledProperty<ICommand?> RoadEditedCommandProperty = AvaloniaProperty.Register<VideoViewport, ICommand?>(nameof(RoadEditedCommand));

    public IImage? Source { get => GetValue(SourceProperty); set => SetValue(SourceProperty, value); }

    public double VideoWidth { get => GetValue(VideoWidthProperty); set => SetValue(VideoWidthProperty, value); }

    public double VideoHeight { get => GetValue(VideoHeightProperty); set => SetValue(VideoHeightProperty, value); }

    public LineSegment? LineA { get => GetValue(LineAProperty); set => SetValue(LineAProperty, value); }

    public LineSegment? LineB { get => GetValue(LineBProperty); set => SetValue(LineBProperty, value); }

    public IReadOnlyList<OverlayPoint>? Points { get => GetValue(PointsProperty); set => SetValue(PointsProperty, value); }

    public IReadOnlyList<Point2>? Markers { get => GetValue(MarkersProperty); set => SetValue(MarkersProperty, value); }

    public ViewportMode Mode { get => GetValue(ModeProperty); set => SetValue(ModeProperty, value); }

    /// <summary>Se vero, le estremità delle linee si possono trascinare.</summary>
    public bool EditLines { get => GetValue(EditLinesProperty); set => SetValue(EditLinesProperty, value); }

    /// <summary>Riceve un <see cref="LineEdit"/>.</summary>
    public ICommand? LineEditedCommand { get => GetValue(LineEditedCommandProperty); set => SetValue(LineEditedCommandProperty, value); }

    /// <summary>Riceve un <see cref="Point2"/> in pixel del video.</summary>
    public ICommand? PointPlacedCommand { get => GetValue(PointPlacedCommandProperty); set => SetValue(PointPlacedCommandProperty, value); }

    /// <summary>Riceve il punto cliccato nelle modalità PlaceRefA, PlaceRefB e PlaceVanishing.</summary>
    public ICommand? ReferencePlacedCommand { get => GetValue(ReferencePlacedCommandProperty); set => SetValue(ReferencePlacedCommandProperty, value); }

    public Point2? RefPointA { get => GetValue(RefPointAProperty); set => SetValue(RefPointAProperty, value); }

    public Point2? RefPointB { get => GetValue(RefPointBProperty); set => SetValue(RefPointBProperty, value); }

    public Point2? VanishingPoint { get => GetValue(VanishingPointProperty); set => SetValue(VanishingPointProperty, value); }

    public IReadOnlyList<OverlayBox>? Boxes { get => GetValue(BoxesProperty); set => SetValue(BoxesProperty, value); }

    public BoundingBox? Region { get => GetValue(RegionProperty); set => SetValue(RegionProperty, value); }

    public bool ShowRegion { get => GetValue(ShowRegionProperty); set => SetValue(ShowRegionProperty, value); }

    public RoadLayout? Road { get => GetValue(RoadProperty); set => SetValue(RoadProperty, value); }

    public bool EditRoad { get => GetValue(EditRoadProperty); set => SetValue(EditRoadProperty, value); }

    public bool EditRoadDividers { get => GetValue(EditRoadDividersProperty); set => SetValue(EditRoadDividersProperty, value); }

    public ICommand? RoadEditedCommand { get => GetValue(RoadEditedCommandProperty); set => SetValue(RoadEditedCommandProperty, value); }

    /// <summary>Livello di zoom (1 = video intero); per i test.</summary>
    internal double ZoomLevel => _zoom.M11;

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property.OwnerType != typeof(VideoViewport) || _surface is null)
        {
            return;
        }

        if (change.Property == VideoWidthProperty || change.Property == VideoHeightProperty)
        {
            ResetZoom();
        }

        if (change.Property == ModeProperty)
        {
            _drawStart = null;
            _drawPressed = false;
            UpdateCursorAndHint();
        }

        Redraw();
    }

    // ───────────── Coordinate ─────────────

    /// <summary>Scala e spostamento che adattano il video al riquadro (come un Viewbox «Uniform»).</summary>
    private (double Scale, double OffsetX, double OffsetY) Fit()
    {
        double w = _root.Bounds.Width, h = _root.Bounds.Height;
        if (VideoWidth <= 0 || VideoHeight <= 0 || w <= 0 || h <= 0)
        {
            return (1, 0, 0);
        }

        double s = Math.Min(w / VideoWidth, h / VideoHeight);
        return (s, (w - (VideoWidth * s)) / 2, (h - (VideoHeight * s)) / 2);
    }

    /// <summary>Da pixel del video a coordinate del riquadro: prima l'adattamento, poi lo zoom.</summary>
    internal Matrix VideoToScreen
    {
        get
        {
            (double s, double ox, double oy) = Fit();
            return Matrix.CreateScale(s, s) * Matrix.CreateTranslation(ox, oy) * _zoom;
        }
    }

    /// <summary>Pixel dello schermo per pixel del video (zoom compreso).</summary>
    private double ScreenScale => Math.Max(1e-6, Fit().Scale * _zoom.M11);

    internal Point2 ToVideo(Point screen)
    {
        Matrix inv = VideoToScreen.Invert();
        Point p = screen.Transform(inv);
        return new Point2(p.X, p.Y);
    }

    private Point2 ToVideo(PointerEventArgs e) => ToVideo(e.GetPosition(_root));

    private bool InsideVideo(Point2 p) => p.X >= 0 && p.Y >= 0 && p.X <= VideoWidth && p.Y <= VideoHeight;

    // ───────────── Zoom ─────────────

    public void ResetZoom()
    {
        _zoom = Matrix.Identity;
        UpdateZoomBadge();
        Redraw();
    }

    private void ZoomAt(Point at, double factor)
    {
        double newScale = Math.Clamp(_zoom.M11 * factor, 1, MaxZoom);
        factor = newScale / _zoom.M11;
        if (Math.Abs(factor - 1) < 1e-6)
        {
            return;
        }

        Matrix m = _zoom * Matrix.CreateTranslation(-at.X, -at.Y) * Matrix.CreateScale(factor, factor) * Matrix.CreateTranslation(at.X, at.Y);
        _zoom = newScale <= 1.0001 ? Matrix.Identity : m;
        UpdateZoomBadge();
        Redraw();
    }

    private void OnWheel(object? sender, PointerWheelEventArgs e)
    {
        if (Math.Abs(e.Delta.Y) < 1e-9)
        {
            return;
        }

        ZoomAt(e.GetPosition(_root), e.Delta.Y > 0 ? 1.25 : 1 / 1.25);
        e.Handled = true;
    }

    /// <summary>Pizzico sul trackpad del Mac.</summary>
    private void OnMagnify(object? sender, PointerDeltaEventArgs e)
    {
        ZoomAt(e.GetPosition(_root), 1 + e.Delta.X);
        e.Handled = true;
    }

    private void UpdateZoomBadge()
    {
        double z = _zoom.M11;
        _zoomBadge.IsVisible = z > 1.0001;
        _zoomText.Text = Loc.S($"Zoom {z:0.#}×  ·  tasto destro per spostare");
    }

    // ───────────── Mouse ─────────────

    private void OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        PointerPointProperties props = e.GetCurrentPoint(_root).Properties;
        if (props.IsRightButtonPressed || props.IsMiddleButtonPressed)
        {
            if (_zoom.M11 > 1.0001)
            {
                _panning = true;
                _panLast = e.GetPosition(_root);
                e.Pointer.Capture(_root);
                _root.Cursor = new Cursor(StandardCursorType.SizeAll);
                e.Handled = true;
            }

            return;
        }

        if (!props.IsLeftButtonPressed)
        {
            return;
        }

        Focus();
        Point2 p = ToVideo(e);

        if (Mode is ViewportMode.DrawLineA or ViewportMode.DrawLineB)
        {
            if (!InsideVideo(p))
            {
                return;
            }

            if (_drawStart is null)
            {
                _drawStart = p;
                _drawStartScreen = e.GetPosition(_root);
                _drawPressed = true;
                e.Pointer.Capture(_root);
            }
            else
            {
                FinishDrawing(p, e.Pointer);
            }

            e.Handled = true;
            return;
        }

        if (EditRoad && Road is { } road && HitTestRoad(p, road) is { } part)
        {
            _roadDrag = part;
            _roadOrigin = p;
            _roadInitial = road;
            _roadPreview = road;
            e.Pointer.Capture(_root);
            e.Handled = true;
            return;
        }

        if (EditLines && HitTestLines(p) is { } hit)
        {
            _dragLine = hit.Line;
            _dragEnd = hit.End;
            _dragOrigin = p;
            _dragInitial = hit.Segment;
            _dragPreview = hit.Segment;
            e.Pointer.Capture(_root);
            e.Handled = true;
            return;
        }

        // Riferimenti A e B nell'immagine; il punto di fuga anche fuori (spesso è sopra l'inquadratura).
        if (Mode is ViewportMode.PlaceRefA or ViewportMode.PlaceRefB or ViewportMode.PlaceVanishing
            && (Mode == ViewportMode.PlaceVanishing || InsideVideo(p)))
        {
            Execute(ReferencePlacedCommand, p);
            e.Handled = true;
            return;
        }

        if (Mode == ViewportMode.PlacePoint && InsideVideo(p))
        {
            Execute(PointPlacedCommand, p);
            e.Handled = true;
        }
    }

    private void OnPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (_panning && e.InitialPressMouseButton is MouseButton.Right or MouseButton.Middle)
        {
            _panning = false;
            e.Pointer.Capture(null);
            UpdateCursorAndHint();
            e.Handled = true;
            return;
        }

        if (e.InitialPressMouseButton != MouseButton.Left)
        {
            return;
        }

        Point2 p = ToVideo(e);
        if (_drawStart is not null && _drawPressed)
        {
            _drawPressed = false;
            Point now = e.GetPosition(_root);
            Point d = now - _drawStartScreen;
            if (Math.Sqrt((d.X * d.X) + (d.Y * d.Y)) > 6)
            {
                FinishDrawing(p, e.Pointer); // disegno con trascinamento
            }

            // altrimenti si attende il secondo clic
            return;
        }

        if (_roadDrag is not null && _roadPreview is { } edited)
        {
            _roadDrag = null;
            _roadPreview = null;
            e.Pointer.Capture(null);
            Execute(RoadEditedCommand, edited);
            Redraw();
            return;
        }

        if (_dragLine is { } line && _dragPreview is { } seg)
        {
            _dragLine = null;
            _dragPreview = null;
            e.Pointer.Capture(null);
            Execute(LineEditedCommand, new LineEdit(line, seg));
            Redraw();
        }
    }

    private void FinishDrawing(Point2 end, IPointer pointer)
    {
        if (_drawStart is not { } start)
        {
            return;
        }

        _drawStart = null;
        _drawPressed = false;
        pointer.Capture(null);
        end = ClampToVideo(end);
        if (Point2.Distance(start, end) * ScreenScale < 8)
        {
            Redraw(); // troppo corta: probabilmente un doppio clic involontario
            return;
        }

        DetectionLine which = Mode == ViewportMode.DrawLineA ? DetectionLine.A : DetectionLine.B;
        Execute(LineEditedCommand, new LineEdit(which, new LineSegment(start, end)));
        Redraw();
    }

    private void OnPointerMoved(object? sender, PointerEventArgs e)
    {
        if (_panning)
        {
            Point now = e.GetPosition(_root);
            Point delta = now - _panLast;
            _panLast = now;
            _zoom *= Matrix.CreateTranslation(delta.X, delta.Y);
            Redraw();
            return;
        }

        Point2 p = ToVideo(e);
        _mouse = InsideVideo(p) ? p : null;
        _mouseAnywhere = p;

        if (_roadDrag is { } part && _roadInitial is { } initial)
        {
            _roadPreview = DragRoad(initial, part, ClampToVideo(p));
        }
        else if (EditRoad && Road is { } road && Mode == ViewportMode.None)
        {
            _root.Cursor = new Cursor(HitTestRoad(p, road) switch
            {
                0 or (>= 1 and <= 4) => StandardCursorType.SizeAll,
                >= 5 and <= 8 => StandardCursorType.Hand,
                >= 20 => StandardCursorType.Hand,
                >= 10 => StandardCursorType.SizeNorthSouth,
                _ => StandardCursorType.Arrow,
            });
        }
        else if (_dragLine is not null)
        {
            Point2 q = ClampToVideo(p);
            _dragPreview = _dragEnd switch
            {
                0 => _dragInitial with { Start = q },
                1 => _dragInitial with { End = q },
                _ => new LineSegment(_dragInitial.Start + (q - _dragOrigin), _dragInitial.End + (q - _dragOrigin)),
            };
        }
        else if (EditLines && Mode == ViewportMode.None)
        {
            _root.Cursor = new Cursor(HitTestLines(p) is { } h ? (h.End == 2 ? StandardCursorType.SizeAll : StandardCursorType.Hand) : StandardCursorType.Arrow);
        }

        Redraw();
    }

    private Point2 ClampToVideo(Point2 p) => new(Math.Clamp(p.X, 0, VideoWidth), Math.Clamp(p.Y, 0, VideoHeight));

    /// <summary>Parte della strada sotto il mouse (vedi <see cref="_roadDrag"/>), o null.</summary>
    private int? HitTestRoad(Point2 p, RoadLayout road)
    {
        double s = ScreenScale;
        bool Near(double d) => d * s <= HandleHitPixels;

        for (int i = 0; i < 4; i++)
        {
            if (Near(Point2.Distance(p, road.Corners[i])))
            {
                return 5 + i;
            }
        }

        // Passo «Bordi della strada»: solo i quattro vertici. Le linee tra le corsie dal passo successivo.
        if (!EditRoadDividers)
        {
            return null;
        }

        for (int i = 0; i < road.Dividers.Count; i++)
        {
            (Point2 l, Point2 r) = road.DividerEnds(i);
            if (Near(Point2.Distance(p, l)))
            {
                return 20 + i;
            }

            if (Near(Point2.Distance(p, r)))
            {
                return 30 + i;
            }
        }

        for (int i = 0; i < road.Dividers.Count; i++)
        {
            (Point2 l, Point2 r) = road.DividerEnds(i);
            if (Near(DistanceToSegment(p, l, r)))
            {
                return 10 + i;
            }
        }

        return null;
    }

    private static double DistanceToSegment(Point2 p, Point2 a, Point2 b)
    {
        Point2 ab = b - a;
        double len2 = Point2.Dot(ab, ab);
        double t = len2 < 1e-12 ? 0 : Math.Clamp(Point2.Dot(p - a, ab) / len2, 0, 1);
        return Point2.Distance(p, a + (ab * t));
    }

    private RoadLayout DragRoad(RoadLayout r, int part, Point2 to)
    {
        Point2 d = to - _roadOrigin;
        return part switch
        {
            >= 30 => r.MoveDivider(part - 30, +1, to, _roadOrigin),
            >= 20 => r.MoveDivider(part - 20, -1, to, _roadOrigin),
            >= 10 => r.MoveDivider(part - 10, 0, to, _roadOrigin),
            >= 5 => r.MoveCorner(part - 5, r.Corners[part - 5] + d, VideoWidth, VideoHeight),
            >= 1 => r.MoveEdge(part - 1, d, VideoWidth, VideoHeight),
            _ => r.Move(d, VideoWidth, VideoHeight),
        };
    }

    private (DetectionLine Line, int End, LineSegment Segment)? HitTestLines(Point2 p)
    {
        double s = ScreenScale;
        (DetectionLine, LineSegment?)[] lines = [(DetectionLine.A, LineA), (DetectionLine.B, LineB)];

        // Prima le estremità (priorità), poi il corpo della linea.
        foreach ((DetectionLine id, LineSegment? seg) in lines)
        {
            if (seg is not { } l)
            {
                continue;
            }

            if (Point2.Distance(p, l.Start) * s <= HandleHitPixels)
            {
                return (id, 0, l);
            }

            if (Point2.Distance(p, l.End) * s <= HandleHitPixels)
            {
                return (id, 1, l);
            }
        }

        foreach ((DetectionLine id, LineSegment? seg) in lines)
        {
            if (seg is not { } l || l.IsDegenerate)
            {
                continue;
            }

            double t = l.ProjectParameter(p);
            if (t > 0 && t < 1 && Math.Abs(l.SignedDistance(p)) * s <= LineHitPixels)
            {
                return (id, 2, l);
            }
        }

        return null;
    }

    private static void Execute(ICommand? cmd, object parameter)
    {
        if (cmd?.CanExecute(parameter) == true)
        {
            cmd.Execute(parameter);
        }
    }

    private void UpdateCursorAndHint()
    {
        string? hint = Mode switch
        {
            ViewportMode.DrawLineA => Loc.T("Linea A: clicca un estremo e poi l'altro, da un bordo all'altro della carreggiata. Esc per annullare."),
            ViewportMode.DrawLineB => Loc.T("Linea B: clicca un estremo e poi l'altro, da un bordo all'altro della carreggiata. Esc per annullare."),
            _ => null,
        };
        _root.Cursor = new Cursor(Mode == ViewportMode.None ? StandardCursorType.Arrow : StandardCursorType.Cross);
        _hintText.Text = hint ?? string.Empty;
        _hintBadge.IsVisible = hint is not null;
    }

    private void Redraw() => _surface?.InvalidateVisual();

    // ───────────── Disegno ─────────────

    /// <summary>Disegna fotogramma e sovrapposizioni; le misure sono in pixel del video, <paramref name="inv"/> = 1 pixel dello schermo.</summary>
    private void Draw(DrawingContext dc)
    {
        if (VideoWidth <= 0 || VideoHeight <= 0)
        {
            return;
        }

        using DrawingContext.PushedState _ = dc.PushTransform(VideoToScreen);
        double inv = 1 / ScreenScale;
        var c = new Painter(dc, inv);

        if (Source is { } img)
        {
            using (dc.PushRenderOptions(new RenderOptions { BitmapInterpolationMode = BitmapInterpolationMode.HighQuality }))
            {
                dc.DrawImage(img, new Rect(img.Size), new Rect(0, 0, VideoWidth, VideoHeight));
            }
        }

        LineSegment? a = _dragLine == DetectionLine.A ? _dragPreview : LineA;
        LineSegment? b = _dragLine == DetectionLine.B ? _dragPreview : LineB;

        if (a is { } la && b is { } lb)
        {
            DrawZone(c, la, lb);
        }

        if (a is { } l1)
        {
            DrawDetectionLine(c, l1, ColorA, "A");
        }

        if (b is { } l2)
        {
            DrawDetectionLine(c, l2, ColorB, "B");
        }

        DrawRoad(c, _roadPreview ?? Road);
        DrawTrack(c);
        DrawMarkers(c);
        DrawRegion(c);
        DrawBoxes(c);

        if (_drawStart is { } ds && _mouse is { } m)
        {
            Color col = Mode == ViewportMode.DrawLineA ? ColorA : ColorB;
            c.Line(ds, m, new SolidColorBrush(col), 2, dashed: true);
            c.Dot(ds, 5, new SolidColorBrush(col), White, 1.5);
        }

        DrawVanishing(c);

        if (Mode is ViewportMode.PlacePoint or ViewportMode.PlaceRefA or ViewportMode.PlaceRefB && _mouse is { } cross && _dragLine is null)
        {
            var guide = new SolidColorBrush(Color.FromArgb(110, 255, 255, 255));
            c.Line(new Point2(0, cross.Y), new Point2(VideoWidth, cross.Y), guide, 1, dashed: false);
            c.Line(new Point2(cross.X, 0), new Point2(cross.X, VideoHeight), guide, 1, dashed: false);
            c.Dot(cross, 8, null, new SolidColorBrush(ColorPoint), 1.5);
        }
    }

    private void DrawVanishing(Painter c)
    {
        Point2? v = Mode == ViewportMode.PlaceVanishing ? _mouseAnywhere : VanishingPoint;
        if (v is { } fv && RefPointA is { } ga && RefPointB is { } gb)
        {
            Point2 ab = gb - ga;
            Point2[] through =
            [
                new Point2(fv.X, fv.Y + 1),        // verticale verso il basso
                Point2.Lerp(ga, gb, 0.5),          // a metà tra A e B
                ga - ab,                           // prima di A, alla distanza A–B
                gb + ab,                           // dopo B, alla distanza A–B
            ];
            var outline = new SolidColorBrush(Color.FromArgb(120, 0, 0, 0));
            var guide = new SolidColorBrush(Color.FromArgb(230, 255, 255, 255));
            foreach (Point2 t in through)
            {
                if (Point2.Distance(t, fv) < 1e-6)
                {
                    continue;
                }

                Point2 end = BeyondToEdge(fv, t);
                c.Line(fv, end, outline, 3.5, dashed: false);
                c.Line(fv, end, guide, 1.5, dashed: false);
            }
        }

        foreach ((Point2? r, Color col) in new[] { (RefPointA, ColorA), (RefPointB, ColorB) })
        {
            if (r is { } rp && v is { } vp)
            {
                Point2 end = BeyondToEdge(vp, rp);
                c.Line(vp, end, new SolidColorBrush(Color.FromArgb(140, 0, 0, 0)), 4, dashed: false);
                c.Line(vp, end, new SolidColorBrush(col), 2, dashed: true);
            }
        }

        if (v is { } vv && (RefPointA is not null || RefPointB is not null))
        {
            c.Dot(vv, 7, new SolidColorBrush(Color.FromArgb(220, 255, 255, 255)), new SolidColorBrush(ColorB), 2.5);
        }

        if (RefPointA is { } pa)
        {
            c.Dot(pa, 7, new SolidColorBrush(ColorA), White, 2);
            c.Label(pa + new Point2(10 * c.Inv, -22 * c.Inv), "A", 13, FontWeight.Bold, Black, new SolidColorBrush(ColorA), 5, 0);
        }

        if (RefPointB is { } pb)
        {
            c.Dot(pb, 7, new SolidColorBrush(ColorB), White, 2);
            c.Label(pb + new Point2(10 * c.Inv, -22 * c.Inv), "B", 13, FontWeight.Bold, Black, new SolidColorBrush(ColorB), 5, 0);
        }
    }

    /// <summary>Punto in cui la semiretta da <paramref name="from"/> attraverso <paramref name="through"/> esce dal video.</summary>
    private Point2 BeyondToEdge(Point2 from, Point2 through)
    {
        Point2 d = through - from;
        double best = double.PositiveInfinity;
        void Try(double t)
        {
            if (t >= 1 && t < best)
            {
                Point2 q = from + (d * t);
                if (q.X >= -0.5 && q.X <= VideoWidth + 0.5 && q.Y >= -0.5 && q.Y <= VideoHeight + 0.5)
                {
                    best = t;
                }
            }
        }

        if (Math.Abs(d.X) > 1e-9)
        {
            Try((0 - from.X) / d.X);
            Try((VideoWidth - from.X) / d.X);
        }

        if (Math.Abs(d.Y) > 1e-9)
        {
            Try((0 - from.Y) / d.Y);
            Try((VideoHeight - from.Y) / d.Y);
        }

        return double.IsFinite(best) ? from + (d * best) : through;
    }

    private static void DrawZone(Painter c, LineSegment a, LineSegment b)
    {
        // Ordina i vertici per evitare un poligono "a farfalla".
        bool crossed = GeometryMath.SegmentsIntersect(new LineSegment(a.Start, b.Start), new LineSegment(a.End, b.End));
        Point2 b1 = crossed ? b.End : b.Start;
        Point2 b2 = crossed ? b.Start : b.End;
        c.Polygon([a.Start, a.End, b2, b1], new SolidColorBrush(Color.FromArgb(28, 255, 255, 255)), null, 0, dashed: false);
    }

    private void DrawDetectionLine(Painter c, LineSegment l, Color color, string label)
    {
        var brush = new SolidColorBrush(color);
        c.Line(l.Start, l.End, new SolidColorBrush(Color.FromArgb(140, 0, 0, 0)), 6, dashed: false);
        c.Line(l.Start, l.End, brush, 3, dashed: false);

        if (EditLines)
        {
            c.Dot(l.Start, 7, White, brush, 2.5);
            c.Dot(l.End, 7, White, brush, 2.5);
        }

        // Etichetta tonda accanto all'estremo iniziale.
        Point2 dir = l.IsDegenerate ? new Point2(1, 0) : l.Direction * (1 / l.Length);
        Point2 pos = l.Start - (dir * (20 * c.Inv));
        c.Dot(pos, 12, brush, Black, 1);
        c.CenteredText(pos, label, 14, FontWeight.Bold, Black);
    }

    /// <summary>Strada: quadrilatero, linee tra le corsie e numero delle corsie (1 = la più vicina).</summary>
    private void DrawRoad(Painter c, RoadLayout? road)
    {
        if (road is null)
        {
            return;
        }

        var brush = new SolidColorBrush(ColorRoad);
        c.Polygon(
            road.Corners,
            new SolidColorBrush(Color.FromArgb(EditRoad ? (byte)36 : (byte)14, ColorRoad.R, ColorRoad.G, ColorRoad.B)),
            brush,
            EditRoad ? 3 : 1.5,
            dashed: !EditRoad);

        for (int i = 0; i < road.Dividers.Count; i++)
        {
            (Point2 l, Point2 r) = road.DividerEnds(i);
            c.Line(l, r, new SolidColorBrush(Color.FromArgb(150, 0, 0, 0)), 5, dashed: false);
            c.Line(l, r, White, 2.5, dashed: true);
            if (EditRoad && EditRoadDividers)
            {
                // Estremità: si spostano una per volta; il punto centrale sposta tutta la linea.
                c.Dot(l, 6, White, brush, 2.5);
                c.Dot(r, 6, White, brush, 2.5);
                c.Dot(Point2.Lerp(l, r, 0.5), 5, brush, White, 2);
            }
        }

        if (EditRoad)
        {
            foreach (Point2 corner in road.Corners)
            {
                c.Dot(corner, 8, White, brush, 3);
            }
        }

        for (int lane = 0; lane < road.Lanes; lane++)
        {
            Point2 center = road.LaneCenter(lane);
            c.Label(center + new Point2(-28 * c.Inv, -9 * c.Inv), Loc.S($"Corsia {lane + 1}"), 12, FontWeight.SemiBold, Black, brush, 5, 1);
        }
    }

    private void DrawTrack(Painter c)
    {
        IReadOnlyList<OverlayPoint>? pts = Points;
        if (pts is null || pts.Count == 0)
        {
            return;
        }

        var brush = new SolidColorBrush(ColorPoint);
        if (pts.Count > 1)
        {
            c.Polyline(pts.Select(p => p.Position), new SolidColorBrush(Color.FromArgb(180, ColorPoint.R, ColorPoint.G, ColorPoint.B)), 1.5);
        }

        foreach (OverlayPoint p in pts)
        {
            if (p.IsCurrentFrame)
            {
                c.Dot(p.Position, 9, null, White, 2);
                c.Dot(p.Position, 5, brush, White, 1);
            }
            else
            {
                c.Dot(p.Position, 3.5, brush, null, 0);
            }
        }
    }

    private void DrawRegion(Painter c)
    {
        if (!ShowRegion || Region is not { } r)
        {
            return;
        }

        c.Polygon(
            [new Point2(r.X, r.Y), new Point2(r.X + r.Width, r.Y), new Point2(r.X + r.Width, r.Y + r.Height), new Point2(r.X, r.Y + r.Height)],
            null,
            new SolidColorBrush(Color.FromArgb(170, 255, 255, 255)),
            1.5,
            dashed: true);
        c.Label(new Point2(r.X + (4 * c.Inv), r.Y + (4 * c.Inv)), Loc.T("Zona analizzata"), 12, FontWeight.Normal, White,
            new SolidColorBrush(Color.FromArgb(150, 0, 0, 0)), 4, 1);
    }

    private void DrawBoxes(Painter c)
    {
        IReadOnlyList<OverlayBox>? boxes = Boxes;
        if (boxes is null)
        {
            return;
        }

        foreach (OverlayBox b in boxes)
        {
            Color col = b.Confirmed ? ColorMarker : Color.FromRgb(0xC8, 0xCC, 0xD4);
            BoundingBox box = b.Box;
            c.Polygon(
                [new Point2(box.X, box.Y), new Point2(box.X + Math.Max(1, box.Width), box.Y),
                 new Point2(box.X + Math.Max(1, box.Width), box.Y + Math.Max(1, box.Height)), new Point2(box.X, box.Y + Math.Max(1, box.Height))],
                null,
                new SolidColorBrush(col),
                2,
                dashed: !b.Confirmed);

            // Punto di riferimento usato per la misura: vertice basso sul davanti (verso noto), altrimenti
            // centro del lato inferiore; nella calibrazione i due vertici bassi, perni delle linee A e B.
            Point2[] refs = b.Heading switch
            {
                > 1 => [box.BottomLeft, box.BottomRight],
                1 => [box.BottomRight],
                < 0 => [box.BottomLeft],
                _ => [box.BottomCenter],
            };
            foreach (Point2 r in refs)
            {
                c.Dot(r, 4.5, new SolidColorBrush(ColorPoint), White, 1.2);
            }

            c.Label(new Point2(box.X, box.Y - (18 * c.Inv)), b.Label, 12, FontWeight.Normal, Black, new SolidColorBrush(col), 4, 1);
        }
    }

    private void DrawMarkers(Painter c)
    {
        if (Markers is not { } markers)
        {
            return;
        }

        foreach (Point2 m in markers)
        {
            double r = 7 * c.Inv;
            c.Polygon(
                [new Point2(m.X, m.Y - r), new Point2(m.X + r, m.Y), new Point2(m.X, m.Y + r), new Point2(m.X - r, m.Y)],
                new SolidColorBrush(ColorMarker),
                Black,
                1,
                dashed: false);
        }
    }

    /// <summary>
    /// Disegno in pixel del video. Spessori, raggi e caratteri sono in pixel dello schermo (moltiplicati per
    /// <see cref="Inv"/>), così restano uguali a qualunque zoom, come nell'app WPF.
    /// </summary>
    private readonly struct Painter(DrawingContext dc, double inv)
    {
        private static readonly Typeface Face = new(FontFamily.Default);

        public double Inv => inv;

        private static Point P(Point2 p) => new(p.X, p.Y);

        private IPen Pen(IBrush brush, double thickness, bool dashed) => new Pen(brush, thickness * inv,
            dashed ? new DashStyle([4, 3], 0) : null, PenLineCap.Round, PenLineJoin.Round);

        public void Line(Point2 a, Point2 b, IBrush brush, double thickness, bool dashed) =>
            dc.DrawLine(Pen(brush, thickness, dashed), P(a), P(b));

        public void Dot(Point2 center, double radius, IBrush? fill, IBrush? stroke, double thickness) =>
            dc.DrawEllipse(fill, stroke is null || thickness <= 0 ? null : Pen(stroke, thickness, false), P(center), radius * inv, radius * inv);

        public void Polygon(IReadOnlyList<Point2> points, IBrush? fill, IBrush? stroke, double thickness, bool dashed)
        {
            var geo = new StreamGeometry();
            using (StreamGeometryContext g = geo.Open())
            {
                g.BeginFigure(P(points[0]), fill is not null);
                for (int i = 1; i < points.Count; i++)
                {
                    g.LineTo(P(points[i]));
                }

                g.EndFigure(true);
            }

            dc.DrawGeometry(fill, stroke is null ? null : Pen(stroke, thickness, dashed), geo);
        }

        public void Polyline(IEnumerable<Point2> points, IBrush stroke, double thickness)
        {
            var geo = new StreamGeometry();
            using (StreamGeometryContext g = geo.Open())
            {
                bool first = true;
                foreach (Point2 p in points)
                {
                    if (first)
                    {
                        g.BeginFigure(P(p), false);
                        first = false;
                    }
                    else
                    {
                        g.LineTo(P(p));
                    }
                }

                if (!first)
                {
                    g.EndFigure(false);
                }
            }

            dc.DrawGeometry(null, Pen(stroke, thickness, false), geo);
        }

        private FormattedText Text(string text, double size, FontWeight weight, IBrush foreground) =>
            new(text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, new Typeface(FontFamily.Default, FontStyle.Normal, weight), size * inv, foreground);

        /// <summary>Etichetta con sfondo; <paramref name="topLeft"/> è l'angolo in alto a sinistra.</summary>
        public void Label(Point2 topLeft, string text, double size, FontWeight weight, IBrush foreground, IBrush background, double padX, double padY)
        {
            FormattedText ft = Text(text, size, weight, foreground);
            var rect = new Rect(topLeft.X, topLeft.Y, ft.Width + (2 * padX * inv), ft.Height + (2 * padY * inv));
            dc.FillRectangle(background, rect);
            dc.DrawText(ft, new Point(rect.X + (padX * inv), rect.Y + (padY * inv)));
        }

        public void CenteredText(Point2 center, string text, double size, FontWeight weight, IBrush foreground)
        {
            FormattedText ft = Text(text, size, weight, foreground);
            dc.DrawText(ft, new Point(center.X - (ft.Width / 2), center.Y - (ft.Height / 2)));
        }
    }

    /// <summary>Superficie su cui il visore disegna; occupa tutto il riquadro.</summary>
    private sealed class Surface(VideoViewport owner) : Control
    {
        public override void Render(DrawingContext context) => owner.Draw(context);
    }
}
