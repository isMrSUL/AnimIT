using System;
using System.Diagnostics;
using System.Threading;
using Avalonia;
using Avalonia.Headless;
using Avalonia.Threading;

namespace AnimIT;

/// <summary>--render out.png [seconds] [w h]: draws the real main window off-screen (headless Skia) and saves it - for
/// checking the look without a window popping up (env ANIMIT_GAME / ANIMIT_DEMO / ANIMIT_DEMO_AT as for a normal start)</summary>
static class Render
{
    /// <summary>--logo dir: the logo (Controls\LogoMark) as PNGs at icon / README sizes</summary>
    public static int Logo(string dir)
    {
        AppBuilder.Configure<App>().UseSkia().WithInterFont()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false }).SetupWithoutStarting();
        System.IO.Directory.CreateDirectory(dir);
        foreach (int n in new[] { 16, 20, 24, 32, 40, 48, 64, 128, 256, 512, 1024 })
        {
            var mark = new Controls.LogoMark { Width = n, Height = n };
            mark.Measure(new Size(n, n));
            mark.Arrange(new Rect(0, 0, n, n));
            using var bmp = new Avalonia.Media.Imaging.RenderTargetBitmap(new PixelSize(n, n), new Vector(96, 96));
            bmp.Render(mark);
            bmp.Save(System.IO.Path.Combine(dir, $"animit_{n}.png"));
        }
        Console.WriteLine("logo written to " + dir);
        return 0;
    }

    public static int Porter(string game, string outPath)
    {
        var cat = new Core.GameCatalog(game);
        AppBuilder.Configure<App>().UseSkia().WithInterFont()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false }).SetupWithoutStarting();
        string tmpl = System.IO.Path.Combine(game, "pac", "ch", "ch125.pac");
        // ANIMIT_PORTER_PACK=<built pack folder>|<move id>: show that built move on the stage
        Core.AnimationPorter.BuiltPack? built = Environment.GetEnvironmentVariable("ANIMIT_PORTER_PACK") is string pk && pk.Split('|') is { Length: 2 } pp
            ? new(pp[0], new[] { int.Parse(pp[1]) }, "(render check)") : null;
        var win = new PorterDialog(cat, System.IO.File.Exists(tmpl) ? Core.Skeleton.Load(tmpl) : null, built);
        win.Show();
        // ANIMIT_PORTER_SOURCE=<source>|<move>: read that source move onto the stage
        double wait = 3;
        if (Environment.GetEnvironmentVariable("ANIMIT_PORTER_SOURCE") is string ps && ps.Split("|") is { Length: >= 2 } sp) { win.TestSource(sp[0], sp[1], sp.Length > 2 ? sp[2] : null); wait = 25; }
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed.TotalSeconds < wait)
        {
            Dispatcher.UIThread.RunJobs(); AvaloniaHeadlessPlatform.ForceRenderTimerTick(); Thread.Sleep(30);
        }
        var frame = win.CaptureRenderedFrame();
        if (frame == null) return 1;
        frame.Save(outPath);
        win.Close();
        return 0;
    }

    public static int Run(string outPath, double seconds, int w, int h)
    {
        AppBuilder.Configure<App>().UseSkia().WithInterFont()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
            .SetupWithoutStarting();
        var win = new MainWindow { Width = w, Height = h };
        win.Show();
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed.TotalSeconds < seconds)
        {
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Thread.Sleep(30);
        }
        Dispatcher.UIThread.RunJobs();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        var frame = win.CaptureRenderedFrame();
        if (frame == null) { Console.WriteLine("no frame"); return 1; }
        frame.Save(outPath);
        Console.WriteLine($"saved {outPath} ({frame.PixelSize.Width} x {frame.PixelSize.Height})");
        return 0;
    }
}
