using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace AnimIT.Controls;

/// <summary>The AnimIT logo: an "A" drawn from two motion curves that cross, with a keyframe track as its crossbar
/// (a splice). Drawn in a 160 x 160 design space and scaled; below 40 px the strokes are thicker so the A still
/// reads at icon sizes. The same drawing makes the app icon and the README images (--logo).</summary>
public sealed class LogoMark : Control
{
    static readonly IBrush Tile = new LinearGradientBrush
    {
        StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
        EndPoint = new RelativePoint(0, 1, RelativeUnit.Relative),
        GradientStops = { new GradientStop(Color.Parse("#17335F"), 0), new GradientStop(Color.Parse("#0B1426"), 1) },
    };
    static readonly IPen Rim = new Pen(new SolidColorBrush(Color.Parse("#2E5490")), 2.5);
    static readonly IBrush Light = new SolidColorBrush(Color.Parse("#62B8FF"));
    static readonly IBrush Deep = new SolidColorBrush(Color.Parse("#3B8CFF"));
    static readonly IBrush Cyan = new SolidColorBrush(Color.Parse("#22C7E8"));
    static readonly IBrush Key = new SolidColorBrush(Color.Parse("#EEF3FA"));
    static readonly Geometry Left = Geometry.Parse("M 30,130 C 58,128 66,40 80,30");
    static readonly Geometry Right = Geometry.Parse("M 80,30 C 94,40 102,128 130,130");
    static readonly Geometry Diamond = Geometry.Parse("M 80,85 L 93,98 L 80,111 L 67,98 Z");

    /// <summary>draw the tile behind the mark (false: the mark alone, on whatever is behind)</summary>
    public bool ShowTile { get; set; } = true;

    public override void Render(DrawingContext ctx)
    {
        double size = System.Math.Min(Bounds.Width, Bounds.Height);
        if (size <= 0) return;
        double s = size / 160.0;
        bool small = size < 40;
        double w = small ? 15 : 12;                    // curve width (design units)
        using (ctx.PushTransform(Matrix.CreateTranslation((Bounds.Width - size) / 2 / s, (Bounds.Height - size) / 2 / s) * Matrix.CreateScale(s, s)))
        {
            if (ShowTile)
            {
                ctx.DrawRectangle(Tile, small ? null : Rim, new RoundedRect(new Rect(1.25, 1.25, 157.5, 157.5), 36));
            }
            ctx.DrawGeometry(null, new Pen(Light, w, lineCap: PenLineCap.Round), Left);
            ctx.DrawGeometry(null, new Pen(Deep, w, lineCap: PenLineCap.Round), Right);
            // the keyframe track (crossbar) and its key
            ctx.DrawLine(new Pen(Cyan, small ? 8 : 5.5, lineCap: PenLineCap.Round), new Point(46, 98), new Point(114, 98));
            ctx.DrawGeometry(Key, null, Diamond);
            if (!small)
            {
                ctx.DrawEllipse(Cyan, null, new Point(30, 130), 7.5, 7.5);
                ctx.DrawEllipse(Cyan, null, new Point(130, 130), 7.5, 7.5);
            }
        }
    }
}
