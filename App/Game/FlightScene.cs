using System.Diagnostics;
using System.Windows;
using System.Windows.Media;
using Brush = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;
using Color = System.Windows.Media.Color;
using ColorConverter = System.Windows.Media.ColorConverter;
using Pen = System.Windows.Media.Pen;
using Point = System.Windows.Point;
using Rect = System.Windows.Rect;
using Size = System.Windows.Size;

namespace App.Game;

// Retained WPF vector drawing, composed by Direct3D. No bitmap assets, native
// DLLs, or software paint loop; Windows handles DPI and device-loss fallback.
public sealed class FlightScene : FrameworkElement, IDisposable
{
    private static readonly Brush Sky = Gradient("#081022", "#1C2C49", new Point(0, 0), new Point(0, 1));
    private static readonly Brush TrailFill = Gradient("#304FC6E8", "#004FC6E8", new Point(0, 0), new Point(0, 1));
    private static readonly Brush Body = Gradient("#FF7186", "#C9284B", new Point(0, 0), new Point(0, 1));
    private static readonly Geometry Fuselage = Shape("M -33,0 C -19,-7 14,-9 30,-4 Q 42,-1 33,3 C 18,9 -15,8 -30,4 Z");
    private static readonly Geometry Wing = Shape("M -7,0 L -19,24 Q -13,27 -8,24 L 15,2 Z");
    private static readonly Geometry FarWing = Shape("M -10,-3 L -18,-18 L -11,-19 L 9,-5 Z");
    private static readonly Geometry Tail = Shape("M -27,0 L -35,-14 L -29,-15 L -16,0 Z");
    private static readonly Geometry Cockpit = Shape("M 8,-6 Q 21,-8 25,-3 L 8,-2 Z");
    private static readonly Dictionary<string, Brush> Inks = new();
    private readonly Stopwatch _ambient = Stopwatch.StartNew();
    private readonly (double X, double Y, double Radius, double Phase)[] _stars;
    private bool _subscribed;
    private bool _disposed;
    private double _lastFrame;
    private decimal _seenCashout;
    private double _cashoutTime = -10;
    internal CrashGame? Game { get; set; }

    public FlightScene()
    {
        IsHitTestVisible = false;
        ClipToBounds = true;
        var random = new Random(481);
        _stars = Enumerable.Range(0, 42).Select(_ =>
            (random.NextDouble() * 328, random.NextDouble() * 133, 0.4 + random.NextDouble() * 0.9, random.NextDouble() * 6)).ToArray();
        Loaded += (_, _) => UpdateSubscription();
        Unloaded += (_, _) => StopRendering();
        IsVisibleChanged += (_, _) => UpdateSubscription();
    }

    private void UpdateSubscription()
    {
        if (!_disposed && IsLoaded && IsVisible && !_subscribed)
        {
            CompositionTarget.Rendering += RenderFrame;
            _subscribed = true;
        }
        else if (_disposed || !IsVisible) StopRendering();
    }

    private void StopRendering()
    {
        if (!_subscribed) return;
        CompositionTarget.Rendering -= RenderFrame;
        _subscribed = false;
    }

    private void RenderFrame(object? sender, EventArgs e)
    {
        double now = _ambient.Elapsed.TotalSeconds;
        if (now - _lastFrame < 1d / 60) return;
        _lastFrame = now;
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);
        if (ActualWidth <= 0 || ActualHeight <= 0) return;
        dc.PushTransform(new ScaleTransform(ActualWidth / 328, ActualHeight / 186));
        dc.DrawRectangle(Sky, null, new Rect(0, 0, 328, 186));
        double t = _ambient.Elapsed.TotalSeconds;
        var phase = Game?.Phase ?? FlightPhase.Boarding;
        bool flying = phase is FlightPhase.Flying or FlightPhase.Crashed;
        double elapsed = Game?.FlightSeconds ?? 0;
        if (phase == FlightPhase.Crashed) elapsed = CrashGame.SecondsFor(Game!.Multiplier);
        foreach (var star in _stars)
        {
            dc.PushOpacity(0.3 + 0.35 * (1 + Math.Sin(t * 0.7 + star.Phase)) / 2);
            dc.DrawEllipse(Ink("#B2C8F0"), null, new Point(star.X, star.Y), star.Radius, star.Radius);
            dc.Pop();
        }
        dc.DrawEllipse(Ink("#162641"), null, new Point(284, 30), 22, 22);
        dc.DrawEllipse(Ink("#5A7195"), null, new Point(284, 30), 9, 9);
        dc.DrawEllipse(Ink("#162641"), null, new Point(280, 27), 9, 9);

        DrawCloud(dc, (float)(360 - t * 4 % 440), 111, 1.15);
        DrawCloud(dc, (float)(190 - t * 2 % 430), 131, 0.75);
        DrawCloud(dc, (float)(460 - t * 3 % 550), 73, 0.6);
        DrawHorizon(dc, t, flying);

        if (flying)
        {
            var nose = Position(elapsed);
            var path = new StreamGeometry();
            using (var c = path.Open())
            {
                c.BeginFigure(new Point(25, 155), false, false);
                c.BezierTo(new Point(74, 159), new Point(nose.X - 42, nose.Y + 40), nose, true, false);
            }
            path.Freeze();
            var area = new StreamGeometry();
            using (var c = area.Open())
            {
                c.BeginFigure(new Point(25, 168), true, true);
                c.LineTo(new Point(25, 155), true, false);
                c.BezierTo(new Point(74, 159), new Point(nose.X - 42, nose.Y + 40), nose, true, false);
                c.LineTo(new Point(nose.X, 168), true, false);
            }
            area.Freeze();
            double fade = phase == FlightPhase.Crashed ? Math.Max(0.2, 1 - Game!.CrashAge * 0.5) : 1;
            dc.PushOpacity(fade);
            dc.DrawGeometry(TrailFill, null, area);
            dc.DrawGeometry(null, new Pen(Ink("#184FD1E8"), 12), path);
            dc.DrawGeometry(null, new Pen(Ink("#304FD1E8"), 6), path);
            dc.DrawGeometry(null, new Pen(Ink("#5EE3ED"), 2), path);
            dc.Pop();
            for (int i = 0; i < 4; i++)
            {
                double p = (t * 0.36 + i * 0.24) % 1;
                dc.DrawEllipse(Ink("#85EAF0"), null, BezierPoint(p, nose), 1.3, 1.3);
            }
            if (phase != FlightPhase.Crashed)
                DrawPlane(dc, nose, -10 + Math.Sin(t * 2) * 2, t);
            else DrawDeparture(dc, nose, Game!.CrashAge, t);
        }
        else
        {
            double progress = Game == null ? 0 : 1 - Game.BoardingLeft / CrashGame.BoardingSeconds;
            double taxi = 8 * progress;
            var nose = new Point(55 + taxi, 146 + Math.Sin(t * 2.4) * 1.3);
            dc.DrawEllipse(Ink("#24FF5C74"), null, new Point(nose.X, 165), 37, 4);
            DrawPlane(dc, nose, progress > 0.8 ? -3 : 0, t);
            if (Game != null)
            {
                dc.DrawRoundedRectangle(Ink("#25364F"), null, new Rect(103, 113, 122, 3), 1.5, 1.5);
                dc.DrawRoundedRectangle(Ink("#F8CD73"), null, new Rect(103, 113, 122 * progress, 3), 1.5, 1.5);
            }
        }

        if (Game?.CashedAt != _seenCashout)
        {
            _seenCashout = Game?.CashedAt ?? 0;
            if (_seenCashout > 0) _cashoutTime = t;
        }
        double celebration = t - _cashoutTime;
        if (celebration < 1.5)
        {
            dc.PushOpacity(Math.Max(0, 1 - celebration / 1.5));
            for (int i = 0; i < 22; i++)
            {
                double a = i * 2.399;
                var p = new Point(164 + Math.Cos(a) * celebration * (50 + i * 3),
                    75 + Math.Sin(a) * celebration * 55 + celebration * celebration * 25);
                dc.DrawEllipse(Ink(i % 2 == 0 ? "#72E6BA" : "#F8CD73"), null, p, 2, 1.3);
            }
            dc.Pop();
        }
        dc.Pop();
    }

    private static Point Position(double elapsed)
    {
        double progress = 1 - Math.Exp(-elapsed / 7.5);
        return new Point(63 + 220 * progress, 146 - 64 * progress + Math.Sin(elapsed * 1.4) * 2);
    }

    private static Point BezierPoint(double p, Point end)
    {
        double u = 1 - p;
        return new Point(u * u * u * 25 + 3 * u * u * p * 74 + 3 * u * p * p * (end.X - 42) + p * p * p * end.X,
            u * u * u * 155 + 3 * u * u * p * 159 + 3 * u * p * p * (end.Y + 40) + p * p * p * end.Y);
    }

    private static void DrawCloud(DrawingContext dc, double x, double y, double scale)
    {
        dc.PushTransform(new TranslateTransform(x, y));
        dc.PushTransform(new ScaleTransform(scale, scale));
        var ink = Ink("#142E496C");
        dc.DrawEllipse(ink, null, new Point(0, 0), 36, 6);
        dc.DrawEllipse(ink, null, new Point(-11, -4), 16, 8);
        dc.DrawEllipse(ink, null, new Point(9, -5), 20, 10);
        dc.Pop();
        dc.Pop();
    }

    private static void DrawHorizon(DrawingContext dc, double t, bool flying)
    {
        var hills = new StreamGeometry();
        using (var c = hills.Open())
        {
            c.BeginFigure(new Point(0, 167), true, true);
            c.BezierTo(new Point(58, 133), new Point(89, 174), new Point(139, 158), true, false);
            c.BezierTo(new Point(211, 123), new Point(229, 168), new Point(328, 145), true, false);
            c.LineTo(new Point(328, 186), true, false);
            c.LineTo(new Point(0, 186), true, false);
        }
        hills.Freeze();
        dc.DrawGeometry(Ink("#132039"), null, hills);
        dc.DrawRectangle(Ink("#0B1527"), null, new Rect(0, 169, 328, 17));
        dc.DrawLine(new Pen(Ink("#344D6B"), 1), new Point(0, 169), new Point(328, 169));
        double drift = t * (flying ? 65 : 12) % 47;
        for (int i = -1; i < 9; i++)
        {
            dc.DrawRectangle(Ink("#334661"), null, new Rect(i * 47 - drift, 177, 21, 2));
            dc.DrawEllipse(Ink("#8DAACC"), null, new Point(i * 47 - drift, 170), 1, 1);
        }
    }

    private static void DrawPlane(DrawingContext dc, Point center, double angle, double t)
    {
        dc.PushTransform(new TranslateTransform(center.X, center.Y));
        dc.PushTransform(new RotateTransform(angle));
        dc.PushTransform(new ScaleTransform(0.79, 0.79));
        dc.DrawGeometry(Ink("#97253E"), null, FarWing);
        dc.DrawGeometry(Body, null, Tail);
        dc.DrawGeometry(Body, new Pen(Ink("#FF9CAC"), 0.5), Fuselage);
        dc.DrawGeometry(Ink("#EA3C5B"), new Pen(Ink("#FF8FA4"), 0.6), Wing);
        dc.DrawGeometry(Ink("#A7E7EF"), null, Cockpit);
        dc.DrawLine(new Pen(Ink("#FFE5EA"), 1.5), new Point(-20, 2), new Point(-2, 2));
        dc.DrawEllipse(Ink("#FFD4DE"), null, new Point(-9, 21), 1.4, 1.4);
        dc.DrawEllipse(Ink("#FFF1CD"), null, new Point(34, 0), 2, 2);
        dc.PushOpacity(0.55);
        dc.DrawEllipse(Ink("#9AE6F2"), null, new Point(37, 0), 1.8, 8 + Math.Sin(t * 65) * 4);
        dc.Pop();
        dc.Pop();
        dc.Pop();
        dc.Pop();
    }

    private static void DrawDeparture(DrawingContext dc, Point nose, double age, double t)
    {
        if (age < 0.65)
        {
            dc.PushOpacity(1 - age / 0.65);
            DrawPlane(dc, new Point(nose.X + age * 510, nose.Y - age * 170), -22, t);
            dc.Pop();
        }
        if (age < 1.3)
        {
            dc.PushOpacity(Math.Max(0, 1 - age / 1.3));
            dc.DrawEllipse(null, new Pen(Ink("#E55A79"), 1.5), nose, 8 + age * 36, 8 + age * 36);
            for (int i = 0; i < 14; i++)
            {
                double a = i * 2.399;
                var start = new Point(nose.X + Math.Cos(a) * age * 53, nose.Y + Math.Sin(a) * age * 35);
                dc.DrawLine(new Pen(Ink("#FF87A0"), 1.3), start,
                    new Point(start.X + Math.Cos(a) * 5, start.Y + Math.Sin(a) * 5));
            }
            dc.Pop();
        }
    }

    private static Geometry Shape(string data)
    {
        var shape = Geometry.Parse(data);
        shape.Freeze();
        return shape;
    }

    private static Brush Ink(string hex)
    {
        if (!Inks.TryGetValue(hex, out var brush))
        {
            brush = (Brush)new BrushConverter().ConvertFromString(hex)!;
            brush.Freeze();
            Inks.Add(hex, brush);
        }
        return brush;
    }

    private static Brush Gradient(string from, string to, Point start, Point end)
    {
        var brush = new LinearGradientBrush((Color)ColorConverter.ConvertFromString(from), (Color)ColorConverter.ConvertFromString(to), start, end);
        brush.Freeze();
        return brush;
    }

    public void Dispose()
    {
        _disposed = true;
        StopRendering();
    }
}
