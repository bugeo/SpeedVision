using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using TrafficSpeedAnalyzer.App.ViewModels;
using TrafficSpeedAnalyzer.Core.Calibration;
using TrafficSpeedAnalyzer.Core.Detection;
using TrafficSpeedAnalyzer.Core.Geometry;
using LineSegment = TrafficSpeedAnalyzer.Core.Geometry.LineSegment;
using TrafficSpeedAnalyzer.Core.Localization;

namespace TrafficSpeedAnalyzer.App.Controls;

/// <summary>
/// Visore del video con disegno delle linee, posizionamento dei punti, zoom (rotellina)
/// e spostamento (tasto destro o centrale). Tutte le coordinate scambiate con il modello
/// sono in pixel del video, indipendenti da zoom e dimensione della finestra.
/// </summary>
public partial class VideoViewport : UserControl
{
    private static readonly Color ColorA = Color.FromRgb(0x4C, 0xC2, 0xFF);
    private static readonly Color ColorB = Color.FromRgb(0xFF, 0xB5, 0x47);
    private static readonly Color ColorPoint = Color.FromRgb(0xFF, 0x5C, 0x8A);
    private static readonly Color ColorLaneVanishing = Color.FromRgb(0xE5, 0x2B, 0x2B);
    private static readonly Color ColorMarker = Color.FromRgb(0x5E, 0xE0, 0x9A);

    private const double HandleHitPixels = 12;
    private const double LineHitPixels = 8;
    private const double MaxZoom = 16;

    private readonly MatrixTransform _zoom = new(Matrix.Identity);

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

    // Modifica della strada: 0 = sposta tutto, 1–4 = lati (sopra, destra, sotto, sinistra),
    // 5–8 = vertici (alto-sinistra, alto-destra, basso-destra, basso-sinistra),
    // 10+i = linea tra le corsie i intera, 20+i = sua estremità sinistra, 30+i = sua estremità destra.
    private int? _roadDrag;
    private Point2 _roadOrigin;
    private RoadLayout? _roadInitial;
    private RoadLayout? _roadPreview;

    private static readonly Color ColorRoad = Color.FromRgb(0x2F, 0xCB, 0x79);

    // Spostamento della vista
    private bool _panning;
    private Point _panLast;

    private Point2? _mouse;

    /// <summary>Posizione del mouse anche fuori dall'immagine (per il punto di fuga).</summary>
    private Point2? _mouseAnywhere;

    public VideoViewport()
    {
        InitializeComponent();
        FitBox.RenderTransform = _zoom;
        Root.SizeChanged += (_, _) => Redraw();
        Root.MouseLeftButtonDown += OnLeftDown;
        Root.MouseLeftButtonUp += OnLeftUp;
        Root.MouseMove += OnMouseMove;
        Root.MouseWheel += OnWheel;
        Root.MouseRightButtonDown += OnPanDown;
        Root.MouseRightButtonUp += OnPanUp;
        Root.PreviewMouseDown += OnMiddleDown;
        Root.PreviewMouseUp += OnMiddleUp;
        Root.MouseLeave += (_, _) =>
        {
            _mouse = null;
            Redraw();
        };
    }

    // ───────────── Proprietà collegabili ─────────────

    public static readonly DependencyProperty SourceProperty = Register(nameof(Source), typeof(ImageSource), null);
    public static readonly DependencyProperty VideoWidthProperty = Register(nameof(VideoWidth), typeof(double), 1920.0);
    public static readonly DependencyProperty VideoHeightProperty = Register(nameof(VideoHeight), typeof(double), 1080.0);
    public static readonly DependencyProperty LineAProperty = Register(nameof(LineA), typeof(LineSegment?), null);
    public static readonly DependencyProperty LineBProperty = Register(nameof(LineB), typeof(LineSegment?), null);
    public static readonly DependencyProperty PointsProperty = Register(nameof(Points), typeof(IReadOnlyList<OverlayPoint>), null);
    public static readonly DependencyProperty MarkersProperty = Register(nameof(Markers), typeof(IReadOnlyList<Point2>), null);
    public static readonly DependencyProperty ModeProperty = Register(nameof(Mode), typeof(ViewportMode), ViewportMode.None);
    public static readonly DependencyProperty EditLinesProperty = Register(nameof(EditLines), typeof(bool), false);
    public static readonly DependencyProperty LineEditedCommandProperty = Register(nameof(LineEditedCommand), typeof(ICommand), null);
    public static readonly DependencyProperty PointPlacedCommandProperty = Register(nameof(PointPlacedCommand), typeof(ICommand), null);
    public static readonly DependencyProperty ReferencePlacedCommandProperty = Register(nameof(ReferencePlacedCommand), typeof(ICommand), null);
    public static readonly DependencyProperty RefPointAProperty = Register(nameof(RefPointA), typeof(Point2?), null);
    public static readonly DependencyProperty RefPointBProperty = Register(nameof(RefPointB), typeof(Point2?), null);
    public static readonly DependencyProperty VanishingPointProperty = Register(nameof(VanishingPoint), typeof(Point2?), null);
    public static readonly DependencyProperty LaneVanishingPointProperty = Register(nameof(LaneVanishingPoint), typeof(Point2?), null);
    public static readonly DependencyProperty BoxesProperty = Register(nameof(Boxes), typeof(IReadOnlyList<OverlayBox>), null);
    public static readonly DependencyProperty RegionProperty = Register(nameof(Region), typeof(BoundingBox?), null);
    public static readonly DependencyProperty ShowRegionProperty = Register(nameof(ShowRegion), typeof(bool), false);
    public static readonly DependencyProperty RoadProperty = Register(nameof(Road), typeof(RoadLayout), null);
    public static readonly DependencyProperty EditRoadProperty = Register(nameof(EditRoad), typeof(bool), false);
    public static readonly DependencyProperty EditRoadDividersProperty = Register(nameof(EditRoadDividers), typeof(bool), false);
    public static readonly DependencyProperty RoadEditedCommandProperty = Register(nameof(RoadEditedCommand), typeof(ICommand), null);

    /// <summary>Strada indicata dall'utente (rettangolo e linee tra le corsie).</summary>
    public RoadLayout? Road { get => (RoadLayout?)GetValue(RoadProperty); set => SetValue(RoadProperty, value); }

    /// <summary>Se vero, rettangolo e linee tra le corsie si possono trascinare.</summary>
    public bool EditRoad { get => (bool)GetValue(EditRoadProperty); set => SetValue(EditRoadProperty, value); }

    /// <summary>Con <see cref="EditRoad"/>: si possono spostare anche le linee tra le corsie (altrimenti solo i 4 vertici).</summary>
    public bool EditRoadDividers { get => (bool)GetValue(EditRoadDividersProperty); set => SetValue(EditRoadDividersProperty, value); }

    /// <summary>Riceve il nuovo <see cref="RoadLayout"/> alla fine di un trascinamento.</summary>
    public ICommand? RoadEditedCommand { get => (ICommand?)GetValue(RoadEditedCommandProperty); set => SetValue(RoadEditedCommandProperty, value); }

    public ImageSource? Source { get => (ImageSource?)GetValue(SourceProperty); set => SetValue(SourceProperty, value); }

    public double VideoWidth { get => (double)GetValue(VideoWidthProperty); set => SetValue(VideoWidthProperty, value); }

    public double VideoHeight { get => (double)GetValue(VideoHeightProperty); set => SetValue(VideoHeightProperty, value); }

    public LineSegment? LineA { get => (LineSegment?)GetValue(LineAProperty); set => SetValue(LineAProperty, value); }

    public LineSegment? LineB { get => (LineSegment?)GetValue(LineBProperty); set => SetValue(LineBProperty, value); }

    public IReadOnlyList<OverlayPoint>? Points { get => (IReadOnlyList<OverlayPoint>?)GetValue(PointsProperty); set => SetValue(PointsProperty, value); }

    public IReadOnlyList<Point2>? Markers { get => (IReadOnlyList<Point2>?)GetValue(MarkersProperty); set => SetValue(MarkersProperty, value); }

    public ViewportMode Mode { get => (ViewportMode)GetValue(ModeProperty); set => SetValue(ModeProperty, value); }

    /// <summary>Se vero, le estremità delle linee si possono trascinare.</summary>
    public bool EditLines { get => (bool)GetValue(EditLinesProperty); set => SetValue(EditLinesProperty, value); }

    /// <summary>Riceve un <see cref="LineEdit"/>.</summary>
    public ICommand? LineEditedCommand { get => (ICommand?)GetValue(LineEditedCommandProperty); set => SetValue(LineEditedCommandProperty, value); }

    /// <summary>Riceve un <see cref="Point2"/> in pixel del video.</summary>
    public ICommand? PointPlacedCommand { get => (ICommand?)GetValue(PointPlacedCommandProperty); set => SetValue(PointPlacedCommandProperty, value); }

    /// <summary>Riceve il punto cliccato nelle modalità PlaceRefA, PlaceRefB e PlaceVanishing.</summary>
    public ICommand? ReferencePlacedCommand { get => (ICommand?)GetValue(ReferencePlacedCommandProperty); set => SetValue(ReferencePlacedCommandProperty, value); }

    /// <summary>Riferimenti a terra A e B e punto di fuga (calibrazione con punto di fuga).</summary>
    public Point2? RefPointA { get => (Point2?)GetValue(RefPointAProperty); set => SetValue(RefPointAProperty, value); }

    public Point2? RefPointB { get => (Point2?)GetValue(RefPointBProperty); set => SetValue(RefPointBProperty, value); }

    public Point2? VanishingPoint { get => (Point2?)GetValue(VanishingPointProperty); set => SetValue(VanishingPointProperty, value); }

    /// <summary>Punto di fuga stimato da bordi e linee tra le corsie: pallino rosso, solo indicativo.</summary>
    public Point2? LaneVanishingPoint { get => (Point2?)GetValue(LaneVanishingPointProperty); set => SetValue(LaneVanishingPointProperty, value); }

    /// <summary>Riquadri dei veicoli rilevati (analisi automatica).</summary>
    public IReadOnlyList<OverlayBox>? Boxes { get => (IReadOnlyList<OverlayBox>?)GetValue(BoxesProperty); set => SetValue(BoxesProperty, value); }

    /// <summary>Zona analizzata dal rilevamento automatico.</summary>
    public BoundingBox? Region { get => (BoundingBox?)GetValue(RegionProperty); set => SetValue(RegionProperty, value); }

    public bool ShowRegion { get => (bool)GetValue(ShowRegionProperty); set => SetValue(ShowRegionProperty, value); }

    private static DependencyProperty Register(string name, Type type, object? defaultValue) =>
        DependencyProperty.Register(name, type, typeof(VideoViewport), new PropertyMetadata(defaultValue, OnAnyChanged));

    private static void OnAnyChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var v = (VideoViewport)d;
        if (e.Property == SourceProperty)
        {
            v.FrameImage.Source = (ImageSource?)e.NewValue;
            return; // le sovrapposizioni non cambiano col fotogramma
        }

        if (e.Property == VideoWidthProperty || e.Property == VideoHeightProperty)
        {
            v.Surface.Width = v.VideoWidth;
            v.Surface.Height = v.VideoHeight;
            v.ResetZoom();
        }

        if (e.Property == ModeProperty)
        {
            v._drawStart = null;
            v._drawPressed = false;
            if (v._dragLine is null && !v._panning)
            {
                v.Root.ReleaseMouseCapture();
            }

            v.UpdateCursorAndHint();
        }

        v.Redraw();
    }

    // ───────────── Zoom ─────────────

    public void ResetZoom()
    {
        _zoom.Matrix = Matrix.Identity;
        UpdateZoomBadge();
        Redraw();
    }

    private void OnResetZoomClick(object sender, RoutedEventArgs e) => ResetZoom();

    private void OnWheel(object sender, MouseWheelEventArgs e)
    {
        Point at = e.GetPosition(Root);
        double factor = e.Delta > 0 ? 1.25 : 1 / 1.25;
        Matrix m = _zoom.Matrix;
        double newScale = Math.Clamp(m.M11 * factor, 1, MaxZoom);
        factor = newScale / m.M11;
        if (Math.Abs(factor - 1) < 1e-6)
        {
            return;
        }

        m.ScaleAt(factor, factor, at.X, at.Y);
        if (newScale <= 1.0001)
        {
            m = Matrix.Identity;
        }

        _zoom.Matrix = m;
        UpdateZoomBadge();
        Redraw();
        e.Handled = true;
    }

    private void OnPanDown(object sender, MouseButtonEventArgs e)
    {
        if (_zoom.Matrix.M11 <= 1.0001)
        {
            return;
        }

        _panning = true;
        _panLast = e.GetPosition(Root);
        Root.CaptureMouse();
        Root.Cursor = Cursors.SizeAll;
        e.Handled = true;
    }

    private void OnPanUp(object sender, MouseButtonEventArgs e)
    {
        if (!_panning)
        {
            return;
        }

        _panning = false;
        Root.ReleaseMouseCapture();
        UpdateCursorAndHint();
        e.Handled = true;
    }

    private void OnMiddleDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Middle)
        {
            OnPanDown(sender, e);
        }
    }

    private void OnMiddleUp(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Middle)
        {
            OnPanUp(sender, e);
        }
    }

    private void UpdateZoomBadge()
    {
        double z = _zoom.Matrix.M11;
        ZoomBadge.Visibility = z > 1.0001 ? Visibility.Visible : Visibility.Collapsed;
        ZoomText.Text = Loc.S($"Zoom {z:0.#}×  ·  tasto destro per spostare");
    }

    /// <summary>Pixel dello schermo per pixel del video (zoom compreso).</summary>
    private double ScreenScale
    {
        get
        {
            if (VideoWidth <= 0 || VideoHeight <= 0 || Root.ActualWidth <= 0 || Root.ActualHeight <= 0)
            {
                return 1;
            }

            double fit = Math.Min(Root.ActualWidth / VideoWidth, Root.ActualHeight / VideoHeight);
            return Math.Max(1e-6, fit * _zoom.Matrix.M11);
        }
    }

    // ───────────── Mouse ─────────────

    private Point2 ToVideo(MouseEventArgs e)
    {
        Point p = e.GetPosition(Surface);
        return new Point2(p.X, p.Y);
    }

    private bool InsideVideo(Point2 p) => p.X >= 0 && p.Y >= 0 && p.X <= VideoWidth && p.Y <= VideoHeight;

    private void OnLeftDown(object sender, MouseButtonEventArgs e)
    {
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
                _drawStartScreen = e.GetPosition(Root);
                _drawPressed = true;
                Root.CaptureMouse();
            }
            else
            {
                FinishDrawing(p);
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
            Root.CaptureMouse();
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
            Root.CaptureMouse();
            e.Handled = true;
            return;
        }

        // Riferimenti A e B nell'immagine; il punto di fuga anche fuori (spesso è sopra l'inquadratura).
        if (Mode is ViewportMode.PlaceRefA or ViewportMode.PlaceRefB or ViewportMode.PlaceVanishing
            && (Mode == ViewportMode.PlaceVanishing || InsideVideo(p)))
        {
            ICommand? rc = ReferencePlacedCommand;
            if (rc?.CanExecute(p) == true)
            {
                rc.Execute(p);
            }

            e.Handled = true;
            return;
        }

        if (Mode == ViewportMode.PlacePoint && InsideVideo(p))
        {
            ICommand? cmd = PointPlacedCommand;
            if (cmd?.CanExecute(p) == true)
            {
                cmd.Execute(p);
            }

            e.Handled = true;
        }
    }

    private void OnLeftUp(object sender, MouseButtonEventArgs e)
    {
        Point2 p = ToVideo(e);
        if (_drawStart is not null && _drawPressed)
        {
            _drawPressed = false;
            Point now = e.GetPosition(Root);
            bool dragged = (now - _drawStartScreen).Length > 6;
            if (dragged)
            {
                FinishDrawing(p); // disegno con trascinamento
            }

            // altrimenti si attende il secondo clic
            return;
        }

        if (_roadDrag is not null && _roadPreview is { } edited)
        {
            _roadDrag = null;
            _roadPreview = null;
            Root.ReleaseMouseCapture();
            Execute(RoadEditedCommand, edited);
            Redraw();
            return;
        }

        if (_dragLine is { } line && _dragPreview is { } seg)
        {
            _dragLine = null;
            _dragPreview = null;
            Root.ReleaseMouseCapture();
            Execute(LineEditedCommand, new LineEdit(line, seg));
            Redraw();
        }
    }

    private void FinishDrawing(Point2 end)
    {
        if (_drawStart is not { } start)
        {
            return;
        }

        _drawStart = null;
        _drawPressed = false;
        Root.ReleaseMouseCapture();
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

    private void OnMouseMove(object sender, MouseEventArgs e)
    {
        if (_panning)
        {
            Point now = e.GetPosition(Root);
            Vector delta = now - _panLast;
            _panLast = now;
            Matrix m = _zoom.Matrix;
            m.Translate(delta.X, delta.Y);
            _zoom.Matrix = m;
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
            Root.Cursor = HitTestRoad(p, road) switch
            {
                0 or (>= 1 and <= 4) => Cursors.SizeAll,
                >= 5 and <= 8 => Cursors.Hand,
                >= 20 => Cursors.Hand,
                >= 10 => Cursors.SizeNS,
                _ => Cursors.Arrow,
            };
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
            Root.Cursor = HitTestLines(p) is { } h ? (h.End == 2 ? Cursors.SizeAll : Cursors.Hand) : Cursors.Arrow;
        }

        Redraw();
    }

    private Point2 ClampToVideo(Point2 p) => new(Math.Clamp(p.X, 0, VideoWidth), Math.Clamp(p.Y, 0, VideoHeight));

    /// <summary>Parte della strada sotto il mouse (vedi <see cref="_roadDrag"/>), o null.</summary>
    private int? HitTestRoad(Point2 p, RoadLayout road)
    {
        double s = ScreenScale;
        bool Near(double d) => d * s <= HandleHitPixels;

        // Vertici ed estremità delle linee, poi le linee tra le corsie, poi i lati, poi l'interno.
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
            if (Near(Point2.Distance(p, l))) return 20 + i;
            if (Near(Point2.Distance(p, r))) return 30 + i;
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

    /// <summary>Strada durante il trascinamento della parte indicata, con il mouse in <paramref name="to"/>.</summary>
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
        Cursor cursor = Mode == ViewportMode.None ? Cursors.Arrow : Cursors.Cross;
        Root.Cursor = cursor;
        HintText.Text = hint ?? string.Empty;
        HintBadge.Visibility = hint is null ? Visibility.Collapsed : Visibility.Visible;
    }

    // ───────────── Disegno delle sovrapposizioni ─────────────

    private void Redraw()
    {
        Overlay.Children.Clear();
        double inv = 1 / ScreenScale;

        LineSegment? a = _dragLine == DetectionLine.A ? _dragPreview : LineA;
        LineSegment? b = _dragLine == DetectionLine.B ? _dragPreview : LineB;

        if (a is { } la && b is { } lb)
        {
            DrawZone(la, lb);
        }

        if (a is { } l1)
        {
            DrawDetectionLine(l1, ColorA, "A", inv);
        }

        if (b is { } l2)
        {
            DrawDetectionLine(l2, ColorB, "B", inv);
        }

        DrawRoad(_roadPreview ?? Road, inv);
        DrawTrack(inv);
        DrawMarkers(inv);
        DrawRegion(inv);
        DrawBoxes(inv);

        if (_drawStart is { } ds && _mouse is { } m)
        {
            Color c = Mode == ViewportMode.DrawLineA ? ColorA : ColorB;
            AddLine(ds, m, new SolidColorBrush(c), 2 * inv, dashed: true);
            AddDot(ds, 5 * inv, new SolidColorBrush(c), Brushes.White, 1.5 * inv);
        }

        DrawVanishing(inv);
        DrawLaneVanishing(inv);

        if (Mode is ViewportMode.PlacePoint or ViewportMode.PlaceRefA or ViewportMode.PlaceRefB && _mouse is { } cross && _dragLine is null)
        {
            var guide = new SolidColorBrush(Color.FromArgb(110, 255, 255, 255));
            AddLine(new Point2(0, cross.Y), new Point2(VideoWidth, cross.Y), guide, 1 * inv, dashed: false);
            AddLine(new Point2(cross.X, 0), new Point2(cross.X, VideoHeight), guide, 1 * inv, dashed: false);
            AddDot(cross, 8 * inv, null, new SolidColorBrush(ColorPoint), 1.5 * inv);
        }
    }

    /// <summary>
    /// Calibrazione con punto di fuga: i riferimenti A e B, il punto di fuga e le rette che li uniscono.
    /// Mentre si sceglie il punto di fuga le rette seguono il mouse.
    /// </summary>
    /// <summary>
    /// Punto di fuga stimato dalle corsie: pallino rosso con il bordo bianco e una scritta accanto. Se cade fuori
    /// dall'immagine (di solito molto sopra), una freccia rossa sul bordo indica da che parte si trova.
    /// </summary>
    private void DrawLaneVanishing(double inv)
    {
        if (LaneVanishingPoint is not { } v)
        {
            return;
        }

        var red = new SolidColorBrush(ColorLaneVanishing);
        string text = Loc.T("Punto di fuga stimato");
        Point2 at = v;
        if (v.X < 0 || v.X > VideoWidth || v.Y < 0 || v.Y > VideoHeight)
        {
            // Freccia sul bordo verso il punto: punta nella direzione del punto vero.
            double margin = 24 * inv;
            var center = new Point2(VideoWidth / 2, VideoHeight / 2);
            Point2 d = v - center;
            double tx = Math.Abs(d.X) < 1e-9 ? double.PositiveInfinity : ((VideoWidth / 2) - margin) / Math.Abs(d.X);
            double ty = Math.Abs(d.Y) < 1e-9 ? double.PositiveInfinity : ((VideoHeight / 2) - margin) / Math.Abs(d.Y);
            at = center + (d * Math.Min(tx, ty));
            Point2 dir = d * (1 / d.Length);
            Point2 side = new Point2(-dir.Y, dir.X) * (9 * inv);
            Point2 tip = at + (dir * (12 * inv)), back = at - (dir * (8 * inv));
            Overlay.Children.Add(new Polygon
            {
                Points = new PointCollection([new Point(tip.X, tip.Y), new Point(back.X + side.X, back.Y + side.Y), new Point(back.X - side.X, back.Y - side.Y)]),
                Fill = red,
                Stroke = Brushes.White,
                StrokeThickness = 2 * inv,
                StrokeLineJoin = PenLineJoin.Round,
            });
            text = Loc.T("Punto di fuga stimato (fuori dall'immagine)");
        }
        else
        {
            AddDot(v, 13 * inv, null, new SolidColorBrush(Color.FromArgb(150, 0, 0, 0)), 3 * inv);
            AddDot(v, 8 * inv, red, Brushes.White, 2.5 * inv);
        }

        var label = new TextBlock
        {
            Text = text,
            FontSize = 12 * inv,
            FontWeight = FontWeights.SemiBold,
            Foreground = Brushes.White,
            Background = new SolidColorBrush(Color.FromArgb(200, 0, 0, 0)),
            Padding = new Thickness(5 * inv, 2 * inv, 5 * inv, 2 * inv),
        };
        Canvas.SetLeft(label, at.X + (14 * inv));
        Canvas.SetTop(label, at.Y - (10 * inv));
        Overlay.Children.Add(label);
    }

    private void DrawVanishing(double inv)
    {
        Point2? v = Mode == ViewportMode.PlaceVanishing ? _mouseAnywhere : VanishingPoint;
        var white = new SolidColorBrush(Color.FromArgb(220, 255, 255, 255));
        // Linee guida bianche dal punto di fuga: verticale, a metà tra A e B, e a ventaglio prima di A e dopo B
        // (mezza, una e due distanze A–B); le più esterne escono dai lati dell'immagine.
        if (v is { } fv && RefPointA is { } ga && RefPointB is { } gb)
        {
            Point2 ab = gb - ga;
            Point2[] through =
            [
                new Point2(fv.X, fv.Y + 1),        // verticale verso il basso
                Point2.Lerp(ga, gb, 0.5),          // a metà tra A e B
                ga - (ab * 0.5),                   // prima di A: mezza distanza A–B
                ga - ab,                           // una distanza A–B
                ga - (ab * 2),                     // due distanze A–B
                gb + (ab * 0.5),                   // dopo B: mezza distanza A–B
                gb + ab,                           // una distanza A–B
                gb + (ab * 2),                     // due distanze A–B
            ];
            var outline = new SolidColorBrush(Color.FromArgb(120, 0, 0, 0));
            var guide = new SolidColorBrush(Color.FromArgb(230, 255, 255, 255));
            foreach (Point2 t in through)
            {
                if (Point2.Distance(t, fv) < 1e-6)
                {
                    continue;
                }

                Point2 end = ExitFromVideo(fv, t);
                AddLine(fv, end, outline, 3.5 * inv, dashed: false);
                AddLine(fv, end, guide, 1.5 * inv, dashed: false);
            }
        }

        foreach ((Point2? r, Color c) in new[] { (RefPointA, ColorA), (RefPointB, ColorB) })
        {
            if (r is { } rp && v is { } vp)
            {
                // Dal punto di fuga attraverso il riferimento, fino al bordo del video (di solito quello inferiore).
                Point2 end = BeyondToEdge(vp, rp);
                AddLine(vp, end, new SolidColorBrush(Color.FromArgb(140, 0, 0, 0)), 4 * inv, dashed: false);
                AddLine(vp, end, new SolidColorBrush(c), 2 * inv, dashed: true);
            }
        }

        if (v is { } vv && (RefPointA is not null || RefPointB is not null))
        {
            AddDot(vv, 7 * inv, white, new SolidColorBrush(Color.FromRgb(0xFF, 0xB5, 0x47)), 2.5 * inv);
        }

        if (RefPointA is { } a)
        {
            AddDot(a, 7 * inv, new SolidColorBrush(ColorA), Brushes.White, 2 * inv);
            AddPointLabel(a, "A", ColorA, inv);
        }

        if (RefPointB is { } b)
        {
            AddDot(b, 7 * inv, new SolidColorBrush(ColorB), Brushes.White, 2 * inv);
            AddPointLabel(b, "B", ColorB, inv);
        }
    }

    /// <summary>
    /// Punto in cui la semiretta da <paramref name="from"/> verso <paramref name="through"/> lascia il video
    /// (il più lontano sul bordo), anche se <paramref name="through"/> è fuori dall'immagine; se la semiretta
    /// non attraversa il video, <paramref name="through"/>.
    /// </summary>
    private Point2 ExitFromVideo(Point2 from, Point2 through)
    {
        Point2 d = through - from;
        double best = double.NegativeInfinity;
        void Try(double t)
        {
            if (t > 0 && t > best)
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

    /// <summary>
    /// Punto in cui la semiretta da <paramref name="from"/> attraverso <paramref name="through"/> esce dal video,
    /// oltre <paramref name="through"/>; se non esce (punti coincidenti), <paramref name="through"/>.
    /// </summary>
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

    private void AddPointLabel(Point2 p, string text, Color color, double inv)
    {
        var label = new TextBlock
        {
            Text = text,
            FontSize = 13 * inv,
            FontWeight = FontWeights.Bold,
            Foreground = Brushes.Black,
            Background = new SolidColorBrush(color),
            Padding = new Thickness(5 * inv, 0, 5 * inv, 0),
        };
        Canvas.SetLeft(label, p.X + (10 * inv));
        Canvas.SetTop(label, p.Y - (22 * inv));
        Overlay.Children.Add(label);
    }

    private void DrawZone(LineSegment a, LineSegment b)
    {
        // Ordina i vertici per evitare un poligono "a farfalla".
        bool crossed = GeometryMath.SegmentsIntersect(new LineSegment(a.Start, b.Start), new LineSegment(a.End, b.End));
        Point2 b1 = crossed ? b.End : b.Start;
        Point2 b2 = crossed ? b.Start : b.End;
        var poly = new Polygon
        {
            Points = [ToPoint(a.Start), ToPoint(a.End), ToPoint(b2), ToPoint(b1)],
            Fill = new SolidColorBrush(Color.FromArgb(28, 255, 255, 255)),
        };
        Overlay.Children.Add(poly);
    }

    private void DrawDetectionLine(LineSegment l, Color color, string label, double inv)
    {
        var brush = new SolidColorBrush(color);
        AddLine(l.Start, l.End, new SolidColorBrush(Color.FromArgb(140, 0, 0, 0)), 6 * inv, dashed: false);
        AddLine(l.Start, l.End, brush, 3 * inv, dashed: false);

        if (EditLines)
        {
            AddDot(l.Start, 7 * inv, Brushes.White, brush, 2.5 * inv);
            AddDot(l.End, 7 * inv, Brushes.White, brush, 2.5 * inv);
        }

        // Etichetta accanto all'estremo iniziale.
        var text = new TextBlock
        {
            Text = label,
            FontSize = 14 * inv,
            FontWeight = FontWeights.Bold,
            Foreground = Brushes.Black,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        double size = 24 * inv;
        var badge = new Border
        {
            Width = size,
            Height = size,
            CornerRadius = new CornerRadius(size / 2),
            Background = brush,
            BorderBrush = Brushes.Black,
            BorderThickness = new Thickness(1 * inv),
            Child = text,
        };
        Point2 dir = l.IsDegenerate ? new Point2(1, 0) : l.Direction * (1 / l.Length);
        Point2 pos = l.Start - dir * (20 * inv);
        Canvas.SetLeft(badge, pos.X - size / 2);
        Canvas.SetTop(badge, pos.Y - size / 2);
        Overlay.Children.Add(badge);
    }

    /// <summary>Strada: quadrilatero, linee tra le corsie e numero delle corsie (1 = la più vicina).</summary>
    private void DrawRoad(RoadLayout? road, double inv)
    {
        if (road is null)
        {
            return;
        }

        var brush = new SolidColorBrush(ColorRoad);
        var poly = new Polygon
        {
            Points = new PointCollection(road.Corners.Select(c => new Point(c.X, c.Y))),
            Stroke = brush,
            StrokeThickness = (EditRoad ? 3 : 1.5) * inv,
            StrokeLineJoin = PenLineJoin.Round,
            Fill = new SolidColorBrush(Color.FromArgb(EditRoad ? (byte)36 : (byte)14, ColorRoad.R, ColorRoad.G, ColorRoad.B)),
        };
        if (!EditRoad)
        {
            poly.StrokeDashArray = [6, 4];
        }

        Overlay.Children.Add(poly);

        var white = new SolidColorBrush(Color.FromRgb(0xFF, 0xFF, 0xFF));
        for (int i = 0; i < road.Dividers.Count; i++)
        {
            (Point2 l, Point2 r) = road.DividerEnds(i);
            AddLine(l, r, new SolidColorBrush(Color.FromArgb(150, 0, 0, 0)), 5 * inv, dashed: false);
            AddLine(l, r, white, 2.5 * inv, dashed: true);
            if (EditRoad && EditRoadDividers)
            {
                // Estremità: si spostano una per volta; il punto centrale sposta tutta la linea.
                // Finché nessuna linea è stata spostata, i pallini lampeggiano per indicare cosa trascinare.
                if (road.DividersAtStart)
                {
                    AddBlinkDot(l, 6, inv);
                    AddBlinkDot(r, 6, inv);
                    AddBlinkDot(Point2.Lerp(l, r, 0.5), 5, inv);
                }
                else
                {
                    AddDot(l, 6 * inv, white, brush, 2.5 * inv);
                    AddDot(r, 6 * inv, white, brush, 2.5 * inv);
                    AddDot(Point2.Lerp(l, r, 0.5), 5 * inv, brush, white, 2 * inv);
                }
            }
        }

        if (EditRoad)
        {
            // Vertici non ancora spostati: lampeggiano; quelli già spostati sono bianchi con il contorno verde.
            bool[] atStart = road.CornersAtStart(VideoWidth, VideoHeight);
            for (int i = 0; i < road.Corners.Count; i++)
            {
                if (atStart[i] && !EditRoadDividers)
                {
                    AddBlinkDot(road.Corners[i], 8, inv);
                }
                else
                {
                    AddDot(road.Corners[i], 8 * inv, Brushes.White, brush, 3 * inv);
                }
            }
        }

        for (int lane = 0; lane < road.Lanes; lane++)
        {
            Point2 center = road.LaneCenter(lane);
            var label = new TextBlock
            {
                Text = Loc.S($"Corsia {lane + 1}"),
                FontSize = 12 * inv,
                FontWeight = FontWeights.SemiBold,
                Foreground = Brushes.Black,
                Background = brush,
                Padding = new Thickness(5 * inv, 1 * inv, 5 * inv, 1 * inv),
            };
            Canvas.SetLeft(label, center.X - (28 * inv));
            Canvas.SetTop(label, center.Y - (9 * inv));
            Overlay.Children.Add(label);
        }
    }

    private void DrawTrack(double inv)
    {
        IReadOnlyList<OverlayPoint>? pts = Points;
        if (pts is null || pts.Count == 0)
        {
            return;
        }

        var brush = new SolidColorBrush(ColorPoint);
        if (pts.Count > 1)
        {
            var line = new Polyline
            {
                Stroke = new SolidColorBrush(Color.FromArgb(180, ColorPoint.R, ColorPoint.G, ColorPoint.B)),
                StrokeThickness = 1.5 * inv,
                StrokeLineJoin = PenLineJoin.Round,
            };
            foreach (OverlayPoint p in pts)
            {
                line.Points.Add(ToPoint(p.Position));
            }

            Overlay.Children.Add(line);
        }

        foreach (OverlayPoint p in pts)
        {
            if (p.IsCurrentFrame)
            {
                AddDot(p.Position, 9 * inv, null, Brushes.White, 2 * inv);
                AddDot(p.Position, 5 * inv, brush, Brushes.White, 1 * inv);
            }
            else
            {
                AddDot(p.Position, 3.5 * inv, brush, null, 0);
            }
        }
    }

    private void DrawRegion(double inv)
    {
        if (!ShowRegion || Region is not { } r)
        {
            return;
        }

        var rect = new Rectangle
        {
            Width = r.Width,
            Height = r.Height,
            Stroke = new SolidColorBrush(Color.FromArgb(170, 255, 255, 255)),
            StrokeThickness = 1.5 * inv,
            StrokeDashArray = [6, 4],
        };
        Canvas.SetLeft(rect, r.X);
        Canvas.SetTop(rect, r.Y);
        Overlay.Children.Add(rect);

        var label = new TextBlock
        {
            Text = Loc.T("Zona analizzata"),
            FontSize = 12 * inv,
            Foreground = Brushes.White,
            Background = new SolidColorBrush(Color.FromArgb(150, 0, 0, 0)),
            Padding = new Thickness(4 * inv, 1 * inv, 4 * inv, 1 * inv),
        };
        Canvas.SetLeft(label, r.X + 4 * inv);
        Canvas.SetTop(label, r.Y + 4 * inv);
        Overlay.Children.Add(label);
    }

    private void DrawBoxes(double inv)
    {
        IReadOnlyList<OverlayBox>? boxes = Boxes;
        if (boxes is null)
        {
            return;
        }

        foreach (OverlayBox b in boxes)
        {
            Color c = b.Confirmed ? ColorMarker : Color.FromRgb(0xC8, 0xCC, 0xD4);
            var rect = new Rectangle
            {
                Width = Math.Max(1, b.Box.Width),
                Height = Math.Max(1, b.Box.Height),
                Stroke = new SolidColorBrush(c),
                StrokeThickness = 2 * inv,
            };
            if (!b.Confirmed)
            {
                rect.StrokeDashArray = [3, 2];
            }

            Canvas.SetLeft(rect, b.Box.X);
            Canvas.SetTop(rect, b.Box.Y);
            Overlay.Children.Add(rect);

            // Punto di riferimento usato per la misura: vertice basso sul davanti (verso noto), altrimenti
            // centro del lato inferiore; nella calibrazione i due vertici bassi, perni delle linee A e B.
            Point2[] refs = b.Heading switch
            {
                > 1 => [b.Box.BottomLeft, b.Box.BottomRight],
                1 => [b.Box.BottomRight],
                < 0 => [b.Box.BottomLeft],
                _ => [b.Box.BottomCenter],
            };
            foreach (Point2 r in refs)
            {
                AddDot(r, 4.5 * inv, new SolidColorBrush(ColorPoint), Brushes.White, 1.2 * inv);
            }

            var label = new TextBlock
            {
                Text = b.Label,
                FontSize = 12 * inv,
                Foreground = Brushes.Black,
                Background = new SolidColorBrush(c),
                Padding = new Thickness(4 * inv, 1 * inv, 4 * inv, 1 * inv),
            };
            Canvas.SetLeft(label, b.Box.X);
            Canvas.SetTop(label, b.Box.Y - 18 * inv);
            Overlay.Children.Add(label);
        }
    }

    private void DrawMarkers(double inv)
    {
        IReadOnlyList<Point2>? markers = Markers;
        if (markers is null)
        {
            return;
        }

        foreach (Point2 m in markers)
        {
            double r = 7 * inv;
            var diamond = new Polygon
            {
                Points = [new Point(m.X, m.Y - r), new Point(m.X + r, m.Y), new Point(m.X, m.Y + r), new Point(m.X - r, m.Y)],
                Fill = new SolidColorBrush(ColorMarker),
                Stroke = Brushes.Black,
                StrokeThickness = 1 * inv,
            };
            Overlay.Children.Add(diamond);
        }
    }

    private void AddLine(Point2 a, Point2 b, Brush stroke, double thickness, bool dashed)
    {
        var line = new Line
        {
            X1 = a.X,
            Y1 = a.Y,
            X2 = b.X,
            Y2 = b.Y,
            Stroke = stroke,
            StrokeThickness = thickness,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
        };
        if (dashed)
        {
            line.StrokeDashArray = [4, 3];
        }

        Overlay.Children.Add(line);
    }

    private void AddDot(Point2 c, double radius, Brush? fill, Brush? stroke, double thickness)
    {
        var e = new Ellipse
        {
            Width = radius * 2,
            Height = radius * 2,
            Fill = fill,
            Stroke = stroke,
            StrokeThickness = thickness,
        };
        Canvas.SetLeft(e, c.X - radius);
        Canvas.SetTop(e, c.Y - radius);
        Overlay.Children.Add(e);
    }

    /// <summary>Durata di un lampeggio.</summary>
    private static readonly Duration BlinkHalfPeriod = new(TimeSpan.FromMilliseconds(550));

    /// <summary>Pallino da spostare: verde pieno che si attenua, con un anello che si allarga (animazioni di WPF).</summary>
    private void AddBlinkDot(Point2 c, double radius, double inv)
    {
        double r = radius * inv;
        var ring = new Ellipse
        {
            Width = (r + (3 * inv)) * 2,
            Height = (r + (3 * inv)) * 2,
            Stroke = new SolidColorBrush(ColorRoad),
            StrokeThickness = 2.5 * inv,
            RenderTransformOrigin = new Point(0.5, 0.5),
            RenderTransform = new ScaleTransform(),
            IsHitTestVisible = false,
        };
        Canvas.SetLeft(ring, c.X - (ring.Width / 2));
        Canvas.SetTop(ring, c.Y - (ring.Height / 2));
        Overlay.Children.Add(ring);
        double grow = 1 + (9 * inv / (r + (3 * inv)));
        var scale = new DoubleAnimation(1, grow, BlinkHalfPeriod) { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever };
        ring.RenderTransform.BeginAnimation(ScaleTransform.ScaleXProperty, scale);
        ring.RenderTransform.BeginAnimation(ScaleTransform.ScaleYProperty, scale);
        ring.BeginAnimation(OpacityProperty, new DoubleAnimation(0.85, 0, BlinkHalfPeriod) { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever });

        var dot = new Ellipse
        {
            Width = r * 2,
            Height = r * 2,
            Fill = new SolidColorBrush(ColorRoad),
            Stroke = Brushes.White,
            StrokeThickness = 2.5 * inv,
            IsHitTestVisible = false,
        };
        Canvas.SetLeft(dot, c.X - r);
        Canvas.SetTop(dot, c.Y - r);
        Overlay.Children.Add(dot);
        dot.BeginAnimation(OpacityProperty, new DoubleAnimation(1, 0.4, BlinkHalfPeriod) { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever });
    }

    private static Point ToPoint(Point2 p) => new(p.X, p.Y);
}
