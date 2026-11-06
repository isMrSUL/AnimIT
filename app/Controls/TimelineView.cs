using System;
using System.Collections.Generic;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;

namespace AnimIT.Controls;

/// <summary>one block as the timeline draws it</summary>
public sealed record TimelineBlock(int Start, int Length, string Name, string AttackerLabel, string ReceiverLabel,
                                   int? AttackerHold, int? ReceiverHold, bool Crossfade, int Blend);

/// <summary>The performer tracks: a frame ruler, the ATTACKER and RECEIVER lanes with the synchronized blocks
/// (white edge handles: trim; the body: drag to reorder; right click: the lane's menu), HOLD where a lane holds its
/// last pose, and the playhead. Ctrl + wheel zooms.</summary>
public sealed class TimelineView : Control
{
    const double HeaderW = 132, RulerH = 30, LaneH = 46, LaneGap = 8, Pad = 10;
    IReadOnlyList<TimelineBlock> blocks = Array.Empty<TimelineBlock>();
    int total;
    double frame;
    int selected = -1;
    double pxPerFrame = 3;
    double scroll;

    public event Action<double>? Scrubbed;
    public event Action<int>? BlockSelected;
    /// <summary>block, edge (-1 = in, +1 = out), frames moved</summary>
    public event Action<int, int, int>? BlockTrimmed;
    /// <summary>from index, to index</summary>
    public event Action<int, int>? BlockMoved;
    /// <summary>block, lane (0 attacker, 1 receiver), where</summary>
    public event Action<int, int, Point>? LaneMenu;
    public event Action<int>? LaneSelected;

    int selectedLane;
    // drag state
    enum Drag { None, Scrub, TrimIn, TrimOut, Move }
    Drag drag;
    int dragBlock;
    double dragX0;
    int dragApplied;

    public TimelineView()
    {
        ClipToBounds = true;
        Focusable = true;
        Height = RulerH + 2 * LaneH + LaneGap + 2 * Pad + 8;
    }

    public void SetBlocks(IReadOnlyList<TimelineBlock> b, int totalFrames)
    {
        blocks = b;
        total = totalFrames;
        InvalidateVisual();
    }

    public double Frame { get => frame; set { frame = value; InvalidateVisual(); } }
    public int Selected { get => selected; set { selected = value; InvalidateVisual(); } }
    public int SelectedLane => selectedLane;

    /// <summary>0..1 slider -> pixels per frame (fits the whole timeline at 0.5)</summary>
    public double Zoom
    {
        set
        {
            double fit = Math.Max(0.4, (Bounds.Width - HeaderW - 2 * Pad - 20) / Math.Max(30, total));
            pxPerFrame = fit * Math.Pow(4, (value - 0.5) * 2);
            InvalidateVisual();
        }
    }

    double X(double f) => HeaderW + Pad + f * pxPerFrame - scroll;
    double F(double x) => (x - HeaderW - Pad + scroll) / pxPerFrame;
    double LaneY(int lane) => RulerH + Pad + lane * (LaneH + LaneGap);

    // ---------------------------------------------------------------- drawing
    static readonly IBrush Bg = new SolidColorBrush(Color.FromRgb(0x13, 0x14, 0x18));
    static readonly IBrush HeaderBg = new SolidColorBrush(Color.FromRgb(0x18, 0x19, 0x1E));
    static readonly IBrush LaneBg = new SolidColorBrush(Color.FromRgb(0x17, 0x18, 0x1D));
    static readonly IBrush BlockFill = new SolidColorBrush(Color.FromRgb(0x2E, 0x5D, 0x7A));
    static readonly IBrush BlockTop = new SolidColorBrush(Color.FromRgb(0x3B, 0x72, 0x93));
    static readonly IBrush HoldFill = new SolidColorBrush(Color.FromRgb(0x1D, 0x37, 0x48));
    static readonly IBrush Accent = new SolidColorBrush(Color.FromRgb(0x3B, 0x8C, 0xFF));
    static readonly IBrush AccentHiB = new SolidColorBrush(Color.FromRgb(0x62, 0xB8, 0xFF));
    /// <summary>each clip its own colour (cycled), so the joins read at a glance</summary>
    static readonly Color[] ClipColors =
    {
        Color.FromRgb(0x3B, 0x8C, 0xFF), Color.FromRgb(0x1F, 0xB8, 0xA8), Color.FromRgb(0x8E, 0x6C, 0xF0),
        Color.FromRgb(0xD9, 0x9A, 0x2B), Color.FromRgb(0xD9, 0x56, 0x8A), Color.FromRgb(0x4C, 0xB8, 0x60),
    };
    static Color Mix(Color a, Color b, double t) => Color.FromRgb((byte)(a.R + (b.R - a.R) * t), (byte)(a.G + (b.G - a.G) * t), (byte)(a.B + (b.B - a.B) * t));
    static readonly IBrush TextB = new SolidColorBrush(Color.FromRgb(0xEC, 0xEC, 0xEE));
    static readonly IBrush DimB = new SolidColorBrush(Color.FromRgb(0x9A, 0x9D, 0xA5));
    static readonly IBrush FaintB = new SolidColorBrush(Color.FromRgb(0x5E, 0x61, 0x69));
    static readonly IPen GridPen = new Pen(new SolidColorBrush(Color.FromArgb(40, 255, 255, 255)), 1);
    static readonly IPen TickPen = new Pen(new SolidColorBrush(Color.FromArgb(90, 255, 255, 255)), 1);
    static readonly Typeface Ui = new("Inter, Segoe UI");
    static readonly Typeface UiBold = new("Inter, Segoe UI", FontStyle.Normal, FontWeight.SemiBold);
    static readonly Typeface Mono = new("Consolas");

    static void Text(DrawingContext c, string s, double x, double y, IBrush b, double size = 11, Typeface? tf = null, double maxW = 0)
    {
        var ft = new FormattedText(s, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, tf ?? Ui, size, b);
        if (maxW > 0) { ft.MaxTextWidth = maxW; ft.MaxLineCount = 1; ft.Trimming = TextTrimming.CharacterEllipsis; }
        c.DrawText(ft, new Point(x, y));
    }

    public override void Render(DrawingContext c)
    {
        var size = Bounds.Size;
        c.FillRectangle(Bg, new Rect(size));
        // ruler
        c.FillRectangle(HeaderBg, new Rect(0, 0, size.Width, RulerH));
        Text(c, "frames", 14, 9, FaintB, 10);
        int step = NiceStep(60 / pxPerFrame);
        using (c.PushClip(new Rect(HeaderW, 0, size.Width - HeaderW, size.Height)))
        {
            int f0 = Math.Max(0, (int)(F(HeaderW) / step) * step);
            for (int f = f0; X(f) < size.Width; f += step)
            {
                double x = X(f);
                c.DrawLine(TickPen, new Point(x, RulerH - 8), new Point(x, RulerH));
                c.DrawLine(GridPen, new Point(x, RulerH), new Point(x, size.Height));
                Text(c, f.ToString(CultureInfo.InvariantCulture), x + 3, 8, DimB, 10, Mono);
                for (int k = 1; k < 4; k++)
                {
                    double xm = X(f + step * k / 4.0);
                    c.DrawLine(GridPen, new Point(xm, RulerH - 4), new Point(xm, RulerH));
                }
            }
        }
        // track headers: a dot in the wrestler's colour, his role
        string[] names = { "Attacker", "Receiver" };
        string[] subs = { "wrestler 1", "wrestler 2" };
        IBrush[] dots = { new SolidColorBrush(Color.FromRgb(0xE8, 0xC4, 0xAE)), new SolidColorBrush(Color.FromRgb(0x82, 0xA8, 0xE4)) };
        for (int lane = 0; lane < 2; lane++)
        {
            double y = LaneY(lane);
            c.FillRectangle(LaneBg, new Rect(HeaderW, y, size.Width - HeaderW, LaneH));
            if (lane == selectedLane) c.DrawRectangle(Accent, null, new Rect(4, y + 6, 3, LaneH - 12), 1.5, 1.5);
            c.DrawEllipse(dots[lane], null, new Point(18, y + 16), 4.5, 4.5);
            Text(c, names[lane], 30, y + 8, lane == selectedLane ? AccentHiB : TextB, 12, UiBold);
            Text(c, subs[lane], 30, y + 25, FaintB, 9.5);
        }
        c.DrawLine(new Pen(new SolidColorBrush(Color.FromRgb(0x2B, 0x2D, 0x34)), 1), new Point(HeaderW, 0), new Point(HeaderW, size.Height));
        // blocks
        using (c.PushClip(new Rect(HeaderW, RulerH, size.Width - HeaderW, size.Height - RulerH)))
        {
            for (int i = 0; i < blocks.Count; i++)
            {
                var b = blocks[i];
                for (int lane = 0; lane < 2; lane++)
                {
                    double y = LaneY(lane) + 3, h = LaneH - 6;
                    double x0 = X(b.Start), x1 = X(b.Start + b.Length);
                    var r = new Rect(x0 + 1, y, Math.Max(4, x1 - x0 - 2), h);
                    var cc = ClipColors[i % ClipColors.Length];
                    var fill = new SolidColorBrush(Mix(Color.FromRgb(0x13, 0x17, 0x1F), cc, 0.42));
                    var edge = new SolidColorBrush(Mix(Color.FromRgb(0x13, 0x17, 0x1F), cc, 0.85));
                    c.DrawRectangle(fill, null, r, 9, 9);
                    c.DrawRectangle(edge, null, new Rect(r.X, r.Bottom - 3, r.Width, 3), 1.5, 1.5);
                    int? hold = lane == 0 ? b.AttackerHold : b.ReceiverHold;
                    if (hold is int hf && hf < b.Length)
                    {
                        double xh = X(b.Start + hf);
                        var hr = new Rect(xh, y, Math.Max(0, r.Right - xh), h);
                        c.DrawRectangle(new SolidColorBrush(Mix(Color.FromRgb(0x13, 0x17, 0x1F), cc, 0.18)), null, hr, 9, 9);
                        Text(c, "holds", xh + 6, y + 4, FaintB, 9);
                    }
                    if (b.Crossfade && b.Blend > 0 && i > 0)
                    {
                        double xb = X(b.Start + b.Blend);
                        var geo = new StreamGeometry();
                        using (var g = geo.Open()) { g.BeginFigure(new Point(r.X, r.Bottom), true); g.LineTo(new Point(xb, r.Y)); g.LineTo(new Point(r.X, r.Y)); g.EndFigure(true); }
                        c.DrawGeometry(new SolidColorBrush(Color.FromArgb(60, 255, 255, 255)), null, geo);
                    }
                    string label = lane == 0 ? b.AttackerLabel : b.ReceiverLabel;
                    Text(c, label, r.X + 12, y + 5, TextB, 11, UiBold, Math.Max(10, r.Width - 26));
                    Text(c, b.Name, r.X + 12, y + 21, DimB, 9.5, null, Math.Max(10, r.Width - 26));
                    // edge handles
                    var handle = new SolidColorBrush(Color.FromArgb(200, 255, 255, 255));
                    c.DrawRectangle(handle, null, new Rect(r.X + 4, y + h / 2 - 7, 2.5, 14), 1.25, 1.25);
                    c.DrawRectangle(handle, null, new Rect(r.Right - 6.5, y + h / 2 - 7, 2.5, 14), 1.25, 1.25);
                    if (i == selected)
                        c.DrawRectangle(null, new Pen(lane == selectedLane ? Brushes.White : new SolidColorBrush(Color.FromArgb(120, 255, 255, 255)), 1.6), r.Inflate(1.5), 10, 10);
                }
            }
            // playhead
            double px = X(frame);
            c.DrawLine(new Pen(Accent, 2), new Point(px, RulerH - 2), new Point(px, size.Height));
        }
        double phx = X(frame);
        if (phx >= HeaderW)
        {
            // the playhead's frame tag
            string tag = ((int)Math.Round(frame)).ToString(CultureInfo.InvariantCulture);
            double tw = 8 + tag.Length * 7;
            c.DrawRectangle(Accent, null, new Rect(phx - tw / 2, 3, tw, RulerH - 8), 7, 7);
            Text(c, tag, phx - tw / 2 + 4, 5, Brushes.White, 10.5, UiBold);
        }
    }

    static int NiceStep(double raw)
    {
        foreach (int s in new[] { 1, 2, 5, 10, 15, 20, 30, 50, 60, 100, 120, 200, 300, 600 })
            if (s >= raw) return s;
        return 1000;
    }

    // ---------------------------------------------------------------- input
    (int Block, int Lane, Drag Kind) Hit(Point p)
    {
        for (int lane = 0; lane < 2; lane++)
        {
            double y = LaneY(lane);
            if (p.Y < y || p.Y > y + LaneH) continue;
            for (int i = 0; i < blocks.Count; i++)
            {
                double x0 = X(blocks[i].Start), x1 = X(blocks[i].Start + blocks[i].Length);
                if (p.X < x0 - 4 || p.X > x1 + 4) continue;
                if (p.X - x0 < 9) return (i, lane, Drag.TrimIn);
                if (x1 - p.X < 9) return (i, lane, Drag.TrimOut);
                return (i, lane, Drag.Move);
            }
            return (-1, lane, Drag.None);
        }
        return (-1, -1, Drag.None);
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        var pt = e.GetCurrentPoint(this);
        var p = pt.Position;
        if (p.X < HeaderW)
        {
            int lane = p.Y >= LaneY(1) ? 1 : p.Y >= LaneY(0) ? 0 : -1;
            if (lane >= 0) { selectedLane = lane; LaneSelected?.Invoke(lane); InvalidateVisual(); }
            return;
        }
        if (p.Y < RulerH)
        {
            drag = Drag.Scrub;
            Scrubbed?.Invoke(Math.Clamp(F(p.X), 0, Math.Max(0, total - 1)));
            e.Pointer.Capture(this);
            return;
        }
        var (bi, ln, kind) = Hit(p);
        if (ln >= 0) { selectedLane = ln; LaneSelected?.Invoke(ln); }
        if (bi < 0) { InvalidateVisual(); return; }
        selected = bi;
        BlockSelected?.Invoke(bi);
        if (pt.Properties.IsRightButtonPressed) { LaneMenu?.Invoke(bi, ln, p); InvalidateVisual(); return; }
        drag = kind;
        dragBlock = bi;
        dragX0 = p.X;
        dragApplied = 0;
        e.Pointer.Capture(this);
        InvalidateVisual();
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        var p = e.GetPosition(this);
        if (drag == Drag.None)
        {
            var (_, _, kind) = Hit(p);
            Cursor = kind is Drag.TrimIn or Drag.TrimOut ? new Cursor(StandardCursorType.SizeWestEast)
                   : kind == Drag.Move ? new Cursor(StandardCursorType.SizeAll) : Cursor.Default;
            return;
        }
        if (drag == Drag.Scrub) { Scrubbed?.Invoke(Math.Clamp(F(p.X), 0, Math.Max(0, total - 1))); return; }
        int frames = (int)Math.Round((p.X - dragX0) / pxPerFrame);
        if (drag is Drag.TrimIn or Drag.TrimOut)
        {
            int d = frames - dragApplied;
            if (d != 0) { BlockTrimmed?.Invoke(dragBlock, drag == Drag.TrimIn ? -1 : 1, d); dragApplied = frames; }
        }
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        if (drag == Drag.Move)
        {
            var p = e.GetPosition(this);
            double f = F(p.X);
            int to = dragBlock;
            for (int i = 0; i < blocks.Count; i++)
                if (f >= blocks[i].Start && f < blocks[i].Start + blocks[i].Length) to = i;
            if (f >= total) to = blocks.Count - 1;
            if (to != dragBlock) BlockMoved?.Invoke(dragBlock, to);
        }
        drag = Drag.None;
        e.Pointer.Capture(null);
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        if ((e.KeyModifiers & KeyModifiers.Control) != 0)
        {
            double f = F(e.GetPosition(this).X);
            pxPerFrame = Math.Clamp(pxPerFrame * Math.Pow(1.15, e.Delta.Y), 0.2, 40);
            scroll = Math.Max(0, HeaderW + Pad + f * pxPerFrame - e.GetPosition(this).X);
        }
        else
            scroll = Math.Max(0, scroll - e.Delta.Y * 40 - e.Delta.X * 40);
        InvalidateVisual();
        e.Handled = true;
    }
}
