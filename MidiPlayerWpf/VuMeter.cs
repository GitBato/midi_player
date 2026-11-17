using System.Globalization;
using System.Windows;
using System.Windows.Media;

namespace MidiPlayerWpf;

/// <summary>
/// アナログの VU メーター（1 チャンネル分）。針はばねとダンパーで動かすので、
/// 音が来ると勢いよく振れて少し行き過ぎ、揺れながら落ち着く。
/// </summary>
sealed class VuMeter : FrameworkElement
{
    /// <summary>針が向かう値（0〜1）</summary>
    public double Target;
    /// <summary>チャンネル番号（表示用、1〜16）</summary>
    public int Channel;
    /// <summary>送っている出口の色（文字盤の下の帯）</summary>
    public Brush Accent = Brushes.LightGreen;

    double _x;   // 針の位置（0〜1）
    double _v;   // 針の速さ

    // 上がるとき：ばねの強さとダンパー（減衰比 0.5 くらい：勢いよく振れて少し行き過ぎる）
    const double Spring = 220, Damper = 15;
    // 戻るとき：弱いばねで、ゆっくり落ちる（減衰比 0.75 くらい）
    const double FallSpring = 60, FallDamper = 11.5;
    // 針の振れる角度（真上が 0 度）
    const double MinDeg = -48, MaxDeg = 48;

    static readonly Brush Face = Freeze(new LinearGradientBrush(Color.FromRgb(250, 240, 205), Color.FromRgb(228, 212, 165), 90));
    static readonly Brush Ink = Freeze(new SolidColorBrush(Color.FromRgb(40, 34, 28)));
    static readonly Brush Red = Freeze(new SolidColorBrush(Color.FromRgb(200, 40, 30)));
    static readonly Brush Needle = Freeze(new SolidColorBrush(Color.FromRgb(20, 20, 20)));
    static readonly Brush Bezel = Freeze(new SolidColorBrush(Color.FromRgb(70, 74, 80)));
    static readonly Pen InkPen = FreezePen(new Pen(Ink, 1));
    static readonly Pen RedPen = FreezePen(new Pen(Red, 2.5));
    static readonly Pen NeedlePen = FreezePen(new Pen(Needle, 1.6) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round });
    static readonly Pen BezelPen = FreezePen(new Pen(Bezel, 2));

    static Brush Freeze(Brush b) { b.Freeze(); return b; }
    static Pen FreezePen(Pen p) { p.Freeze(); return p; }

    /// <summary>針を dt 秒ぶん動かす。動いたら true</summary>
    public bool Step(double dt)
    {
        dt = Math.Min(dt, 0.05);
        double before = _x;
        // 細かく刻んで計算する（大きな dt でも暴れないように）
        int n = Math.Max(1, (int)Math.Ceiling(dt / 0.004));
        double h = dt / n;
        for (int i = 0; i < n; i++)
        {
            bool rising = Target > _x;
            double a = rising ? Spring * (Target - _x) - Damper * _v : FallSpring * (Target - _x) - FallDamper * _v;
            _v += a * h;
            _x += _v * h;
            // 右端・左端のピンに当たったら跳ね返る
            if (_x > 1.06) { _x = 1.06; _v = -_v * 0.35; }
            if (_x < -0.03) { _x = -0.03; _v = -_v * 0.35; }
        }
        bool moved = Math.Abs(_x - before) > 0.0005;
        if (moved) InvalidateVisual();
        return moved;
    }

    Point At(Point pivot, double r, double value)
    {
        double deg = MinDeg + (MaxDeg - MinDeg) * value;
        double rad = deg * Math.PI / 180;
        return new Point(pivot.X + r * Math.Sin(rad), pivot.Y - r * Math.Cos(rad));
    }

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        if (w < 10 || h < 10) return;
        var face = new Rect(1, 1, w - 2, h - 2);
        dc.DrawRoundedRectangle(Face, BezelPen, face, 4, 4);

        // 針の付け根は文字盤の下の外
        var pivot = new Point(w / 2, h * 1.02);
        double r = Math.Min(h * 0.82, w * 0.62);

        // 目盛りの弧（0.75 から先は赤）
        DrawArc(dc, pivot, r, 0, 0.75, InkPen);
        DrawArc(dc, pivot, r, 0.75, 1, RedPen);
        for (int i = 0; i <= 10; i++)
        {
            double v = i / 10.0;
            double len = i % 5 == 0 ? r * 0.12 : r * 0.07;
            dc.DrawLine(v > 0.75 ? RedPen : InkPen, At(pivot, r, v), At(pivot, r + len, v));
        }

        // 文字（チャンネル番号と VU）
        double dip = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        var ch = new FormattedText(Channel.ToString(), CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
            new Typeface("Segoe UI Semibold"), Math.Max(h * 0.2, 8), Ink, dip);
        dc.DrawText(ch, new Point(5, 3));
        var vu = new FormattedText("VU", CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
            new Typeface("Georgia"), Math.Max(h * 0.16, 7), Ink, dip);
        dc.DrawText(vu, new Point(w / 2 - vu.Width / 2, h * 0.5));

        // 出口の色の帯
        dc.DrawRectangle(Accent, null, new Rect(4, h - 7, w - 8, 3));

        // 針（文字盤の中だけに描く）
        dc.PushClip(new RectangleGeometry(face, 4, 4));
        dc.DrawLine(NeedlePen, At(pivot, r * 0.18, _x), At(pivot, r * 1.08, _x));
        dc.Pop();
    }

    void DrawArc(DrawingContext dc, Point pivot, double r, double from, double to, Pen pen)
    {
        var g = new StreamGeometry();
        using (var c = g.Open())
        {
            c.BeginFigure(At(pivot, r, from), false, false);
            c.ArcTo(At(pivot, r, to), new Size(r, r), 0, false, SweepDirection.Clockwise, true, false);
        }
        g.Freeze();
        dc.DrawGeometry(null, pen, g);
    }
}
