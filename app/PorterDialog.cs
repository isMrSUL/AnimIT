using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Shapes;
using Avalonia.Controls.Templates;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using AnimIT.Controls;
using AnimIT.Core;
using Path = System.IO.Path;

namespace AnimIT;

/// <summary>Source discovery, donor choice, offline conversion, a 3D look at the donor and the result, then installation.
/// Laid out like the main window: source moves | stage | donor + options, with a status bar.</summary>
public sealed class PorterDialog : Window
{
    sealed record Donor(int Id, string Name, string Category, int Rank = 0, List<(string State, string Text)>? Checks = null)
    {
        public override string ToString() => Id == 0 ? "Choose automatically (HCTP)" : $"{Name}  ·  {Id}  ·  {Category}";
    }
    static readonly string[] SourceKeys = { "hctp", "k26", "aew" };
    static readonly string[] SourceNames = { "WWE HCTP", "WWE 2K26", "AEW Fight Forever" };

    readonly GameCatalog cat;
    readonly Skeleton? skel;
    int sourceIndex;
    readonly ToggleButton[] sourceChips;
    readonly TextBox path = new() { Watermark = "Extracted source folder or animation file", Classes = { "bare" } };
    readonly TextBox findSource = new() { Watermark = "Find a source move" };
    readonly ListBox sourceList = new() { Classes = { "rows" } };
    readonly ComboBox sourceKind = new() { HorizontalAlignment = HorizontalAlignment.Stretch };
    static readonly string[] Kinds = { "Front grapples", "Rear grapples", "Strikes", "Running", "Ground", "Corner", "Ropes & apron", "Dives",
        "Seated", "Counters", "Lock-ups", "Weapons", "Pins", "Taunts", "Reactions", "Other" };
    List<string?> kindKeys = new();
    readonly TextBlock sourceCount = new() { Classes = { "faint" }, Text = "not read", VerticalAlignment = VerticalAlignment.Center };
    readonly TextBox move = new() { Watermark = "Move id or AEW montage" };
    readonly TextBox variant = new() { Text = "000", Watermark = "000" };
    readonly TextBox findDonor = new() { Watermark = "Find a similar SvR 2011 move" };
    readonly ListBox donorList = new() { Classes = { "rows" } };
    readonly TextBox moveName = new() { Watermark = "Name for the new move" };
    readonly CheckBox running = new() { Content = "Running attack (keep the receiver's start spot)", FontSize = 12 };
    readonly CheckBox customDamage = new() { Content = "Set damage per body part", FontSize = 12 };
    readonly NumericUpDown[] damage = Enumerable.Range(0, 4).Select(_ => new NumericUpDown { Minimum = 0, Maximum = 255, Value = 0, FormatString = "0", Increment = 1, ShowButtonSpinner = false, HorizontalContentAlignment = HorizontalAlignment.Center }).ToArray();
    readonly Grid damageGrid = new() { ColumnDefinitions = new("*,6,*,6,*,6,*") };
    readonly TextBox python = new() { Text = MainWindow.LoadSetting("porterPython") ?? AnimationPorter.DefaultPython };
    readonly TextBox report = new() { IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, FontFamily = new FontFamily("Consolas, Cascadia Mono, monospace"), FontSize = 11, BorderThickness = new Thickness(0) };
    readonly TextBlock help = new() { Classes = { "faint" }, TextWrapping = TextWrapping.Wrap };
    readonly TextBlock status = new() { Classes = { "sub" }, Text = "Choose a source game, read its moves, then pick a move and a donor." };
    readonly Button load = new() { Content = "Read source moves", HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Center };
    readonly Button build = new() { Content = "Build ported move", Classes = { "accent" }, HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Center };
    readonly Button preview = new() { Content = "Open in editor", IsEnabled = false, HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Center };
    readonly Button install = new() { Content = "Install pack to game", IsEnabled = false, HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Center };
    readonly Button cancel = new() { Content = "Close" };
    // the stage
    readonly Viewport3D view = new();
    readonly ToggleButton showSource = new() { Content = "Source", Classes = { "chip" }, IsEnabled = false };
    readonly ToggleButton showDonor = new() { Content = "Donor", Classes = { "chip" }, IsEnabled = false };
    readonly ToggleButton showPorted = new() { Content = "Ported move", Classes = { "chip" }, IsEnabled = false };
    readonly TextBlock stageTitle = new() { FontWeight = FontWeight.SemiBold, FontSize = 13, Text = "Nothing to show yet", TextTrimming = TextTrimming.CharacterEllipsis };
    readonly TextBlock stageSub = new() { Classes = { "faint" }, Text = "Pick a donor to see it; build to see the ported move." };
    readonly Button play = new() { Classes = { "round", "accent" }, MinWidth = 44 };
    readonly Button prev = new() { Classes = { "round" }, Content = "‹" };
    readonly Button next = new() { Classes = { "round" }, Content = "›" };
    readonly ToggleButton loop = new() { Content = "Loop", IsChecked = true, Classes = { "chip" }, VerticalAlignment = VerticalAlignment.Center };
    readonly Slider scrub = new() { Minimum = 0, Maximum = 1, Width = 220, VerticalAlignment = VerticalAlignment.Center };
    readonly TextBlock frameText = new() { Text = "0", FontFamily = new FontFamily("Consolas"), FontSize = 15, FontWeight = FontWeight.Bold, MinWidth = 70, VerticalAlignment = VerticalAlignment.Center };
    readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromSeconds(1 / 30.0) };
    int frames; double frame; bool playing, scrubbing;
    string? previewDir;                 // a built pack loaded into the catalog for the stage (taken out again)

    readonly List<Control> inputs = new();
    List<AnimationPorter.SourceMove> sourceMoves = new();
    readonly List<Donor> donors;
    AnimationPorter.BuiltPack? result;
    CancellationTokenSource? work;
    bool installing;
    public AnimationPorter.BuiltPack? Built => result;
    string Source => SourceKeys[sourceIndex];

    public PorterDialog(GameCatalog catalog, Skeleton? skeleton = null, AnimationPorter.BuiltPack? previous = null)
    {
        cat = catalog; skel = skeleton;
        Title = "AnimIT — Animation porter";
        try { Icon = new WindowIcon(Avalonia.Platform.AssetLoader.Open(new Uri("avares://AnimIT/Assets/animit.ico"))); } catch { }
        Width = 1500; Height = 920; MinWidth = 1200; MinHeight = 720;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        donors = cat.Moves.Values.Where(m => m.Pack == null && m.Usable && m.Id >= 2000 && m.Id < 16384 && cat.Best(m.Id, m.MainPhase, 0)?.Kind == "YMKs")
            .OrderBy(m => m.Name).Select(m => new Donor(m.Id, m.Name, m.Category)).Prepend(new Donor(0, "", "")).ToList();

        sourceChips = SourceNames.Select((n, i) => new ToggleButton { Content = n, Classes = { "chip" }, IsChecked = i == 0 }).ToArray();
        int saved = Array.IndexOf(SourceKeys, MainWindow.LoadSetting("porterLastSource") ?? "");
        sourceIndex = saved < 0 ? 0 : saved;
        for (int i = 0; i < sourceChips.Length; i++)
        {
            int k = i;
            sourceChips[i].IsChecked = i == sourceIndex;
            sourceChips[i].Click += (_, _) =>
            {
                foreach (var c in sourceChips) c.IsChecked = c == sourceChips[k];
                if (sourceIndex == k) return;
                sourceIndex = k; MainWindow.SaveSetting("porterLastSource", Source); SourceChanged();
            };
        }

        sourceList.ItemTemplate = new FuncDataTemplate<AnimationPorter.SourceMove>((m, _) =>
            m == null ? new Border() : Row(m.Name, (m.Name == m.Id ? $"variant {m.Variant}" : m.Variant == "000" ? m.Id : $"{m.Id}  ·  {m.Variant}")
                + (m.Kind.Length > 0 ? $"  ·  {m.Kind}" : "") + (m.Named ? "" : "  ·  name from file")));
        donorList.ItemTemplate = new FuncDataTemplate<Donor>((d, _) =>
            d == null ? new Border() : d.Id == 0 ? Row("Choose automatically", "HCTP picks a donor for you") : DonorRow(d));
        bestChip.Click += (_, _) => { bestChip.IsChecked = true; allChip.IsChecked = false; FilterDonors(); };
        allChip.Click += (_, _) => { allChip.IsChecked = true; bestChip.IsChecked = false; FilterDonors(); };

        var browse = new Button { Content = "Folder…", Classes = { "small" }, Margin = new Thickness(0, 0, 3, 0), VerticalAlignment = VerticalAlignment.Center };
        var file = new Button { Content = "File…", Classes = { "small" }, Margin = new Thickness(0, 0, 3, 0), VerticalAlignment = VerticalAlignment.Center };
        browse.Click += async (_, _) =>
        {
            var picked = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = "Extracted source game / animations" });
            if (picked.FirstOrDefault()?.TryGetLocalPath() is string p) { path.Text = p; await LoadMoves(); }
        };
        file.Click += async (_, _) =>
        {
            var picked = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions { Title = "Extracted source animation", FileTypeFilter = new[] { new FilePickerFileType("Animations") { Patterns = new[] { "*.clip", "*.clips", "*.uasset" } } } });
            if (picked.FirstOrDefault()?.TryGetLocalPath() is string p) { path.Text = p; await LoadMoves(); }
        };
        findSource.TextChanged += (_, _) => FilterSources();
        sourceKind.SelectionChanged += (_, _) => { if (!fillingKinds) FilterSources(); };
        findDonor.TextChanged += (_, _) => FilterDonors();
        sourceList.SelectionChanged += (_, _) =>
        {
            if (sourceList.SelectedItem is not AnimationPorter.SourceMove m) return;
            move.Text = m.Id; variant.Text = m.Variant; moveName.Text = m.Name;
            _ = ShowSource(m);
            _ = RankDonors(m);
        };
        donorList.SelectionChanged += (_, _) => { if (!filling && donorList.SelectedItem is Donor d && d.Id != 0) ShowDonor(d); };
        load.Click += async (_, _) => await LoadMoves();
        Ui.CopyMenu(sourceList, o => o is AnimationPorter.SourceMove m ? (m.Name, m.Id) : null, Status);
        Ui.CopyMenu(donorList, o => o is Donor d && d.Id != 0 ? (d.Name, d.Id.ToString()) : null, Status);
        build.Click += async (_, _) => await Build();
        preview.Click += (_, _) => { UnloadPreview(); Close(result); };
        install.Click += async (_, _) => await Install();
        cancel.Click += (_, _) => { if (work != null) work.Cancel(); else Close((AnimationPorter.BuiltPack?)null); };
        Closing += (_, e) => { if (installing) e.Cancel = true; else { work?.Cancel(); timer.Stop(); } };
        Closed += (_, _) => { if (evData?.Dirty == true) SaveEvents(); UnloadPreview(); };
        customDamage.IsCheckedChanged += (_, _) => { foreach (var n in damage) n.IsEnabled = customDamage.IsChecked == true; damageGrid.IsVisible = customDamage.IsChecked == true; };
        showDonor.Click += (_, _) => { if (donorList.SelectedItem is Donor d && d.Id != 0) ShowDonor(d); else showDonor.IsChecked = false; };
        showPorted.Click += (_, _) => ShowPorted();
        showSource.Click += (_, _) => { if (sourceList.SelectedItem is AnimationPorter.SourceMove m) _ = ShowSource(m); else showSource.IsChecked = false; };
        play.Click += (_, _) => SetPlaying(!playing);
        prev.Click += (_, _) => { SetPlaying(false); SetFrame(frame - 1); };
        next.Click += (_, _) => { SetPlaying(false); SetFrame(frame + 1); };
        scrub.PropertyChanged += (_, e) => { if (e.Property == RangeBase.ValueProperty && !scrubbing) { SetPlaying(false); SetFrame(scrub.Value); } };
        timer.Tick += (_, _) => Tick();
        evLine.MarkMoved += (tag, f) => MoveEvent((PackEvents.Ev)tag, f);
        evLine.MarkSelected += tag => { evSel = tag as PackEvents.Ev; EventInfo(); };
        evLine.FrameClicked += f =>
        {
            SetPlaying(false);
            if (showPorted.IsChecked != true && result != null) ShowPorted();
            SetPlaying(false);
            SetFrame(f);
        };
        evPrev.Click += (_, _) => { if (evSel != null) MoveEvent(evSel, evSel.Frame - 1); };
        evNext.Click += (_, _) => { if (evSel != null) MoveEvent(evSel, evSel.Frame + 1); };
        evAll.Click += (_, _) => RefreshEvents();
        evUndo.Click += (_, _) => { evData?.Reload(); evSel = null; RefreshEvents(); Status("Event changes undone."); };
        evSave.Click += (_, _) => SaveEvents();
        evMatch.Click += async (_, _) => await MatchAew();
        ToolTip.SetTip(evMatch, "Put the hit, ring shakes and rumbles on AEW's own markers again (the dashed lines)");
        if (skel != null) view.SetSkeleton(skel);
        SetPlaying(false);

        Content = Layout(browse, file);
        SourceChanged();
        if (previous != null && File.Exists(Path.Combine(previous.Directory, "pack.txt")))
        {
            result = previous;
            preview.IsEnabled = true;
            install.IsEnabled = !Installed(previous.Directory);
            report.Text = $"Last built move: {string.Join(", ", previous.Ids)}\nPack: {previous.Directory}\n\n{previous.Report}";
            showPorted.IsEnabled = true;
            ShowPorted();
            LoadEvents();
        }
    }

    // ---------------------------------------------------------------- layout
    static Border Row(string title, string sub) => new()
    {
        Classes = { "row" },
        Child = new StackPanel
        {
            Spacing = 3,
            Children =
            {
                new TextBlock { Text = title, FontWeight = FontWeight.SemiBold, FontSize = 13, TextTrimming = TextTrimming.CharacterEllipsis },
                new TextBlock { Text = sub, Classes = { "mono" }, TextTrimming = TextTrimming.CharacterEllipsis },
            },
        },
    };

    static Grid Heading(string text, Control? right = null)
    {
        var g = new Grid { ColumnDefinitions = new("Auto,*,Auto") };
        g.Children.Add(new Border { Classes = { "bar" } });
        var t = new TextBlock { Text = text, Classes = { "h" } }; Grid.SetColumn(t, 1); g.Children.Add(t);
        if (right != null) { Grid.SetColumn(right, 2); g.Children.Add(right); }
        return g;
    }

    static StackPanel Labeled(string label, Control c) => new() { Spacing = 3, Children = { new TextBlock { Text = label, Classes = { "label" } }, c } };

    Control Layout(Button browse, Button file)
    {
        // header: the mark, the title, the source game chips
        var mark = new Controls.LogoMark { Width = 40, Height = 40 };   // the AnimIT logo
        var titleRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children =
        {
            new TextBlock { Text = "Animation porter", FontSize = 20, FontWeight = FontWeight.Bold },
            new Border { Classes = { "tag" }, VerticalAlignment = VerticalAlignment.Center, Child = new TextBlock { Text = "to SvR 2011", FontSize = 10.5, FontWeight = FontWeight.SemiBold, Foreground = Ui.Res("AccentHi") } },
        } };
        var brand = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12, VerticalAlignment = VerticalAlignment.Center, Children =
        {
            mark, new StackPanel { Spacing = 1, VerticalAlignment = VerticalAlignment.Center, Children = { titleRow, new TextBlock { Classes = { "sub" }, Text = "Bring moves from other wrestling games into SvR 2011" } } },
        } };
        var chips = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, VerticalAlignment = VerticalAlignment.Center };
        chips.Children.Add(new TextBlock { Text = "Source game", Classes = { "faint" }, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 4, 0) });
        foreach (var c in sourceChips) chips.Children.Add(c);
        var headerGrid = new Grid { ColumnDefinitions = new("Auto,*,Auto") };
        headerGrid.Children.Add(brand); Grid.SetColumn(chips, 2); headerGrid.Children.Add(chips);
        var header = new Border { Background = Ui.Res("Surface"), Padding = new Thickness(18, 12), Child = headerGrid };

        // left: source moves
        var pathField = new Border { Classes = { "field" }, Child = new Grid { ColumnDefinitions = new("*,Auto,Auto") } };
        var pf = (Grid)pathField.Child;
        pf.Children.Add(path); Grid.SetColumn(browse, 1); pf.Children.Add(browse); Grid.SetColumn(file, 2); pf.Children.Add(file);
        var ids = new Grid { ColumnDefinitions = new("*,10,80") };
        var idCell = Labeled("Move id / montage", move); ids.Children.Add(idCell);
        var varCell = Labeled("Clip variant", variant); Grid.SetColumn(varCell, 2); ids.Children.Add(varCell);
        var leftGrid = new Grid { RowDefinitions = new("Auto,Auto,Auto,Auto,Auto,*,Auto"), RowSpacing = 0 };
        void L(Control c, int row, Thickness m) { c.Margin = m; Grid.SetRow(c, row); leftGrid.Children.Add(c); }
        L(Heading("1  ·  Source move", sourceCount), 0, new Thickness(0));
        L(pathField, 1, new Thickness(0, 12, 0, 6));
        L(help, 2, new Thickness(2, 0, 2, 8));
        L(load, 3, new Thickness(0, 0, 0, 10));
        var findRow = new Grid { ColumnDefinitions = new("*,8,170") };
        findRow.Children.Add(findSource); Grid.SetColumn(sourceKind, 2); findRow.Children.Add(sourceKind);
        ToolTip.SetTip(sourceKind, "Move type");
        L(findRow, 4, new Thickness(0, 0, 0, 8));
        L(sourceList, 5, new Thickness(0));
        L(new Border { Classes = { "well" }, Child = ids }, 6, new Thickness(0, 10, 0, 0));
        var left = new Border { Classes = { "panel" }, Padding = new Thickness(14), Child = leftGrid };
        inputs.Add(left);

        // centre: the stage with a floating transport, then the report
        play.Content = PlayIcon(false);
        var transport = new Border
        {
            Classes = { "transport" }, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(0, 0, 0, 14),
            Child = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Children =
            {
                prev, play, next, loop, new Border { Width = 1, Background = Ui.Res("Line"), Margin = new Thickness(8, 6) }, scrub,
                new TextBlock { Text = "frame", Classes = { "faint" }, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0, 0, 0) }, frameText,
            } },
        };
        frameText.Foreground = Ui.Res("AccentHi");
        var hint = new TextBlock { Classes = { "faint" }, Text = "drag: orbit  ·  right drag: pan  ·  wheel: zoom  ·  double click: re-centre", Margin = new Thickness(0, 6, 0, 0) };
        var stageInfo = new StackPanel { Margin = new Thickness(14, 10), Spacing = 2, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top, Children = { stageTitle, stageSub, hint } };
        var stageChips = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Children = { showSource, showDonor, showPorted } };
        var legend = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12, Margin = new Thickness(0, 6, 0, 0), HorizontalAlignment = HorizontalAlignment.Right, Children =
        {
            Dot("#E8C4AE", "attacker"), Dot("#82A8E4", "receiver"),
        } };
        Control stageBody = skel != null ? view : new TextBlock { Text = "3D preview needs the game's skeleton (load the game in AnimIT).", Classes = { "faint" }, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        var stage = new Border { Classes = { "panel" }, ClipToBounds = true, Child = new Grid { Children = { stageBody, stageInfo, new StackPanel { Margin = new Thickness(14, 10), HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top, Children = { stageChips, legend } }, transport } } };
        var tabs = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Children = { eventsTab, reportTab } };
        var evTools = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, VerticalAlignment = VerticalAlignment.Center, Children =
        {
            evMatch, evPrev, evNext, evAll, evUndo, evSave,
        } };
        var headRow = new Grid { ColumnDefinitions = new("Auto,Auto,*,Auto") };
        headRow.Children.Add(new Border { Classes = { "bar" } });
        headRow.Children.Add(Col(tabs, 1));
        headRow.Children.Add(Col(evTools, 3)); headRow.Children.Add(Col(evInfo, 2)); evTools.Margin = new Thickness(8, 0, 0, 0);
        var reportPanel = new Border { Classes = { "panel" }, Padding = new Thickness(14, 10, 14, 12), Child = new Grid { RowDefinitions = new("Auto,*"), RowSpacing = 8, Children = { headRow } } };
        var rg = (Grid)reportPanel.Child;
        var reportWell = new Border { Classes = { "well" }, Padding = new Thickness(4), Child = report, IsVisible = false };
        evWell.Child = new Grid { Children = { evLine, evEmpty } };
        Grid.SetRow(evWell, 1); rg.Children.Add(evWell);
        eventsTab.Click += (_, _) => { eventsTab.IsChecked = true; reportTab.IsChecked = false; evWell.IsVisible = true; reportWell.IsVisible = false; evTools.IsVisible = true; evInfo.IsVisible = true; };
        reportTab.Click += (_, _) => { reportTab.IsChecked = true; eventsTab.IsChecked = false; evWell.IsVisible = false; reportWell.IsVisible = true; evTools.IsVisible = false; evInfo.IsVisible = false; };
        ToolTip.SetTip(evPrev, "Move the chosen event 1 frame earlier"); ToolTip.SetTip(evNext, "Move the chosen event 1 frame later"); ToolTip.SetTip(evAll, "Also show the small events (hand grabs, positions, motion changes)");
        report.Background = Brushes.Transparent;
        Grid.SetRow(reportWell, 1); rg.Children.Add(reportWell);
        var centre = new Grid { RowDefinitions = new("*,10,190") };
        centre.Children.Add(stage); Grid.SetRow(reportPanel, 2); centre.Children.Add(reportPanel);

        // right: donor + the new move + actions
        damageGrid.IsVisible = false;
        for (int i = 0; i < 4; i++)
        {
            damage[i].IsEnabled = false;
            var cell = Labeled(new[] { "Head", "Body", "Arms", "Legs" }[i], damage[i]);
            Grid.SetColumn(cell, i * 2); damageGrid.Children.Add(cell);
        }
        var newMove = new Border { Classes = { "well" }, Child = new StackPanel { Spacing = 8, Children =
        {
            Heading("3  ·  New move"),
            Labeled("Name in Create-a-Moveset", moveName),
            running, customDamage, damageGrid,
        } } };
        var rightGrid = new Grid { RowDefinitions = new("Auto,Auto,Auto,*,Auto,Auto") };
        void R(Control c, int row, Thickness m) { c.Margin = m; Grid.SetRow(c, row); rightGrid.Children.Add(c); }
        R(Heading("2  ·  Copy game rules from"), 0, new Thickness(0));
        R(new TextBlock { Text = "A similar SvR 2011 move: same start (front / behind) and the receiver ends the same way (face up / down). Its damage, sounds, events and menu type are used.", Classes = { "faint" }, TextWrapping = TextWrapping.Wrap }, 1, new Thickness(2, 8, 2, 8));
        var donorTools = new StackPanel { Spacing = 6, Children =
        {
            new Grid { ColumnDefinitions = new("*,6,Auto,6,Auto"), Children = { findDonor, Col(bestChip, 2), Col(allChip, 4) } },
            rankInfo,
        } };
        R(donorTools, 2, new Thickness(0, 0, 0, 8));
        R(donorList, 3, new Thickness(0));
        R(newMove, 4, new Thickness(0, 10, 0, 0));
        var rightTop = new Border { Classes = { "panel" }, Padding = new Thickness(14), Child = rightGrid };
        inputs.Add(rightTop);
        var actions = new Border { Classes = { "panel" }, Padding = new Thickness(14, 12), Child = new StackPanel { Spacing = 8, Children =
        {
            build,
            new Grid { ColumnDefinitions = new("*,8,*"), Children = { preview, install } },
            new TextBlock { Text = "Install adds a new move pack. Restart the game, then pick the move in Create-a-Moveset.", Classes = { "faint" }, TextWrapping = TextWrapping.Wrap },
        } } };
        Grid.SetColumn(install, 2);
        var right = new Grid { RowDefinitions = new("*,10,Auto") };
        right.Children.Add(rightTop); Grid.SetRow(actions, 2); right.Children.Add(actions);

        var body = new Grid { ColumnDefinitions = new("340,10,*,10,380"), Margin = new Thickness(12, 10) };
        body.Children.Add(left);
        Grid.SetColumn(centre, 2); body.Children.Add(centre);
        Grid.SetColumn(right, 4); body.Children.Add(right);

        var statusGrid = new Grid { ColumnDefinitions = new("*,Auto,Auto"), ColumnSpacing = 10 };
        statusGrid.Children.Add(status);
        var ver = new TextBlock { Classes = { "faint" }, Text = "AnimIT porter  ·  offline  ·  the game is changed only by Install", VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(ver, 1); statusGrid.Children.Add(ver);
        cancel.Classes.Add("small"); Grid.SetColumn(cancel, 2); statusGrid.Children.Add(cancel);
        var statusBar = new Border { Background = Ui.Res("Surface"), Padding = new Thickness(16, 6), Child = statusGrid };

        var root = new Grid { RowDefinitions = new("Auto,*,Auto") };
        root.Children.Add(header);
        Grid.SetRow(body, 1); root.Children.Add(body);
        Grid.SetRow(statusBar, 2); root.Children.Add(statusBar);
        return root;
    }

    static StackPanel Dot(string color, string text) => new()
    {
        Orientation = Orientation.Horizontal, Spacing = 5,
        Children = { new Ellipse { Width = 8, Height = 8, Fill = Brush.Parse(color), VerticalAlignment = VerticalAlignment.Center }, new TextBlock { Text = text, Classes = { "faint" } } },
    };

    static Avalonia.Controls.Shapes.Path PlayIcon(bool playing) => new()
    {
        Fill = Brushes.White,
        Data = Geometry.Parse(playing ? "M 0,0 H 4 V 13 H 0 Z M 7,0 H 11 V 13 H 7 Z" : "M 0,0 L 11,6.5 L 0,13 Z"),
    };

    // ---------------------------------------------------------------- the stage
    void ShowDonor(Donor d)
    {
        showDonor.IsEnabled = true;
        showDonor.IsChecked = true; showPorted.IsChecked = false; showSource.IsChecked = false; sampling?.Cancel();
        if (Show(d.Id, $"Donor  ·  {d.Name}", $"SvR 2011 move {d.Id}  ·  {d.Category}  ·  the game rules come from here"))
            Status($"Showing the donor {d.Name}. Compare its start and end with the source move.");
    }

    void ShowPorted()
    {
        if (result == null) { showPorted.IsChecked = false; return; }
        showPorted.IsChecked = true; showDonor.IsChecked = false; showSource.IsChecked = false; sampling?.Cancel();
        try
        {
            if (!cat.Packs.Any(p => string.Equals(Path.GetFullPath(p.Dir), Path.GetFullPath(result.Directory), StringComparison.OrdinalIgnoreCase)))
            {
                UnloadPreview();
                if (cat.LoadTemporaryPack(result.Directory)) previewDir = result.Directory;
            }
            string name = cat.Names.GetValueOrDefault(result.Ids[0]) ?? $"move {result.Ids[0]}";
            if (Show(result.Ids[0], $"Ported  ·  {name}", $"move {result.Ids[0]}  ·  built, not installed yet" + (Installed(result.Directory) ? " (installed)" : ""), fresh: true))
                Status("Showing the ported move. Check the positioning, the hit and how the receiver ends.");
        }
        catch (Exception e) { Status("Could not show the ported move: " + e.Message); }
    }

    Composer? stageComp;
    bool Show(int id, string title, string sub, bool fresh = false)
    {
        if (skel == null) return false;
        try
        {
            if (fresh || stageComp == null) stageComp = new Composer(cat, skel);   // (a rebuilt move keeps its id: no stale cache)
            var m = cat.Moves[id];
            int x = m.MainPhase, n = m.AttackerAt(x) ?? 1;
            var b = new Block
            {
                Name = m.Name,
                Attacker = new LaneRef(id, x, 0, $"{id}_{x}_0"),
                Receiver = m.ReceiverAt(x) != null ? new LaneRef(id, x, 1, $"{id}_{x}_1") : null,
                In = 0, Out = n - 1, SourceLength = n,
            };
            var compo = stageComp.Compose(new[] { b });
            var (A, R) = stageComp.Joints(compo);
            view.SetStage(compo.Stage.Yaw, compo.Stage.Shift, compo.Stage.AttackerY, compo.Stage.ReceiverY);
            view.SetFrames(A, R, true);
            view.SetPartner(stageComp.PartnerJoints(compo));
            Stage(compo.Frames, title, sub);
            return true;
        }
        catch (Exception e) { Status("Could not show the move: " + e.Message); return false; }
    }

    void Stage(int n, string title, string sub)
    {
        frames = n;
        scrubbing = true; scrub.Maximum = Math.Max(1, frames - 1); scrubbing = false;
        stageTitle.Text = title;
        stageSub.Text = $"{sub}  ·  {frames} frames ({frames / 30.0:0.0} s)";
        SetFrame(0);
        SetPlaying(true);
    }

    // the source move as it is (before any build): the worker samples its joints on 2011's names
    readonly Dictionary<string, AnimationPorter.SourceSample> samples = new();
    CancellationTokenSource? sampling;
    async Task ShowSource(AnimationPorter.SourceMove m)
    {
        if (skel == null || work != null) return;
        if (Source == "hctp") { Status("HCTP moves are shown after Build (pick a donor to compare)."); showSource.IsEnabled = false; return; }
        sampling?.Cancel();
        var cts = sampling = new CancellationTokenSource();
        showSource.IsEnabled = true;
        showSource.IsChecked = true; showDonor.IsChecked = false; showPorted.IsChecked = false;
        string key = $"{Source}|{path.Text}|{m.Id}|{m.Variant}";
        try
        {
            if (!samples.TryGetValue(key, out var smp))
            {
                stageTitle.Text = $"Source  ·  {m.Name}"; stageSub.Text = "reading the animation…";
                Status($"Reading {m.Name} from the source game…");
                smp = await AnimationPorter.Sample(python.Text ?? "python", cat.Game, Source, path.Text?.Trim().Trim('"') ?? "", m.Id, m.Variant, cts.Token);
                samples[key] = smp;
            }
            if (cts.IsCancellationRequested || showSource.IsChecked != true) return;
            view.SetStage(0, new V3(0, 0, 0));
            view.SetFrames(Joints(smp, smp.Attacker), Joints(smp, smp.Receiver), true);
            view.SetPartner(new List<V3[]?>());
            Stage(smp.Attacker.Count, $"Source  ·  {m.Name}", $"{SourceNames[sourceIndex]}  ·  {m.Id}  ·  as it is in the source game (no donor yet)"
                + (smp.Receiver.Count == 0 ? "  ·  no receiver clip" : ""));
            Status($"Showing {m.Name} from {SourceNames[sourceIndex]}. Now pick a donor that starts and ends the same way.");
        }
        catch (OperationCanceledException) { }
        catch (Exception e)
        {
            if (cts.IsCancellationRequested) return;
            stageSub.Text = "could not read this animation";
            Status("Could not read the source animation: " + e.Message.Split('\n')[0]);
        }
    }

    /// <summary>(headless check) show one source move on the stage</summary>
    internal async void TestSource(string source, string id, string? kind = null)
    {
        int k = Array.IndexOf(SourceKeys, source);
        if (k >= 0 && k != sourceIndex) { sourceIndex = k; foreach (var c in sourceChips) c.IsChecked = c == sourceChips[k]; SourceChanged(); }
        await LoadMoves();
        if (kind != null) sourceKind.SelectedIndex = Math.Max(0, kindKeys.IndexOf(kind));
        // (selecting the row does what a click does: the stage shows it and the donors are ranked)
        sourceList.SelectedItem = (sourceList.ItemsSource as IEnumerable<AnimationPorter.SourceMove>)?.FirstOrDefault(m => m.Id == id)
                                  ?? sourceMoves.FirstOrDefault(m => m.Id == id);
    }

    /// <summary>sampled joints -> one position per 2011 skeleton node (a node with no sample sits on its nearest sampled parent)</summary>
    List<V3[]?> Joints(AnimationPorter.SourceSample smp, List<double[]> frames)
    {
        var s = skel!;
        int nb = s.Names.Length;
        var col = new int[nb];
        for (int i = 0; i < nb; i++) col[i] = Array.IndexOf(smp.Joints, s.Names[i]);
        int koshi = Array.IndexOf(smp.Joints, "koshi");
        var from = new int[nb];
        for (int i = 0; i < nb; i++)
        {
            int k = i;
            while (k >= 0 && col[k] < 0) k = s.Parent[k];
            from[i] = k >= 0 ? col[k] : koshi;
        }
        return frames.Select(f =>
        {
            var P = new V3[nb];
            for (int i = 0; i < nb; i++) { int c = Math.Max(0, from[i]) * 3; P[i] = new V3(f[c], f[c + 1], f[c + 2]); }
            return (V3[]?)P;
        }).ToList();
    }

    void SetFrame(double f)
    {
        frame = frames == 0 ? 0 : Math.Clamp(Math.Round(f), 0, frames - 1);
        view.CurrentFrame = frame;
        scrubbing = true; scrub.Value = frame; scrubbing = false;
        frameText.Text = $"{frame:0} / {Math.Max(0, frames - 1)}";
        evLine.CurrentFrame = showPorted.IsChecked == true ? frame : -1;
    }

    void SetPlaying(bool on)
    {
        playing = on && frames > 1;
        play.Content = PlayIcon(playing);
        if (playing) timer.Start(); else timer.Stop();
    }

    void Tick()
    {
        if (frames <= 1) { SetPlaying(false); return; }
        double f = frame + 1;
        if (f > frames - 1)
        {
            if (loop.IsChecked == true) f = 0;
            else { SetPlaying(false); f = frames - 1; }
        }
        SetFrame(f);
    }

    void UnloadPreview()
    {
        if (previewDir == null) return;
        cat.UnloadPreviewPack(previewDir);
        previewDir = null;
        stageComp = null;
    }

    bool Installed(string dir) => Path.GetFullPath(dir).StartsWith(Path.GetFullPath(cat.Game) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

    void Status(string s) => status.Text = s;

    // ---------------------------------------------------------------- source / donor lists
    void SourceChanged()
    {
        string fallback = Source switch
        {
            "hctp" => "",
            "k26" => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Steam", "steamapps", "common", "WWE 2K26"),
            _ => "",
        };
        path.Text = MainWindow.LoadSetting("porterSource_" + Source) ?? (Directory.Exists(fallback) ? fallback : "");
        help.Text = Source switch
        {
            "hctp" => "The extracted disc folder with PAC/M.PAC and PAC/MOVES.PAC. HCTP can choose a donor for you.",
            "k26" => "The game folder, a .cak archive, or extracted .clips / .clip files. A game folder is cached the first time.",
            _ => "The AEW game folder (.utoc / .ucas) or extracted Animations/Ingame. Only the chosen move is extracted.",
        };
        variant.IsEnabled = Source == "k26";
        running.IsEnabled = customDamage.IsEnabled = Source == "aew";
        if (Source != "aew") { running.IsChecked = false; customDamage.IsChecked = false; }
        sourceMoves.Clear(); sourceList.ItemsSource = null; move.Text = "";
        sourceCount.Text = "not read";
        FillKinds();
        FilterDonors();
    }

    bool fillingKinds;
    /// <summary>the type list with how many moves each has; keeps the chosen type when it is still there</summary>
    void FillKinds()
    {
        string? keep = sourceKind.SelectedIndex > 0 && sourceKind.SelectedIndex < kindKeys.Count ? kindKeys[sourceKind.SelectedIndex] : null;
        var counts = sourceMoves.GroupBy(m => m.Kind).ToDictionary(g => g.Key, g => g.Count());
        kindKeys = new List<string?> { null };
        var items = new List<string> { $"All types ({sourceMoves.Count})" };
        foreach (var k in Kinds.Concat(counts.Keys.Where(k => k.Length > 0 && !Kinds.Contains(k))))
            if (counts.TryGetValue(k, out int n)) { kindKeys.Add(k); items.Add($"{k} ({n})"); }
        fillingKinds = true;
        sourceKind.ItemsSource = items;
        sourceKind.SelectedIndex = Math.Max(0, kindKeys.IndexOf(keep));
        sourceKind.IsEnabled = items.Count > 1;
        fillingKinds = false;
    }

    void FilterSources()
    {
        string query = findSource.Text?.Trim() ?? "";
        string? kind = sourceKind.SelectedIndex > 0 && sourceKind.SelectedIndex < kindKeys.Count ? kindKeys[sourceKind.SelectedIndex] : null;
        var l = sourceMoves.Where(m => (kind == null || m.Kind == kind) && (m.Name.Contains(query, StringComparison.OrdinalIgnoreCase) || m.Id.Contains(query)))
            .OrderBy(m => m.Named ? 0 : m.Name.EndsWith(m.Id) ? 2 : 1).ThenBy(m => m.Name, StringComparer.OrdinalIgnoreCase).Take(1500).ToList();
        sourceList.ItemsSource = l;
        sourceCount.Text = $"{l.Count} shown · {sourceMoves.Count}";
    }

    void FilterDonors()
    {
        string query = findDonor.Text?.Trim() ?? "";
        int keep = donorList.SelectedItem is Donor k ? k.Id : -1;
        bool best = ranked != null && bestChip.IsChecked == true;
        var from = best ? ranked! : donors;
        var choices = from.Where(d => (d.Id != 0 || Source == "hctp") && (d.Id == 0 || d.Name.Contains(query, StringComparison.OrdinalIgnoreCase) || d.Id.ToString() == query)).ToList();
        filling = true;
        donorList.ItemsSource = choices;
        donorList.SelectedItem = choices.FirstOrDefault(d => d.Id == keep) ?? (Source == "hctp" ? choices.FirstOrDefault() : null);
        filling = false;
    }
    bool filling;

    static Control Col(Control c, int col) { Grid.SetColumn(c, col); return c; }

    static readonly IBrush OkBrush = Brush.Parse("#5BD08A"), WarnBrush = Brush.Parse("#F2B14C"), InfoBrush = Brush.Parse("#82A8E4");

    /// <summary>a donor row: name, id and type; when ranked, its place and the checks (ok, problem, note)</summary>
    static Border DonorRow(Donor d)
    {
        if (d.Checks == null) return Row(d.Name, $"{d.Id}  ·  {d.Category}");
        int warns = d.Checks.Count(c => c.State == "warn");
        var head = new Grid { ColumnDefinitions = new("*,Auto") };
        head.Children.Add(new TextBlock { Text = d.Name, FontWeight = FontWeight.SemiBold, FontSize = 13, TextTrimming = TextTrimming.CharacterEllipsis });
        var badge = new Border
        {
            CornerRadius = new CornerRadius(8), Padding = new Thickness(7, 1), Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center,
            Background = Brush.Parse(warns == 0 ? "#173A28" : warns == 1 ? "#3A3017" : "#3A1E17"),
            Child = new TextBlock { Text = $"#{d.Rank}" + (warns == 0 ? "  all good" : $"  {warns} problem{(warns == 1 ? "" : "s")}"), FontSize = 10.5, FontWeight = FontWeight.SemiBold,
                                    Foreground = warns == 0 ? OkBrush : warns == 1 ? WarnBrush : Brush.Parse("#F07C5C") },
        };
        Grid.SetColumn(badge, 1); head.Children.Add(badge);
        var stack = new StackPanel { Spacing = 2, Children = { head, new TextBlock { Text = $"{d.Id}  ·  {d.Category}", Classes = { "mono" } } } };
        foreach (var (state, text) in d.Checks.OrderBy(c => c.State == "warn" ? 0 : c.State == "info" ? 2 : 1))
            stack.Children.Add(new TextBlock
            {
                Text = (state == "ok" ? "\u2713  " : state == "warn" ? "\u26A0\uFE0E  " : "\u2022  ") + text, FontSize = 11, TextWrapping = TextWrapping.Wrap,
                Foreground = state == "ok" ? OkBrush : state == "warn" ? WarnBrush : InfoBrush,
            });
        return new Border { Classes = { "row" }, Child = stack };
    }

    // ---------------------------------------------------------------- event editor
    readonly EventTimeline evLine = new();
    readonly Border evWell = new() { Classes = { "well" }, Padding = new Thickness(6, 4) };
    readonly TextBlock evEmpty = new() { Classes = { "faint" }, Text = "Build a move to see and move its events (hit, ring shake, sounds, rumble).", HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
    readonly ToggleButton eventsTab = new() { Content = "Events", Classes = { "chip" }, IsChecked = true };
    readonly ToggleButton reportTab = new() { Content = "Report", Classes = { "chip" } };
    readonly TextBlock evInfo = new() { Classes = { "faint" }, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 0, 0), TextTrimming = TextTrimming.CharacterEllipsis, Text = "drag a mark to move it" };
    readonly Button evPrev = new() { Content = "‹", Classes = { "small" }, IsEnabled = false };
    readonly Button evNext = new() { Content = "›", Classes = { "small" }, IsEnabled = false };
    readonly ToggleButton evAll = new() { Content = "All", Classes = { "chip" }, VerticalAlignment = VerticalAlignment.Center };
    readonly Button evUndo = new() { Content = "Undo", Classes = { "small" }, IsEnabled = false };
    readonly Button evMatch = new() { Content = "Match AEW", Classes = { "small" }, IsVisible = false };
    string? aewMontage;
    readonly Button evSave = new() { Content = "Save timing", Classes = { "small", "accent" }, IsEnabled = false };
    PackEvents? evData;
    PackEvents.Ev? evSel;

    void LoadEvents()
    {
        evData = null; evSel = null;
        if (result != null && !Installed(result.Directory))
        {
            try { evData = PackEvents.Load(cat, result.Directory, result.Ids[0]); }
            catch (Exception e) { Status("Could not read the move's events: " + e.Message); }
        }
        RefreshEvents();
    }

    void RefreshEvents()
    {
        evEmpty.IsVisible = evData == null;
        evLine.IsVisible = evData != null;
        evUndo.IsEnabled = evSave.IsEnabled = evData?.Dirty == true;
        if (evData == null) { evLine.Set(new(), 1); EventInfo(); return; }
        bool all = evAll.IsChecked == true;
        var marks = new List<EventTimeline.Mark>();
        foreach (var e in evData.Events)
        {
            var (name, color, major) = PackEvents.Describe(e);
            if (!major && !all) continue;
            marks.Add(new EventTimeline.Mark(e, e.IsSound ? 2 + e.Track : e.Track, e.Frame, name, Color.Parse(color), major));
        }
        if (evSel != null && !marks.Any(m => ReferenceEquals(m.Tag, evSel))) evSel = null;
        evLine.SetGuides(Guides());
        evLine.Set(marks, Math.Max(evData.Frames[0], evData.Frames[1]));
        evLine.Select(evSel);
        EventInfo();
    }

    /// <summary>AEW's own markers saved with the build (aew_marks.json): dashed guides on the event lanes</summary>
    List<EventTimeline.Guide> Guides()
    {
        var g = new List<EventTimeline.Guide>();
        aewMontage = null;
        evMatch.IsVisible = false;
        string file = evData == null ? "" : Path.Combine(evData.Dir, "aew_marks.json");
        if (!File.Exists(file)) return g;
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(file));
            var root = doc.RootElement;
            aewMontage = root.GetProperty("montage").GetString();
            evMatch.IsVisible = aewMontage != null;
            foreach (var (key, label, color) in new[] { ("hit", "AEW hit", "#FF5C5C"), ("shake", "AEW ring shake", "#F2B14C"), ("rumble", "AEW rumble", "#B58CFF"), ("end", "AEW move end", "#98A2B5") })
                if (root.TryGetProperty(key, out var arr))
                    foreach (var f in arr.EnumerateArray()) g.Add(new EventTimeline.Guide(f.GetInt32(), label, Color.Parse(color)));
        }
        catch (Exception) { }
        return g;
    }

    async Task MatchAew()
    {
        if (evData == null || aewMontage == null || work != null) return;
        if (evData.Dirty) SaveEvents();
        string dir = evData.Dir; int id = evData.Id;
        await WithWork(async ct =>
        {
            Status("Matching the events to AEW's markers…");
            string notes = await AnimationPorter.MapEvents(python.Text ?? "python", cat.Game, path.Text?.Trim().Trim('"') ?? "", dir, id, aewMontage, ct);
            report.Text = notes + "\n\n" + report.Text;
            Status("Events matched to AEW's markers (hit, ring shakes, rumbles). Check them, then Install.");
        });
        LoadEvents();
    }

    void EventInfo()
    {
        evPrev.IsEnabled = evNext.IsEnabled = evSel != null;
        if (evSel == null)
        {
            int hits = evData?.Events.Count(e => !e.IsSound && PackEvents.Describe(e).Name.StartsWith("Hit")) ?? 0;
            int shakes = evData?.Events.Count(e => e.Op is >= 96 and <= 99) ?? 0;
            int sounds = evData?.Events.Count(e => e.IsSound) ?? 0;
            evInfo.Text = evData == null ? "" : $"{hits} hit · {shakes} ring shake · {sounds} sounds  —  drag a mark to move it";
            return;
        }
        var (name, _, _) = PackEvents.Describe(evSel);
        evInfo.Text = $"{name} · {(evSel.Track == 0 ? "attacker" : "receiver")} · frame {evSel.Frame}" + (evSel.Frame != evSel.Orig ? $" (was {evSel.Orig})" : "");
        SetPlaying(false);
        if (showPorted.IsChecked == true) SetFrame(evSel.Frame);
    }

    void MoveEvent(PackEvents.Ev e, int f)
    {
        if (evData == null) return;
        int n = Math.Max(1, evData.Frames[e.Track]);
        e.Frame = Math.Clamp(f, 0, n - 1);
        evSel = e;
        RefreshEvents();
        Status("Event moved. Play the move to check it, then Save timing (Install also saves it).");
    }

    void SaveEvents()
    {
        if (evData == null || !evData.Dirty) return;
        try
        {
            int moved = evData.Events.Count(e => e.Frame != e.Orig);
            var notes = evData.Save();
            report.Text = $"Saved {moved} event change(s) into {evData.Dir}." + (notes.Count > 0 ? "\n" + string.Join("\n", notes) : "") + "\n\n" + report.Text;
            Status($"Saved {moved} event change(s) into the built pack. Install it to use the new timing in the game.");
        }
        catch (Exception e) { Status("Could not save the events: " + e.Message); evData.Reload(); }
        RefreshEvents();
    }

    // ---------------------------------------------------------------- donor finder
    readonly ToggleButton bestChip = new() { Content = "Best matches", Classes = { "chip" }, IsEnabled = false, VerticalAlignment = VerticalAlignment.Center };
    readonly ToggleButton allChip = new() { Content = "All moves", Classes = { "chip" }, IsChecked = true, VerticalAlignment = VerticalAlignment.Center };
    readonly TextBlock rankInfo = new() { Classes = { "faint" }, TextWrapping = TextWrapping.Wrap, Text = "Pick a source move: AnimIT ranks the best donors for it." };
    List<Donor>? ranked;
    CancellationTokenSource? ranking;

    async Task RankDonors(AnimationPorter.SourceMove m)
    {
        ranking?.Cancel();
        ranked = null; bestChip.IsEnabled = false;
        if (Source == "hctp") { rankInfo.Text = "HCTP moves: AnimIT can choose the donor itself (Choose automatically)."; FilterDonors(); return; }
        var cts = ranking = new CancellationTokenSource();
        rankInfo.Text = $"Finding the best donors for {m.Name}...  (the first time, AnimIT measures every SvR move: about 10 seconds)";
        try
        {
            var r = await AnimationPorter.Donors(python.Text ?? "python", cat.Game, Source, path.Text?.Trim().Trim('"') ?? "", m.Id, m.Variant, cts.Token);
            if (cts.IsCancellationRequested) return;
            var byId = donors.Where(d => d.Id != 0).ToDictionary(d => d.Id);
            int rank = 0;
            ranked = r.Donors.Select(x =>
            {
                var b = byId.GetValueOrDefault(x.Id) ?? new Donor(x.Id, cat.Names.GetValueOrDefault(x.Id) ?? $"move {x.Id}", cat.Moves.GetValueOrDefault(x.Id)?.Category ?? "");
                return b with { Rank = ++rank, Checks = x.Checks };
            }).ToList();
            rankInfo.Text = $"Source: {r.Source}";
            bestChip.IsEnabled = true; bestChip.IsChecked = true; allChip.IsChecked = false;
            FilterDonors();
            Status($"Donors ranked for {m.Name}. #1 is the closest match; the orange lines show what does not match.");
        }
        catch (OperationCanceledException) { }
        catch (Exception e)
        {
            if (cts.IsCancellationRequested) return;
            rankInfo.Text = "Could not rank donors: " + e.Message.Split('\n')[0];
            FilterDonors();
        }
    }

    // ---------------------------------------------------------------- work
    void SetBusy(bool busy)
    {
        foreach (var c in inputs) c.IsEnabled = !busy;
        foreach (var c in sourceChips) c.IsEnabled = !busy;
        build.IsEnabled = !busy;
        preview.IsEnabled = install.IsEnabled = !busy && result != null;
        if (!busy && result != null) install.IsEnabled = !Installed(result.Directory);
        cancel.Content = busy ? "Cancel" : "Close";
    }

    async Task WithWork(Func<CancellationToken, Task> action)
    {
        if (work != null) return;
        using var cancellation = new CancellationTokenSource();
        work = cancellation; SetBusy(true);
        try { await action(cancellation.Token); }
        catch (OperationCanceledException) { report.Text = "Conversion cancelled. The game was not changed."; Status("Cancelled."); }
        catch (Exception e) { report.Text = e.Message; Status("Something went wrong - see the report."); }
        finally { work = null; SetBusy(false); }
    }

    async Task LoadMoves() => await WithWork(async ct =>
    {
        report.Text = "Reading source animations…"; Status("Reading source animations…");
        var catalog = await AnimationPorter.List(python.Text ?? "python", Source, path.Text?.Trim().Trim('"') ?? "", ct);
        sourceMoves = catalog.Moves;
        MainWindow.SaveSetting("porterSource_" + Source, path.Text ?? "");
        MainWindow.SaveSetting("porterPython", python.Text ?? "");
        FillKinds(); FilterSources(); report.Text = $"{sourceMoves.Count} source moves found. Choose one and a similar donor.\n{catalog.Notes}";
        Status($"{sourceMoves.Count} source moves found. Choose one, then a donor.");
    });

    async Task Build() => await WithWork(async ct =>
    {
        if (string.IsNullOrWhiteSpace(move.Text) || string.IsNullOrWhiteSpace(moveName.Text)) throw new InvalidOperationException("Choose a source move and enter its new name.");
        if (donorList.SelectedItem is not Donor donor) throw new InvalidOperationException("Choose a similar SvR 2011 donor.");
        SetPlaying(false);
        UnloadPreview();                 // (its move id must be free again for the new build)
        result = null; showPorted.IsEnabled = false; showPorted.IsChecked = false;
        evData = null; evSel = null; RefreshEvents();
        report.Text = "Converting the animation and checking both motion formats…"; Status("Building… this can take a few minutes.");
        result = await AnimationPorter.Build(python.Text ?? "python", cat, new()
        {
            Source = Source, Path = path.Text?.Trim().Trim('"') ?? "", Move = move.Text.Trim(), Variant = variant.Text?.Trim() ?? "000",
            Name = moveName.Text.Trim(), Donor = donor.Id, Running = running.IsChecked == true,
            Damage = customDamage.IsChecked == true ? damage.Select(n => (int)(n.Value ?? 0)).ToArray() : null,
        }, ct);
        MainWindow.SaveSetting("porterPython", python.Text ?? "");
        MainWindow.SaveSetting("porterSource_" + Source, path.Text ?? "");
        report.Text = $"Built and checked move {string.Join(", ", result.Ids)}.\nPack: {result.Directory}\n\n{result.Report}";
        showPorted.IsEnabled = true;
        ShowPorted();
        LoadEvents();
    });

    async Task Install()
    {
        if (result == null || work != null || installing) return;
        installing = true; SetBusy(true); cancel.IsEnabled = false;
        SetPlaying(false);
        if (evData?.Dirty == true) SaveEvents();
        UnloadPreview();
        report.Text = "Checking the current game and installing the new pack…"; Status("Installing…");
        try
        {
            string target = await Task.Run(() => AnimationPorter.Install(cat.Game, result));
            result = result with { Directory = target };
            report.Text = $"Installed to {target}\nMove id: {string.Join(", ", result.Ids)}. Restart SvR 2011, then select the move in Create-a-Moveset.";
            evData = null; evSel = null; RefreshEvents();
            evEmpty.Text = "Installed. Event timing is edited before installing: build again to change it.";
            Status("Installed. Restart the game and pick the move in Create-a-Moveset.");
        }
        catch (Exception e) { report.Text = e.Message; Status("Install failed - see the report."); }
        finally { installing = false; SetBusy(false); cancel.IsEnabled = true; install.IsEnabled = false; }
    }
}
