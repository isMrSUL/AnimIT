using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;

namespace AnimIT.Controls;

/// <summary>The porter's event lanes: attacker / receiver motion events and their sounds on one frame ruler.
/// Drag a mark to move it (whole frames), click empty space to move the playhead.</summary>
public sealed class EventTimeline : Control
{
    public sealed record Mark(object Tag, int Lane, int Frame, string Label, Color Color, bool Major);
    /// <summary>a source-game marker drawn as a dashed line over the lanes (AEW hit, ring shake...)</summary>
    public sealed record Guide(int Frame, string Label, Color Color);
    List<Guide> guides = new();
    public void SetGuides(List<Guide> g) { guides = g; InvalidateVisual(); }

    public static readonly string[] Lanes = { "Attacker", "Receiver", "Attacker sounds", "Receiver sounds" };
    List<Mark> marks = new();
    int frames = 1;
    double current;
    Mark? drag, hover;
    Guide? hoverGuide;
    int dragFrame;
    public object? Selected { get; private set; }

    /// <summary>a mark dropped on a new frame</summary>
    public event Action<object, int>? MarkMoved;
    public event Action<object?>? MarkSelected;
    /// <summary>a click on empty space: the frame under it</summary>
    public event Action<double>? FrameClicked;

    const double LeftW = 112, RowH = 24, Top = 20;

    public EventTimeline() { ClipToBounds = true; Focusable = true; }

    public void Set(List<Mark> m, int n)
    {
        marks = m; frames = Math.Max(1, n);
        if (Selected != null && !marks.Any(x => ReferenceEquals(x.Tag, Selected))) Selected = null;
        InvalidateVisual();
    }

    public void Select(object? tag) { Selected = tag; InvalidateVisual(); }

    public double CurrentFrame
    {
        get => current;
        set { current = value; InvalidateVisual(); }
    }

    double Span => Math.Max(1, Bounds.Width - LeftW - 22);
    double X(double f) => LeftW + 10 + f * Span / Math.Max(1, frames - 1);
    double F(double x) => (x - LeftW - 10) * Math.Max(1, frames - 1) / Span;
    static double RowY(int lane) => Top + lane * RowH;

    protected override Size MeasureOverride(Size available) => new(double.IsInfinity(available.Width) ? 600 : available.Width, Top + RowH * Lanes.Length + 4);

    static FormattedText Text(string s, double size, IBrush brush, FontWeight w = FontWeight.Normal) =>
        new(s, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, new Typeface("Inter, Segoe UI", FontStyle.Normal, w), size, brush);

    public override void Render(DrawingContext ctx)
    {
        var b = Bounds;
        var faint = new SolidColorBrush(Color.Parse("#646E82"));
        var line = new SolidColorBrush(Color.Parse("#252C3A"));
        // ruler
        int step = frames > 400 ? 60 : frames > 200 ? 30 : 10;
        for (int f = 0; f < frames; f += step)
        {
            double x = X(f);
            ctx.DrawLine(new Pen(line, 1), new Point(x, Top - 4), new Point(x, Top + RowH * Lanes.Length));
            if (f % (step * (frames > 200 ? 1 : 3)) == 0) ctx.DrawText(Text(f.ToString(), 10, faint), new Point(x + 2, 2));
        }
        // lanes
        for (int i = 0; i < Lanes.Length; i++)
        {
            double y = RowY(i);
            ctx.FillRectangle(new SolidColorBrush(Color.Parse(i % 2 == 0 ? "#0F131B" : "#0C1017")), new Rect(LeftW, y, b.Width - LeftW, RowH - 2), 4);
            ctx.DrawText(Text(Lanes[i], 11, new SolidColorBrush(Color.Parse(i < 2 ? "#98A2B5" : "#5FA9C2"))), new Point(4, y + 5));
        }
        // the source game's markers
        foreach (var gd in guides)
        {
            double gx = X(gd.Frame);
            var pen = new Pen(new SolidColorBrush(gd.Color, 0.55), 1, new DashStyle(new double[] { 3, 3 }, 0));
            ctx.DrawLine(pen, new Point(gx, Top - 2), new Point(gx, Top + RowH * Lanes.Length));
        }
        // marks: minor first, then major, the dragged / selected on top; marks on the same frame of a lane share its
        // height (a hit and a ring shake on one frame both show)
        var slot = new Dictionary<Mark, (int I, int N)>();
        foreach (var g in marks.Where(m => !ReferenceEquals(m, drag)).GroupBy(m => (m.Lane, m.Frame)))
        {
            var l = g.OrderByDescending(m => m.Major).ToList();
            for (int i = 0; i < l.Count; i++) slot[l[i]] = (i, l.Count);
        }
        foreach (var m in marks.OrderBy(m => m.Major).ThenBy(m => ReferenceEquals(m.Tag, Selected)))
        {
            int f = ReferenceEquals(m, drag) ? dragFrame : m.Frame;
            double x = X(f), y = RowY(m.Lane);
            double w = m.Major ? 9 : 5, h = m.Major ? 16 : 10;
            var (si, sn) = slot.GetValueOrDefault(m, (0, 1));
            double top = y + (RowH - 2 - h) / 2;
            if (sn > 1) { h = Math.Max(3, (RowH - 4) / (double)sn - 1); top = y + 1 + si * (h + 1); }
            var r = new Rect(x - w / 2, top, w, h);
            bool sel = ReferenceEquals(m.Tag, Selected);
            ctx.DrawRectangle(new SolidColorBrush(m.Color), sel ? new Pen(Brushes.White, 2) : null, r, 3, 3);
        }
        // playhead
        double px = X(current);
        ctx.DrawLine(new Pen(new SolidColorBrush(Color.Parse("#62B8FF")), 1.5), new Point(px, Top - 6), new Point(px, Top + RowH * Lanes.Length));
        // the label of the mark under the mouse / being dragged (else of a guide under the mouse)
        if (drag == null && hover == null && hoverGuide is Guide hg)
        {
            var t = Text($"{hg.Label}  ·  frame {hg.Frame}", 11, Brushes.White, FontWeight.SemiBold);
            double x = Math.Clamp(X(hg.Frame) + 6, LeftW, Math.Max(LeftW, b.Width - t.Width - 12));
            ctx.FillRectangle(new SolidColorBrush(Color.Parse("#E6121722")), new Rect(x - 4, 0, t.Width + 8, 18), 5);
            ctx.DrawText(t, new Point(x, 1));
        }
        var lab = drag ?? hover;
        if (lab != null)
        {
            int f = ReferenceEquals(lab, drag) ? dragFrame : lab.Frame;
            var t = Text($"{lab.Label}  ·  frame {f}" + (ReferenceEquals(lab, drag) && f != lab.Frame ? $"  (was {lab.Frame})" : ""), 11, Brushes.White, FontWeight.SemiBold);
            double x = Math.Clamp(X(f) + 8, LeftW, Math.Max(LeftW, b.Width - t.Width - 12));
            double y = RowY(lab.Lane) - 2;
            ctx.FillRectangle(new SolidColorBrush(Color.Parse("#E6121722")), new Rect(x - 4, y, t.Width + 8, 18), 5);
            ctx.DrawText(t, new Point(x, y + 1));
        }
    }

    Mark? HitTest(Point p)
    {
        int lane = (int)Math.Floor((p.Y - Top) / RowH);
        if (lane < 0 || lane >= Lanes.Length || p.X < LeftW) return null;
        return marks.Where(m => m.Lane == lane && Math.Abs(X(m.Frame) - p.X) <= (m.Major ? 7 : 5))
                    .OrderByDescending(m => m.Major).ThenBy(m => Math.Abs(X(m.Frame) - p.X)).FirstOrDefault();
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        var p = e.GetPosition(this);
        var m = HitTest(p);
        if (m != null)
        {
            drag = m; dragFrame = m.Frame; Selected = m.Tag;
            MarkSelected?.Invoke(m.Tag);
            e.Pointer.Capture(this);
        }
        else if (p.X >= LeftW)
        {
            Selected = null; MarkSelected?.Invoke(null);
            FrameClicked?.Invoke(Math.Clamp(F(p.X), 0, frames - 1));
        }
        InvalidateVisual();
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        var p = e.GetPosition(this);
        if (drag != null) { dragFrame = (int)Math.Clamp(Math.Round(F(p.X)), 0, frames - 1); InvalidateVisual(); return; }
        var h = HitTest(p);
        var hg = h == null ? guides.Where(g => Math.Abs(X(g.Frame) - p.X) <= 4).Cast<Guide?>().FirstOrDefault() : null;
        if (!ReferenceEquals(h, hover) || hg != hoverGuide) { hover = h; hoverGuide = hg; Cursor = h != null ? new Cursor(StandardCursorType.SizeWestEast) : Cursor.Default; InvalidateVisual(); }
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (drag != null)
        {
            var d = drag; drag = null;
            e.Pointer.Capture(null);
            if (dragFrame != d.Frame) MarkMoved?.Invoke(d.Tag, dragFrame);
            InvalidateVisual();
        }
    }

    protected override void OnPointerExited(PointerEventArgs e) { base.OnPointerExited(e); if (drag == null) { hover = null; InvalidateVisual(); } }
}
