using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using AnimIT.Core;

namespace AnimIT;

/// <summary>shared look for the small dialogs</summary>
static class Ui
{
    public static IBrush Res(string k) => (IBrush)Application.Current!.FindResource(k)!;
    public static TextBlock H(string s) => new() { Text = s, Classes = { "h" }, Margin = new Thickness(0, 0, 0, 6) };
    public static TextBlock T(string s, string? cls = null)
    {
        var t = new TextBlock { Text = s, TextWrapping = TextWrapping.Wrap };
        if (cls != null) t.Classes.Add(cls);
        return t;
    }
    public static Border Box(Control c) => new() { Classes = { "box" }, Child = c };

    /// <summary>a move list with a check box on every row and a right-click menu: Copy name / Copy id of that row,
    /// Copy checked names / ids (one per line, in the order they were checked), Check all shown, Uncheck all.
    /// info: row item -> (name, id), null for rows without one. Call after the list's ItemTemplate is set (it is wrapped).</summary>
    public static void CopyMenu(ListBox list, Func<object, (string Name, string Id)?> info, Action<string>? done = null)
    {
        var picked = new List<(string Name, string Id)>();
        var boxes = new List<(WeakReference<CheckBox> Box, string Key)>();
        static string Key((string Name, string Id) i) => i.Id + "|" + i.Name;
        bool syncing = false;

        void Sync()
        {
            syncing = true;
            var keys = picked.Select(Key).ToHashSet();
            boxes.RemoveAll(b => !b.Box.TryGetTarget(out _));
            foreach (var (wb, key) in boxes)
                if (wb.TryGetTarget(out var cb)) cb.IsChecked = keys.Contains(key);
            syncing = false;
        }

        var inner = list.ItemTemplate;
        list.ItemTemplate = new Avalonia.Controls.Templates.FuncDataTemplate<object>((o, _) =>
        {
            var content = inner?.Build(o) ?? new TextBlock { Text = o?.ToString() };
            if (o == null || info(o) is not { } i) return content;
            string key = Key(i);
            var cb = new CheckBox { IsChecked = picked.Any(p => Key(p) == key), VerticalAlignment = VerticalAlignment.Center, MinWidth = 0, CornerRadius = new CornerRadius(4), Margin = new Thickness(0, 0, 4, 0) };
            ToolTip.SetTip(cb, "Check to copy several move names at once (right click)");
            cb.IsCheckedChanged += (_, _) =>
            {
                if (syncing) return;
                picked.RemoveAll(p => Key(p) == key);
                if (cb.IsChecked == true) picked.Add(i);
            };
            boxes.Add((new WeakReference<CheckBox>(cb), key));
            var g = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*") };
            g.Children.Add(cb);
            Grid.SetColumn(content!, 1);
            g.Children.Add(content!);
            return g;
        });

        async void Put(string text, string what)
        {
            if (TopLevel.GetTopLevel(list)?.Clipboard is { } clip)
            {
                await clip.SetTextAsync(text);
                done?.Invoke($"Copied {what}");
            }
        }

        list.ContainerPrepared += (_, e) =>
        {
            var row = e.Container;
            var copyName = new MenuItem { Header = "Copy name" };
            var copyId = new MenuItem { Header = "Copy id" };
            var copyChecked = new MenuItem { Header = "Copy checked names" };
            var copyCheckedIds = new MenuItem { Header = "Copy checked ids" };
            var checkAll = new MenuItem { Header = "Check all shown" };
            var uncheck = new MenuItem { Header = "Uncheck all" };
            copyName.Click += (_, _) => { if (row.DataContext is { } o && info(o) is { } i) Put(i.Name, ": " + i.Name); };
            copyId.Click += (_, _) => { if (row.DataContext is { } o && info(o) is { } i) Put(i.Id, ": " + i.Id); };
            copyChecked.Click += (_, _) => { if (picked.Count > 0) Put(string.Join(Environment.NewLine, picked.Select(p => p.Name)), $"{picked.Count} move name(s)"); };
            copyCheckedIds.Click += (_, _) => { if (picked.Count > 0) Put(string.Join(Environment.NewLine, picked.Select(p => p.Id)), $"{picked.Count} move id(s)"); };
            checkAll.Click += (_, _) =>
            {
                var keys = picked.Select(Key).ToHashSet();
                foreach (var o in list.ItemsSource?.Cast<object>() ?? Enumerable.Empty<object>())
                    if (info(o) is { } i && keys.Add(Key(i))) picked.Add(i);
                Sync();
                done?.Invoke($"{picked.Count} move(s) checked");
            };
            uncheck.Click += (_, _) => { picked.Clear(); Sync(); done?.Invoke("All checks cleared"); };
            var menu = new ContextMenu { Items = { copyName, copyId, new Separator(), copyChecked, copyCheckedIds, new Separator(), checkAll, uncheck } };
            menu.Opening += (_, _) =>
            {
                copyChecked.Header = $"Copy checked names ({picked.Count})";
                copyCheckedIds.Header = $"Copy checked ids ({picked.Count})";
                copyChecked.IsEnabled = copyCheckedIds.IsEnabled = uncheck.IsEnabled = picked.Count > 0;
            };
            row.ContextMenu = menu;
        };
    }
    public static Window Dialog(string title, double w, double h) => new()
    {
        Title = title, Width = w, Height = h, CanResize = false,
        WindowStartupLocation = WindowStartupLocation.CenterOwner, ShowInTaskbar = false,
    };
}

/// <summary>Import animation into a lane: pick a move, then the track (its attacker or receiver)</summary>
public sealed class ImportDialog : Window
{
    sealed class Row
    {
        public required MoveInfo M;
        public override string ToString() => $"{M.Name}   ·   move {M.Id}" + (M.Pack != null ? $"   ·   {M.Pack}" : "");
    }

    sealed class TrackRow
    {
        public required LaneRef R; public int Frames;
        public override string ToString() => $"{R.Label}   ·   {(R.Y == 0 ? "attacker" : R.Y == 1 ? "receiver" : $"track {R.Y}")} side   ·   {Frames} f";
    }

    readonly ListBox moves = new() { Height = 300 };
    readonly ListBox tracks = new() { Height = 110 };
    readonly TextBox search = new() { Watermark = "Search moves…" };
    readonly Button ok = new() { Content = "Import", Classes = { "accent" }, IsEnabled = false, MinWidth = 90 };
    readonly List<Row> all;

    public ImportDialog(GameCatalog cat, int lane)
    {
        Title = "Swap a track";
        Width = 620; Height = 640; CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner; ShowInTaskbar = false;
        all = cat.Moves.Values.Where(m => m.Usable || m.Pack != null).OrderBy(m => m.Name, StringComparer.OrdinalIgnoreCase)
                 .Select(m => new Row { M = m }).ToList();
        var cancel = new Button { Content = "Cancel", MinWidth = 90 };
        cancel.Click += (_, _) => Close(null);
        ok.Click += (_, _) => Close((tracks.SelectedItem as TrackRow)?.R);
        search.TextChanged += (_, _) => Fill();
        moves.SelectionChanged += (_, _) =>
        {
            var m = (moves.SelectedItem as Row)?.M;
            var l = m == null ? new List<TrackRow>() : m.Tracks.Select(kv => new TrackRow
            { R = new LaneRef(m.Id, kv.Key.X, kv.Key.Y, $"{m.Id}_{kv.Key.X}_{kv.Key.Y}"), Frames = kv.Value }).ToList();
            tracks.ItemsSource = l;
            // the lane's own side first: an attacker lane takes the attacker track, a receiver lane the receiver's
            tracks.SelectedItem = l.FirstOrDefault(t => t.R.X == 0 && t.R.Y == lane) ?? l.FirstOrDefault();
        };
        tracks.SelectionChanged += (_, _) => ok.IsEnabled = tracks.SelectedItem != null;
        tracks.DoubleTapped += (_, _) => { if (ok.IsEnabled) Close((tracks.SelectedItem as TrackRow)?.R); };
        Content = new StackPanel
        {
            Margin = new Thickness(18), Spacing = 10,
            Children =
            {
                Ui.T("Swap a track", "title"),
                Ui.T($"New animation for the {(lane == 0 ? "attacker" : "receiver")} of this clip: any move's attacker or receiver track.", "sub"),
                search,
                Ui.H("From move"), moves,
                Ui.H("Track"), tracks,
                new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right, Children = { cancel, ok } },
            },
        };
        Fill();
    }

    void Fill()
    {
        string q = (search.Text ?? "").Trim();
        moves.ItemsSource = all.Where(r => q.Length == 0 || r.M.Name.Contains(q, StringComparison.OrdinalIgnoreCase) || r.M.Id.ToString() == q).Take(500).ToList();
    }
}

/// <summary>Speed / Duration: play a source slower or faster (the ADEF "interpret at" fps)</summary>
public sealed class SpeedDialog : Window
{
    readonly Block b;
    readonly NumericUpDown fps = new() { Minimum = 5, Maximum = 240, Increment = 1, FormatString = "0.#", Width = 130 };
    readonly NumericUpDown frames = new() { Minimum = 1, Maximum = 100000, Increment = 1, FormatString = "0", Width = 130 };
    readonly TextBlock result = new() { TextWrapping = TextWrapping.Wrap };
    bool busy;
    int Src => b.Out - b.In + 1;

    public SpeedDialog(Block block, LaneRef src, string name)
    {
        b = block;
        Title = "Speed / Duration";
        Width = 470; Height = 470; CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner; ShowInTaskbar = false;
        fps.Value = (decimal)Math.Round(30 * b.Speed, 1);
        frames.Value = b.Length;
        fps.ValueChanged += (_, _) => { if (busy) return; busy = true; frames.Value = Math.Max(1, (int)Math.Round(Src * 30 / (double)(fps.Value ?? 30))); busy = false; Refresh(); };
        frames.ValueChanged += (_, _) => { if (busy) return; busy = true; fps.Value = (decimal)Math.Round(Src * 30 / (double)Math.Max(1, frames.Value ?? 1), 1); busy = false; Refresh(); };
        var quick = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        foreach (int p in new[] { 50, 75, 100, 150, 200 })
        {
            var q = new Button { Content = $"{p}%", MinWidth = 56 };
            q.Click += (_, _) => fps.Value = 30m * p / 100;
            quick.Children.Add(q);
        }
        var cancel = new Button { Content = "Cancel", MinWidth = 90 };
        cancel.Click += (_, _) => Close(false);
        var apply = new Button { Content = "Apply", Classes = { "accent" }, MinWidth = 90 };
        apply.Click += (_, _) => { b.Speed = Math.Max(0.05, (double)(fps.Value ?? 30) / 30); Close(true); };
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("150,*"), RowDefinitions = new RowDefinitions("Auto,8,Auto,8,Auto") };
        void Put(int r, string label, Control c)
        {
            var t = Ui.T(label, "sub"); t.VerticalAlignment = VerticalAlignment.Center;
            Grid.SetRow(t, r); Grid.SetRow(c, r); Grid.SetColumn(c, 1);
            grid.Children.Add(t); grid.Children.Add(c);
        }
        Put(0, "Made at", Ui.T("30 frames a second"));
        Put(2, "Play at (frames/s)", fps);
        Put(4, "Length (frames)", frames);
        Content = new StackPanel
        {
            Margin = new Thickness(18), Spacing = 12,
            Children =
            {
                Ui.T("Speed and length", "title"),
                Ui.T($"{src.Label}  ·  {name}  ·  frames {b.In} – {b.Out} ({Src} source frames)", "sub"),
                Ui.Box(grid),
                Ui.H("Quick speed"), quick,
                Ui.Box(result),
                new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right, Children = { cancel, apply } },
            },
        };
        Refresh();
    }

    void Refresh()
    {
        double sp = (double)(fps.Value ?? 30) / 30;
        int n = Math.Max(1, (int)Math.Round(Src / Math.Max(0.05, sp)));
        result.Text = $"Speed {sp * 100:0.#}%  ·  {n} output frames  ·  {n / 30.0:0.00} s at 30 FPS";
    }
}

/// <summary>Save to game: the move's name, then the pack is written (Mods\Moves\animit_moves)</summary>
/// <summary>what the save dialog returns: the move's name and its donor (the stock move whose list, damage and match
/// records the new move takes)</summary>
public sealed record SaveChoice(string Name, int Donor);

public sealed class SaveDialog : Window
{
    readonly TextBox name = new();
    readonly ComboBox kind = new() { HorizontalAlignment = HorizontalAlignment.Stretch };
    readonly TextBox search = new() { Watermark = "Search a donor move..." };
    readonly ListBox list = new() { Height = 190 };
    readonly TextBlock where = new() { TextWrapping = TextWrapping.Wrap, FontWeight = FontWeight.SemiBold };
    readonly TextBlock parts = new() { TextWrapping = TextWrapping.Wrap, Classes = { "sub" } };
    readonly Button save = new() { Content = "Save", Classes = { "accent" }, MinWidth = 90 };
    readonly List<MoveInfo> donors;
    readonly GameCatalog cat;
    sealed record Row(MoveInfo M, string Text) { public override string ToString() => Text; }

    /// <param name="usable">the moves that can be donors (stock, in a list, with match records)</param>
    public SaveDialog(GameCatalog cat, string suggested, int? defaultDonor, IEnumerable<MoveInfo> usable, int frames, string packDir)
    {
        this.cat = cat;
        Title = "Save to game";
        Width = 600; SizeToContent = SizeToContent.Height; CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner; ShowInTaskbar = false;
        name.Text = suggested;
        donors = usable.OrderBy(m => cat.Names.GetValueOrDefault(m.Id) ?? m.Name, StringComparer.OrdinalIgnoreCase).ToList();
        var kinds = MoveCategory.Names.Where(k => donors.Any(m => m.Category == k)).ToList();
        kind.ItemsSource = kinds.Select(k => $"{k}  ({donors.Count(m => m.Category == k)} moves)").ToList();
        var cancel = new Button { Content = "Cancel", MinWidth = 90 };
        cancel.Click += (_, _) => Close(null);
        save.Click += (_, _) =>
        {
            var n = (name.Text ?? "").Trim();
            if (n.Length > 0 && list.SelectedItem is Row r) Close(new SaveChoice(n, r.M.Id));
        };
        name.TextChanged += (_, _) => Check();
        kind.SelectionChanged += (_, _) => Fill(null);
        search.TextChanged += (_, _) => Fill((list.SelectedItem as Row)?.M.Id);
        list.SelectionChanged += (_, _) => { Explain(); Check(); };
        Content = new StackPanel
        {
            Margin = new Thickness(18), Spacing = 10,
            Children =
            {
                Ui.T("Save to game", "title"),
                Ui.T("The timeline becomes a new move in your game, as a move pack.", "sub"),
                Ui.H("Move name"), name,
                Ui.H("What kind of move is it?"),
                Ui.T("This decides where the move is in Create-a-Moveset and how the game starts it (a grapple, a strike, a dive...).", "faint"),
                kind,
                Ui.T("Donor: the game move it copies. The new move takes the donor's list, damage and match records.", "faint"),
                search, list,
                Ui.Box(new StackPanel
                {
                    Spacing = 6,
                    Children =
                    {
                        where, parts,
                        Ui.T($"{frames} frames ({frames / 30.0:0.0} s) at 30 FPS", "faint"),
                        Ui.T($"Pack: {packDir}", "faint"),
                        Ui.T("Saving the same name again replaces that move. The old pack.txt is kept as pack.txt.bak. Restart the game to see it.", "faint"),
                    },
                }),
                new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right, Children = { cancel, save } },
            },
        };
        // start on the first clip's own move when it can be a donor
        var start = defaultDonor is int d ? donors.FirstOrDefault(m => m.Id == d) : null;
        int ki = start != null ? kinds.IndexOf(start.Category) : 0;
        kind.SelectedIndex = Math.Max(0, ki);
        Fill(start?.Id);
    }

    string Kind => kind.SelectedIndex >= 0 && kind.ItemsSource is List<string> l ? MoveCategory.Names.First(k => l[kind.SelectedIndex].StartsWith(k + "  (")) : "";

    void Fill(int? select)
    {
        string q = (search.Text ?? "").Trim();
        var rows = donors.Where(m => m.Category == Kind)
                         .Select(m => new Row(m, $"{cat.Names.GetValueOrDefault(m.Id) ?? m.Name}   ({m.Id})"))
                         .Where(r => q.Length == 0 || r.Text.Contains(q, StringComparison.OrdinalIgnoreCase)).ToList();
        list.ItemsSource = rows;
        list.SelectedItem = rows.FirstOrDefault(r => r.M.Id == select) ?? rows.FirstOrDefault();
        if (list.SelectedItem != null) list.ScrollIntoView(list.SelectedItem);
        Explain(); Check();
    }

    void Check() => save.IsEnabled = (name.Text ?? "").Trim().Length > 0 && list.SelectedItem is Row;

    void Explain()
    {
        if (list.SelectedItem is not Row r) { where.Text = "Choose a donor move."; parts.Text = ""; return; }
        var m = r.M;
        string dn = cat.Names.GetValueOrDefault(m.Id) ?? m.Name;
        where.Text = $"In Create-a-Moveset it will be in the same list as {dn}  ({m.Category}).";
        Func<int, Track?> none = _ => null;
        var shown = m.Phases.Where(x => PhaseGuide.Shown(m, x)).ToList();
        var others = shown.Where(x => x != m.MainPhase).Select(x => PhaseGuide.Name(m, x, none)).Distinct().ToList();
        parts.Text = others.Count == 0
            ? "Your timeline becomes the whole move."
            : $"Your timeline becomes its \"{PhaseGuide.Name(m, m.MainPhase, none)}\". The game still plays the donor's own {string.Join(", ", others)} " +
              "around it (if your first clip is this donor's move, your timeline replaces the part that clip is from).";
    }
}

/// <summary>a plain message with an OK button</summary>
public sealed class MessageDialog : Window
{
    public MessageDialog(string title, string head, string body)
    {
        Title = title;
        Width = 560; SizeToContent = SizeToContent.Height; CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner; ShowInTaskbar = false;
        var ok = new Button { Content = "OK", Classes = { "accent" }, MinWidth = 90, HorizontalAlignment = HorizontalAlignment.Right };
        ok.Click += (_, _) => Close();
        Content = new StackPanel
        {
            Margin = new Thickness(18), Spacing = 12,
            Children = { Ui.T(head, "title"), Ui.Box(new SelectableTextBlock { Text = body, TextWrapping = TextWrapping.Wrap }), ok },
        };
    }
}
