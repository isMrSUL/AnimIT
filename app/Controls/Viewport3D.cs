using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using AnimIT.Core;

namespace AnimIT.Controls;

/// <summary>The 3D preview: both performers as shaded capsule mannequins on a floor grid, with contact shadows.
/// Left drag orbits, right / middle drag pans, the wheel zooms, a double click frames the pair again.
/// Game space is y down; the view turns it y up.</summary>
public sealed class Viewport3D : Control
{
    // ---------------------------------------------------------------- data
    Skeleton? skel;
    List<V3[]?> att = new(), rec = new(), par = new();
    double frame;
    readonly List<(int A, int B, double R)> caps = new();
    int head = -1, eyeL = -1, eyeR = -1;
    double floorY;                // the floor, in view coordinates (y up)

    // ---------------------------------------------------------------- camera
    double yaw = 0.65, pitch = 0.22, dist = 62;
    V3 target = new(0, 8, -8);
    Point? dragFrom;
    bool panning;

    public static readonly Color AttackerColor = Color.FromRgb(0xE8, 0xC4, 0xAE);
    public static readonly Color ReceiverColor = Color.FromRgb(0x82, 0xA8, 0xE4);
    public static readonly Color PartnerColor = Color.FromRgb(0x8F, 0xD6, 0xA4);

    /// <summary>a third wrestler (a double-team move's partner), or an empty list</summary>
    public void SetPartner(List<V3[]?> p) { parRaw = p; par = Staged(p, stageRY); InvalidateVisual(); }

    // ---------------------------------------------------------------- the stage: where in the ring the move plays
    // (view only: the game puts a corner move in the corner, an apron move at the ropes). The bodies and the handle are
    // drawn turned by stageYaw and moved by stageShift; the handle reports its drags back in the unstaged space.
    double stageYaw;
    V3 stageShift = new(0, 0, 0);
    List<V3[]?> attRaw = new(), recRaw = new(), parRaw = new();
    double stageAY, stageRY;          // each lane raised / lowered (game y, down): the floor, a turnbuckle
    public void SetStage(double yaw, V3 shift, double attackerY = 0, double receiverY = 0)
    {
        stageYaw = yaw; stageShift = shift; stageAY = attackerY; stageRY = receiverY;
        att = Staged(attRaw, stageAY); rec = Staged(recRaw, stageRY); par = Staged(parRaw, stageRY);
        InvalidateVisual();
    }
    bool NoStage => Math.Abs(stageYaw) < 1e-12 && Math.Abs(stageShift.X) + Math.Abs(stageShift.Z) < 1e-12 && stageAY == 0 && stageRY == 0;
    V3 St(V3 p) => M3.Ry(stageYaw) * p + stageShift;
    V3 Un(V3 p) => M3.Ry(-stageYaw) * (p - stageShift);
    List<V3[]?> Staged(List<V3[]?> L, double dy) => NoStage ? L : L.Select(P => P?.Select(p => St(p) + new V3(0, dy, 0)).ToArray()).ToList();

    public Viewport3D()
    {
        ClipToBounds = true;
        Focusable = true;
    }

    public void SetSkeleton(Skeleton s)
    {
        skel = s;
        caps.Clear();
        int J(string n) => s.IndexOf(n);
        void C(string a, string b, double r) { int i = J(a), j = J(b); if (i >= 0 && j >= 0) caps.Add((i, j, r)); }
        C("l_momo", "r_momo", 1.05); C("root", "koshi", 1.25); C("koshi", "mune", 1.45); C("l_sakotsu", "r_sakotsu", 1.0);
        C("mune", "kubi", 1.05); C("kubi", "atama", 0.62);
        foreach (var x in new[] { "l_", "r_" })
        {
            C(x + "momo", x + "sune", 1.0); C(x + "sune", x + "ashi", 0.74); C(x + "ashi", x + "tsumasaki", 0.5);
            C("mune", x + "sakotsu", 0.85); C(x + "sakotsu", x + "ninoude", 0.72); C(x + "ninoude", x + "kote", 0.74);
            C(x + "kote", x + "te", 0.58); C(x + "te", x + "yubi20", 0.44);
            foreach (int f in new[] { 0, 1, 2, 3, 4 })
            {
                C(x + "te", $"{x}yubi{f}0", 0.16); C($"{x}yubi{f}0", $"{x}yubi{f}1", 0.13); C($"{x}yubi{f}1", $"{x}yubi{f}2", 0.12);
            }
        }
        head = J("atama"); eyeL = J("l_eye"); eyeR = J("r_eye");
        InvalidateVisual();
    }

    public void SetFrames(List<V3[]?> attacker, List<V3[]?> receiver, bool reframe)
    {
        attRaw = attacker; recRaw = receiver;
        att = Staged(attacker, stageAY);
        rec = Staged(receiver, stageRY);
        // the floor: the mat is at the wrestlers' origin height (game y 0); every stock move's feet sit ~0.5 above it
        floorY = 0;
        if (reframe) Frame();
        InvalidateVisual();
    }

    public double CurrentFrame
    {
        get => frame;
        set { frame = value; InvalidateVisual(); }
    }

    /// <summary>aim the camera at the pair (their roots at the start)</summary>
    public void Frame()
    {
        var pts = new List<V3>();
        foreach (var P in new[] { att.FirstOrDefault(), rec.FirstOrDefault() })
            if (P != null) pts.AddRange(P);
        if (pts.Count == 0) return;
        double cx = pts.Average(p => p.X), cz = pts.Average(p => p.Z), cy = -pts.Average(p => p.Y);
        target = new V3(cx, cy, cz);
        dist = 62;
        InvalidateVisual();
    }

    // ---------------------------------------------------------------- the handle (turn ring, X / Z arrows, centre: free move)
    int gizmoLane = -1;                 // which wrestler it moves (0 attacker, 1 receiver), -1 hidden
    V3 gizmoAt;                         // its centre on the mat (game x, z; that wrestler at the clip start)
    enum Grab { None, Turn, X, Z, Free }
    Grab grab, hover;
    V3? grabFloor;
    Point pressAt;
    const double GizmoR = 7.5, ArrowL = 11;

    /// <summary>a turn (degrees, about the handle centre) or a move on the mat (dx, dz) asked for by dragging the handle</summary>
    public event Action<int, double>? GizmoTurned;
    public event Action<int, double, double>? GizmoMoved;
    /// <summary>a wrestler clicked in the view (0 attacker, 1 receiver)</summary>
    public event Action<int>? WrestlerPicked;

    public void SetGizmo(int lane, V3 at) { gizmoLane = lane; gizmoAt = at; InvalidateVisual(); }
    public void HideGizmo() { gizmoLane = -1; InvalidateVisual(); }

    /// <summary>screen point -> where it lands on the mat (view coordinates), or null</summary>
    V3? Floor(Point s)
    {
        var (right, up, fwd) = Basis();
        var eye = target - fwd * dist;
        double f = Bounds.Height * 1.25;
        var d = fwd + right * ((s.X - Bounds.Width / 2) / f) + up * (-(s.Y - Bounds.Height / 2) / f);
        if (Math.Abs(d.Y) < 1e-6) return null;
        double t = (floorY - eye.Y) / d.Y;
        return t > 0 ? eye + d * t : null;
    }

    static double Dist(Point a, Point b) => Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y));

    static double SegDist(Point p, Point a, Point b)
    {
        double dx = b.X - a.X, dy = b.Y - a.Y, l = dx * dx + dy * dy;
        double u = l < 1e-9 ? 0 : Math.Clamp(((p.X - a.X) * dx + (p.Y - a.Y) * dy) / l, 0, 1);
        double x = a.X + u * dx - p.X, y = a.Y + u * dy - p.Y;
        return Math.Sqrt(x * x + y * y);
    }

    Grab HitGizmo(Point p)
    {
        if (gizmoLane < 0) return Grab.None;
        var at = St(new V3(gizmoAt.X, 0, gizmoAt.Z));
        var c = new V3(at.X, floorY, at.Z);
        if (Project(c) is { } qc && Dist(qc.P, p) < 12) return Grab.Free;
        foreach (var (g, dir) in new[] { (Grab.X, M3.Ry(stageYaw) * new V3(1, 0, 0)), (Grab.Z, M3.Ry(stageYaw) * new V3(0, 0, 1)) })
            if (Project(c) is { } a && Project(c + dir * ArrowL) is { } b && SegDist(p, a.P, b.P) < 7) return g;
        Point? last = null;
        for (int k = 0; k <= 64; k++)
        {
            double t = k * Math.PI * 2 / 64;
            if (Project(c + new V3(Math.Sin(t) * GizmoR, 0, Math.Cos(t) * GizmoR)) is not { } q) { last = null; continue; }
            if (last is Point l && SegDist(p, l, q.P) < 8) return Grab.Turn;
            last = q.P;
        }
        return Grab.None;
    }

    void DrawGizmo(DrawingContext ctx)
    {
        if (gizmoLane < 0) return;
        var at = St(new V3(gizmoAt.X, 0, gizmoAt.Z));
        var c = new V3(at.X, floorY + 0.05, at.Z);
        IPen P(Color col, Grab g, double w)
        {
            bool on = hover == g || grab == g;
            return new Pen(new SolidColorBrush(on ? Color.FromRgb(0xFF, 0xF0, 0x9A) : col), on ? w + 1.5 : w, lineCap: PenLineCap.Round);
        }
        // the turn ring, a tick every 45 degrees
        var ring = P(Color.FromRgb(0x5F, 0xC8, 0xFF), Grab.Turn, 2.6);
        V3? prev = null;
        for (int k = 0; k <= 72; k++)
        {
            double t = k * Math.PI * 2 / 72;
            var q = c + new V3(Math.Sin(t) * GizmoR, 0, Math.Cos(t) * GizmoR);
            if (prev is V3 l) Line(ctx, ring, l, q);
            prev = q;
            if (k % 9 == 0) Line(ctx, ring, q, c + new V3(Math.Sin(t) * (GizmoR - 0.9), 0, Math.Cos(t) * (GizmoR - 0.9)));
        }
        // X (red) and Z (green) arrows
        foreach (var (g, dir, col) in new[] { (Grab.X, M3.Ry(stageYaw) * new V3(1, 0, 0), Color.FromRgb(0xF0, 0x55, 0x60)), (Grab.Z, M3.Ry(stageYaw) * new V3(0, 0, 1), Color.FromRgb(0x6A, 0xD6, 0x5C)) })
        {
            var pen = P(col, g, 2.6);
            var tip = c + dir * ArrowL;
            Line(ctx, pen, c + dir * 1.4, tip);
            var side = new V3(dir.Z, 0, -dir.X) * 0.9;
            Line(ctx, pen, tip, tip - dir * 1.8 + side);
            Line(ctx, pen, tip, tip - dir * 1.8 - side);
        }
        if (Project(c) is { } qc)
        {
            bool on = hover == Grab.Free || grab == Grab.Free;
            ctx.DrawEllipse(new SolidColorBrush(on ? Color.FromRgb(0xFF, 0xF0, 0x9A) : Colors.White), new Pen(new SolidColorBrush(Color.FromArgb(160, 0, 0, 0)), 1.5), qc.P, on ? 7 : 5.5, on ? 7 : 5.5);
        }
    }

    /// <summary>the wrestler nearest a click (within 28 px of a joint), or -1</summary>
    int PickWrestler(Point p)
    {
        int f = (int)Math.Round(frame), best = -1;
        double bd = 28;
        for (int lane = 0; lane < 2; lane++)
        {
            var L = lane == 0 ? att : rec;
            var J = f >= 0 && f < L.Count ? L[f] : null;
            if (J == null) continue;
            foreach (var j in J)
                if (Project(View(j)) is { } q && Dist(q.P, p) < bd) { bd = Dist(q.P, p); best = lane; }
        }
        return best;
    }

    // ---------------------------------------------------------------- input
    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        var pt = e.GetCurrentPoint(this);
        if (e.ClickCount == 2) { Frame(); return; }
        pressAt = pt.Position;
        if (pt.Properties.IsLeftButtonPressed && HitGizmo(pt.Position) is var g && g != Grab.None)
        {
            grab = g;
            grabFloor = Floor(pt.Position);
            e.Pointer.Capture(this);
            InvalidateVisual();
            return;
        }
        dragFrom = pt.Position;
        panning = pt.Properties.IsRightButtonPressed || pt.Properties.IsMiddleButtonPressed;
        e.Pointer.Capture(this);
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        var p = e.GetPosition(this);
        if (grab != Grab.None)
        {
            if (Floor(p) is not V3 nowV || grabFloor is not V3 wasV) return;
            V3 now = Un(nowV), was = Un(wasV);   // (the handle works in the unstaged space)
            if (grab == Grab.Turn)
            {
                double a0 = Math.Atan2(was.X - gizmoAt.X, was.Z - gizmoAt.Z), a1 = Math.Atan2(now.X - gizmoAt.X, now.Z - gizmoAt.Z);
                double d = ((a1 - a0) * 180 / Math.PI + 540) % 360 - 180;
                if (Math.Abs(d) > 1e-6) GizmoTurned?.Invoke(gizmoLane, d);
            }
            else
            {
                double mx = grab == Grab.Z ? 0 : now.X - was.X, mz = grab == Grab.X ? 0 : now.Z - was.Z;
                if (Math.Abs(mx) + Math.Abs(mz) > 1e-6) GizmoMoved?.Invoke(gizmoLane, mx, mz);
            }
            grabFloor = nowV;
            return;
        }
        if (dragFrom is not Point p0)
        {
            var h = HitGizmo(p);
            if (h != hover) { hover = h; Cursor = new Cursor(h == Grab.None ? StandardCursorType.Arrow : StandardCursorType.Hand); InvalidateVisual(); }
            return;
        }
        double dx = p.X - p0.X, dy = p.Y - p0.Y;
        dragFrom = p;
        if (panning)
        {
            var (right, up, _) = Basis();
            double k = dist / Math.Max(200, Bounds.Height) * 1.1;
            target = target - right * (dx * k) + up * (dy * k);
        }
        else
        {
            yaw -= dx * 0.008;
            pitch = Math.Clamp(pitch + dy * 0.006, -0.25, 1.35);
        }
        InvalidateVisual();
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        var p = e.GetPosition(this);
        bool click = Dist(p, pressAt) < 4 && grab == Grab.None && !panning;
        grab = Grab.None;
        grabFloor = null;
        dragFrom = null;
        e.Pointer.Capture(null);
        if (click && PickWrestler(p) is int lane && lane >= 0) WrestlerPicked?.Invoke(lane);
        InvalidateVisual();
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        dist = Math.Clamp(dist * Math.Pow(0.88, e.Delta.Y), 12, 260);
        InvalidateVisual();
    }

    // ---------------------------------------------------------------- projection
    (V3 Right, V3 Up, V3 Fwd) Basis()
    {
        var fwd = new V3(-Math.Sin(yaw) * Math.Cos(pitch), -Math.Sin(pitch), -Math.Cos(yaw) * Math.Cos(pitch));
        var right = new V3(Math.Cos(yaw), 0, -Math.Sin(yaw));
        var up = new V3(right.Y * fwd.Z - right.Z * fwd.Y, right.Z * fwd.X - right.X * fwd.Z, right.X * fwd.Y - right.Y * fwd.X);
        return (right, up, fwd);
    }

    /// <summary>view-space point (y up) -> screen point, depth; null behind the camera</summary>
    (Point P, double Depth, double Scale)? Project(V3 w)
    {
        var (right, up, fwd) = Basis();
        var eye = target - fwd * dist;
        var d = w - eye;
        double x = d.X * right.X + d.Y * right.Y + d.Z * right.Z;
        double y = d.X * up.X + d.Y * up.Y + d.Z * up.Z;
        double z = d.X * fwd.X + d.Y * fwd.Y + d.Z * fwd.Z;
        if (z < 1) return null;
        double f = Bounds.Height * 1.25;
        return (new Point(Bounds.Width / 2 + x / z * f, Bounds.Height / 2 - y / z * f), z, f / z);
    }

    static V3 View(V3 g) => new(g.X, -g.Y, g.Z);   // game (y down) -> view (y up)

    // ---------------------------------------------------------------- drawing
    public override void Render(DrawingContext ctx)
    {
        var b = new Rect(Bounds.Size);
        ctx.FillRectangle(new LinearGradientBrush
        {
            StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative), EndPoint = new RelativePoint(0, 1, RelativeUnit.Relative),
            GradientStops = { new GradientStop(Color.FromRgb(0x14, 0x19, 0x24), 0), new GradientStop(Color.FromRgb(0x08, 0x0A, 0x0F), 1) },
        }, b);
        DrawRing(ctx);
        if (skel == null) return;
        int f = (int)Math.Round(frame);
        var A = f >= 0 && f < att.Count ? att[f] : att.LastOrDefault();
        var R = f >= 0 && f < rec.Count ? rec[f] : rec.LastOrDefault();
        var Pp = f >= 0 && f < par.Count ? par[f] : par.LastOrDefault();
        // shadows first (on the floor), then the bodies depth-sorted together
        foreach (var P in new[] { A, R, Pp })
            if (P != null) DrawShadow(ctx, P);
        var items = new List<(double Depth, Action Draw)>();
        if (A != null) Collect(ctx, items, A, AttackerColor);
        if (R != null) Collect(ctx, items, R, ReceiverColor);
        if (Pp != null) Collect(ctx, items, Pp, PartnerColor);
        CollectRing(ctx, items);
        foreach (var it in items.OrderByDescending(i => i.Depth)) it.Draw();
        DrawGizmo(ctx);
    }

    // ---------------------------------------------------------------- the ring (instead of a plain grid)
    // A 2011-size ring around the attacker's start spot: mat ~68 units across (a 20 ft ring for ~18-unit wrestlers),
    // apron, four posts and three ropes. Posts and ropes are depth-sorted with the bodies.
    const double RingHalf = 31.5, ApronHalf = 36, PostH = 13;   // the ropes: wrestlers on the apron stand at 32-34 (previews)
    const double FloorDrop = 12 / 0.955;                       // the arena floor is 12 (game) below the mat
    static readonly double[] RopeH = { 5.0, 8.5, 12.0 };

    void DrawRing(DrawingContext ctx)
    {
        // the apron and the mat (filled where all four corners are in front of the camera)
        Fill(ctx, ApronHalf, new SolidColorBrush(Color.FromRgb(0x10, 0x13, 0x1A)));
        Fill(ctx, RingHalf, new SolidColorBrush(Color.FromRgb(0x17, 0x1C, 0x26)));
        var weave = new Pen(new SolidColorBrush(Color.FromArgb(26, 170, 190, 220)), 1);
        for (double i = -RingHalf + 8.5; i < RingHalf - 0.1; i += 8.5)
        {
            Line(ctx, weave, new V3(i, floorY, -RingHalf), new V3(i, floorY, RingHalf));
            Line(ctx, weave, new V3(-RingHalf, floorY, i), new V3(RingHalf, floorY, i));
        }
        var edge = new Pen(new SolidColorBrush(Color.FromArgb(150, 0x3B, 0x8C, 0xFF)), 1.6);
        var outer = new Pen(new SolidColorBrush(Color.FromArgb(60, 160, 175, 200)), 1);
        Square(ctx, edge, RingHalf);
        Square(ctx, outer, ApronHalf);
        // the centre logo ring
        var logo = new Pen(new SolidColorBrush(Color.FromArgb(40, 0x22, 0xC7, 0xE8)), 1.2);
        V3? last = null;
        for (int k = 0; k <= 48; k++)
        {
            double a = k * Math.PI * 2 / 48;
            var p = new V3(Math.Cos(a) * 9, floorY, Math.Sin(a) * 9);
            if (last is V3 l) Line(ctx, logo, l, p);
            last = p;
        }
    }

    void Fill(DrawingContext ctx, double h, IBrush brush)
    {
        var pts = new[] { new V3(-h, floorY, -h), new V3(h, floorY, -h), new V3(h, floorY, h), new V3(-h, floorY, h) }.Select(Project).ToList();
        if (pts.Any(p => p == null)) return;
        var geo = new StreamGeometry();
        using (var g = geo.Open())
        {
            g.BeginFigure(pts[0]!.Value.P, true);
            foreach (var p in pts.Skip(1)) g.LineTo(p!.Value.P);
            g.EndFigure(true);
        }
        ctx.DrawGeometry(brush, null, geo);
    }

    void Square(DrawingContext ctx, IPen pen, double h)
    {
        Line(ctx, pen, new V3(-h, floorY, -h), new V3(h, floorY, -h));
        Line(ctx, pen, new V3(h, floorY, -h), new V3(h, floorY, h));
        Line(ctx, pen, new V3(h, floorY, h), new V3(-h, floorY, h));
        Line(ctx, pen, new V3(-h, floorY, h), new V3(-h, floorY, -h));
    }

    void CollectRing(DrawingContext ctx, List<(double Depth, Action Draw)> items)
    {
        var corners = new[] { new V3(-RingHalf, 0, -RingHalf), new V3(RingHalf, 0, -RingHalf), new V3(RingHalf, 0, RingHalf), new V3(-RingHalf, 0, RingHalf) };
        var rope = new Pen(new SolidColorBrush(Color.FromArgb(185, 0xD5, 0xDA, 0xE3)), 1.6, lineCap: PenLineCap.Round);
        for (int s = 0; s < 4; s++)
        {
            var a = corners[s]; var b = corners[(s + 1) % 4];
            foreach (double rh in RopeH)
                for (int k = 0; k < 8; k++)
                {
                    var p0 = V3.Lerp(a, b, k / 8.0) + new V3(0, floorY + rh, 0);
                    var p1 = V3.Lerp(a, b, (k + 1) / 8.0) + new V3(0, floorY + rh, 0);
                    if (Project(p0) is not { } q0 || Project(p1) is not { } q1) continue;
                    double w = Math.Max(1, 0.22 * (q0.Scale + q1.Scale) / 2);
                    var pen = new Pen(rope.Brush, w, lineCap: PenLineCap.Round);
                    items.Add(((q0.Depth + q1.Depth) / 2, (Action)(() => ctx.DrawLine(pen, q0.P, q1.P))));
                }
        }
        foreach (var c0 in corners)
        {
            if (Project(c0 + new V3(0, floorY, 0)) is not { } qb || Project(c0 + new V3(0, floorY + PostH, 0)) is not { } qt) continue;
            double w = Math.Max(2, 0.9 * qb.Scale);
            var post = new Pen(new SolidColorBrush(Color.FromRgb(0x4A, 0x52, 0x62)), w, lineCap: PenLineCap.Round);
            var shine = new Pen(new SolidColorBrush(Color.FromArgb(120, 0xB8, 0xC4, 0xD8)), Math.Max(1, w * 0.3), lineCap: PenLineCap.Round);
            items.Add((qb.Depth, (Action)(() =>
            {
                ctx.DrawLine(post, qb.P, qt.P);
                ctx.DrawLine(shine, qb.P + new Vector(-w * 0.2, 0), qt.P + new Vector(-w * 0.2, 0));
                foreach (double rh in RopeH)
                    if (Project(c0 + new V3(0, floorY + rh, 0)) is { } qp)
                        ctx.DrawEllipse(new SolidColorBrush(Color.FromRgb(0x3B, 0x8C, 0xFF)), null, qp.P, w * 0.75, w * 0.6);
            })));
        }
    }

    void DrawGrid(DrawingContext ctx)
    {
        var minor = new Pen(new SolidColorBrush(Color.FromArgb(34, 160, 170, 190)), 1);
        var major = new Pen(new SolidColorBrush(Color.FromArgb(70, 160, 170, 190)), 1);
        double cx = Math.Round(target.X / 4) * 4, cz = Math.Round(target.Z / 4) * 4;
        double gy = floorY - FloorDrop;   // the arena floor, below the raised ring
        const int half = 48;
        for (int i = -half; i <= half; i += 4)
        {
            var pen = i % 16 == 0 ? major : minor;
            Line(ctx, pen, new V3(cx + i, gy, cz - half), new V3(cx + i, gy, cz + half));
            Line(ctx, pen, new V3(cx - half, gy, cz + i), new V3(cx + half, gy, cz + i));
        }
    }

    void Line(DrawingContext ctx, IPen pen, V3 a, V3 b)
    {
        // clip a grid line to the camera (split it so the near part doesn't flip)
        const int steps = 8;
        Point? last = null;
        for (int s = 0; s <= steps; s++)
        {
            var p = Project(V3.Lerp(a, b, s / (double)steps));
            if (p is { } q && last is Point l) ctx.DrawLine(pen, l, q.P);
            last = p?.P;
        }
    }

    void DrawShadow(DrawingContext ctx, V3[] P)
    {
        var brush = new SolidColorBrush(Color.FromArgb(70, 0, 0, 0));
        foreach (var (i, j, r) in caps)
        {
            var a = View(P[i]); var c = View(P[j]);
            double h = Math.Max(0, (a.Y + c.Y) / 2 - floorY);
            double fade = Math.Clamp(1.0 - h / 14.0, 0, 1);
            if (fade <= 0.02) continue;
            var pa = Project(new V3(a.X, floorY + 0.02, a.Z)); var pc = Project(new V3(c.X, floorY + 0.02, c.Z));
            if (pa is not { } qa || pc is not { } qc) continue;
            var pen = new Pen(new SolidColorBrush(Color.FromArgb((byte)(60 * fade), 0, 0, 0)), Math.Max(1, r * 2.6 * qa.Scale), lineCap: PenLineCap.Round);
            ctx.DrawLine(pen, qa.P, qc.P);
        }
        _ = brush;
    }

    static Color Shade(Color c, double k) => Color.FromRgb((byte)Math.Clamp(c.R * k, 0, 255), (byte)Math.Clamp(c.G * k, 0, 255), (byte)Math.Clamp(c.B * k, 0, 255));

    void Collect(DrawingContext ctx, List<(double Depth, Action Draw)> items, V3[] P, Color baseColor)
    {
        var light = new V3(-0.35, 0.85, 0.4);
        foreach (var (i, j, r) in caps)
        {
            var a = View(P[i]); var c = View(P[j]);
            var pa = Project(a); var pc = Project(c);
            if (pa is not { } qa || pc is not { } qc) continue;
            var dir = c - a;
            double len = Math.Max(1e-6, dir.Length);
            // a tube lit from above: brighter when it lies across the light, darker when it points along it
            double along = Math.Abs((dir.X * light.X + dir.Y * light.Y + dir.Z * light.Z) / len);
            double k = 0.62 + 0.38 * Math.Sqrt(Math.Max(0, 1 - along * along));
            double depthK = Math.Clamp(1.15 - (qa.Depth + qc.Depth) / 2 / 240, 0.75, 1.1);
            double w = Math.Max(1.2, r * 2 * (qa.Scale + qc.Scale) / 2);
            var col = Shade(baseColor, k * depthK);
            var hi = Shade(baseColor, Math.Min(1.45, k * depthK * 1.32));
            var dark = Shade(baseColor, k * depthK * 0.62);
            double depth = (qa.Depth + qc.Depth) / 2;
            items.Add((depth, (Action)(() =>
            {
                ctx.DrawLine(new Pen(new SolidColorBrush(dark), w + 1.6, lineCap: PenLineCap.Round), qa.P, qc.P);
                ctx.DrawLine(new Pen(new SolidColorBrush(col), w, lineCap: PenLineCap.Round), qa.P, qc.P);
                var off = new Vector(-w * 0.18, -w * 0.22);
                ctx.DrawLine(new Pen(new SolidColorBrush(Color.FromArgb(150, hi.R, hi.G, hi.B)), w * 0.36, lineCap: PenLineCap.Round), qa.P + off, qc.P + off);
            })));
        }
        if (head >= 0)
        {
            var hc = View(P[head]);
            if (eyeL >= 0 && eyeR >= 0)
            {
                var em = (View(P[eyeL]) + View(P[eyeR])) * 0.5;
                hc = V3.Lerp(hc, em, 0.55);
            }
            if (Project(hc) is { } qh)
            {
                double rad = 1.28 * qh.Scale;
                var col = Shade(baseColor, Math.Clamp(1.15 - qh.Depth / 240, 0.75, 1.1));
                items.Add((qh.Depth, (Action)(() =>
                {
                    var g = new RadialGradientBrush
                    {
                        Center = new RelativePoint(0.36, 0.32, RelativeUnit.Relative),
                        GradientOrigin = new RelativePoint(0.36, 0.32, RelativeUnit.Relative),
                        RadiusX = new RelativeScalar(0.75, RelativeUnit.Relative), RadiusY = new RelativeScalar(0.75, RelativeUnit.Relative),
                        GradientStops = { new GradientStop(Shade(col, 1.3), 0), new GradientStop(col, 0.55), new GradientStop(Shade(col, 0.6), 1) },
                    };
                    ctx.DrawEllipse(g, new Pen(new SolidColorBrush(Shade(col, 0.5)), 1), qh.P, rad, rad * 1.08);
                })));
            }
        }
    }
}
