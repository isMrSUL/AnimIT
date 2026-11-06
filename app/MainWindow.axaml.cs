using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using AnimIT.Controls;
using AnimIT.Core;

namespace AnimIT;

/// <summary>a source sequence card</summary>
public sealed class MoveItem
{
    public MoveInfo Info { get; }
    public MoveItem(MoveInfo m) => Info = m;
    public int Id => Info.Id;
    public string Name => Info.Name;
    public string Badge => Info.Pack != null ? $"{Info.Category} · {Info.Pack.Replace('_', ' ')}" : Info.Category;
    public string Summary => (Info.Receiver != null ? "2 wrestlers" : "attacker only") + $" · id {Info.Id}";
    public string P1 => $"{Info.Attacker} f";
    public string P2 => (Info.Receiver is int r ? $"{r} f" : "none") + $"     id {Info.Id}" + (Info.MainPhase != 0 ? $" · phase {Info.MainPhase}" : "");
}

/// <summary>a timeline block card</summary>
public sealed class BlockItem
{
    public BlockItem(Block b, int index, int start)
    {
        Title = b.Name;
        Range = $"Frames {b.In} – {b.Out} · {b.Length} out · {b.Speed * 100:0}%";
        Kind = b.Receiver != null ? "2 wrestlers" : "attacker only";
        Join = index == 0 ? "first" : b.Join == JoinMode.Crossfade ? $"blend {b.Blend} f" : b.Join == JoinMode.Smooth ? $"smooth {b.Blend} f" : "cut";
        if (b.Align == RootAlign.Continue && index > 0) Join = "continues";
        else if (b.Align != RootAlign.None && index > 0) Join += " · lined up";
        _ = start;
    }
    public string Title { get; }
    public string Range { get; }
    public string Kind { get; }
    public string Join { get; }
}

public partial class MainWindow : Window
{
    GameCatalog? cat;
    Skeleton? skel;
    /// <summary>the game's match records (EXH, sounds...), for the event lanes and the save</summary>
    MovePack.Records? recs;
    Composer? comp;
    readonly List<Block> blocks = new();
    Composition? compo;
    AnimationPorter.BuiltPack? lastPort;
    List<MoveItem> allMoves = new();
    MoveItem? picked;
    double frame;
    bool playing;
    readonly DispatcherTimer timer;
    bool updating;
    static readonly string SettingsPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "AnimIT", "animit2.json");

    public MainWindow()
    {
        InitializeComponent();
        timer = new DispatcherTimer(TimeSpan.FromMilliseconds(1000.0 / 30), DispatcherPriority.Render, (_, _) => Tick());
        sideTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(1000.0 / 30), DispatcherPriority.Render, (_, _) => SideTick());
        sideTimer.Stop();
        BrowseButton.Click += async (_, _) => await Browse();
        ScanButton.Click += async (_, _) => await Scan();
        SaveButton.Click += async (_, _) => await SaveToGame();
        PortButton.Click += async (_, _) => await PortMoves();
        SearchBox.TextChanged += (_, _) => Filter();
        CategoryBox.ItemsSource = new[] { "All types" }.Concat(MoveCategory.Names).ToList();
        CategoryBox.SelectedIndex = 0;
        CategoryBox.SelectionChanged += (_, _) => Filter();
        foreach (var t in new[] { FilterAll, FilterStock, FilterPacks })
            t.Click += (s, _) => { foreach (var o in new[] { FilterAll, FilterStock, FilterPacks }) o.IsChecked = o == s; Filter(); };
        MoveList.SelectionChanged += (_, _) => Pick(MoveList.SelectedItem as MoveItem);
        Ui.CopyMenu(MoveList, o => o is MoveItem m ? (m.Name, m.Id.ToString()) : null, Status);
        PickPhase.SelectionChanged += (_, _) => { if (!pickingPhase && picked != null && PickPhase.SelectedItem is PhaseItem p) SetPhase(picked, p.X); };
        AddButton.Click += (_, _) => AddBlock();
        WholeButton.Click += (_, _) => AddWholeMove();
        VersionText.Text = $"AnimIT {typeof(MainWindow).Assembly.GetName().Version?.ToString(3)}  ·  30 FPS";
        SideClose.Click += (_, _) => SetSplit(false);
        SplitToggle.IsCheckedChanged += (_, _) => { if (SplitToggle.IsChecked == true && picked != null && blocks.Count > 0) ShowSide(picked); else SetSplit(false); };
        PlayButton.Click += (_, _) => SetPlaying(!playing);
        PrevButton.Click += (_, _) => { SetPlaying(false); SetFrame(Math.Round(frame) - 1); };
        NextButton.Click += (_, _) => { SetPlaying(false); SetFrame(Math.Round(frame) + 1); };
        RebuildButton.Click += (_, _) => Rebuild(true);
        CutButton.Click += (_, _) => CutAt(frame);
        // keys: C cut at the playhead, Space play / pause, Left / Right one frame (not while typing in a box)
        AddHandler(KeyDownEvent, (object? _, Avalonia.Input.KeyEventArgs e) =>
        {
            if (FocusManager?.GetFocusedElement() is TextBox) return;
            if (e.Key == Avalonia.Input.Key.C && e.KeyModifiers == Avalonia.Input.KeyModifiers.None) { CutAt(frame); e.Handled = true; }
            else if (e.Key == Avalonia.Input.Key.Space) { SetPlaying(!playing); e.Handled = true; }
            else if (e.Key == Avalonia.Input.Key.Left) { SetPlaying(false); SetFrame(Math.Round(frame) - 1); e.Handled = true; }
            else if (e.Key == Avalonia.Input.Key.Right) { SetPlaying(false); SetFrame(Math.Round(frame) + 1); e.Handled = true; }
        }, Avalonia.Interactivity.RoutingStrategies.Tunnel);
        ZoomSlider.PropertyChanged += (_, e) =>
        {
            if (e.Property != Avalonia.Controls.Primitives.RangeBase.ValueProperty) return;
            Timeline.Zoom = ZoomSlider.Value;
            ZoomText.Text = $"{Math.Pow(4, (ZoomSlider.Value - 0.5) * 2) * 100:0}%";
        };
        Timeline.Scrubbed += f => { SetPlaying(false); SetFrame(f); };
        EvLine.FrameClicked += f => { SetPlaying(false); SetFrame(f); };
        EvLine.MarkMoved += (tag, f) => MoveEvent((MovePack.SpliceEvent)tag, f);
        EvLine.MarkSelected += tag => { evSel = tag is MovePack.SpliceEvent e ? (e.Block, e.Key) : null; if (tag is MovePack.SpliceEvent se) { SetPlaying(false); SetFrame(se.Frame); } EventsText(); };
        EventsToggle.IsCheckedChanged += (_, _) => { EvLine.IsVisible = EventsToggle.IsChecked == true; RefreshEvents(); };
        EventsAll.IsCheckedChanged += (_, _) => RefreshEvents();
        EventsReset.Click += (_, _) => { foreach (var b in blocks) b.EventShift.Clear(); RefreshEvents(); Status("Every event is back where its clip has it."); };
        EventsNudgeL.Click += (_, _) => { if (SelEvent() is MovePack.SpliceEvent e) MoveEvent(e, e.Frame - 1); };
        Timeline.BlockSelected += i => { SelectBlock(i); SideTabs.SelectedIndex = 0; };
        Timeline.BlockTrimmed += Trim;
        Timeline.BlockMoved += (a, b) => MoveBlock(a, b);
        Timeline.LaneMenu += ShowLaneMenu;
        Timeline.LaneSelected += lane => { ImportLane.SelectedIndex = lane; handleLane = lane; UpdateGizmo(); };
        BlockList.SelectionChanged += (_, _) => { if (!updating && BlockList.SelectedIndex >= 0) SelectBlock(BlockList.SelectedIndex); };
        UpButton.Click += (_, _) => { int i = BlockList.SelectedIndex; if (i > 0) MoveBlock(i, i - 1); };
        DownButton.Click += (_, _) => { int i = BlockList.SelectedIndex; if (i >= 0 && i < blocks.Count - 1) MoveBlock(i, i + 1); };
        RemoveButton.Click += (_, _) => RemoveBlock(BlockList.SelectedIndex);
        ApplyButton.Click += (_, _) => ApplyInspector();
        // turn / move of each wrestler for the whole block: live
        // turn / move numbers: X / Z set the spot directly; a Turn turns the wrestler where he stands (about his spot at the
        // clip start), which also moves X / Z (they are the placement on the mat about the attacker's start spot)
        foreach (var n in new[] { AdjAX, AdjAZ, AdjRX, AdjRZ })
            n.ValueChanged += (_, _) =>
            {
                if (updating || Current is not Block b) return;
                int i = BlockList.SelectedIndex;
                b.AttackerX = (double)(AdjAX.Value ?? 0); b.AttackerZ = (double)(AdjAZ.Value ?? 0);
                b.ReceiverX = (double)(AdjRX.Value ?? 0); b.ReceiverZ = (double)(AdjRZ.Value ?? 0);
                Rebuild(false);
                SelectBlock(i);
            };
        AdjATurn.ValueChanged += (_, _) => { if (!updating && Current is Block b) TurnLane(0, (double)(AdjATurn.Value ?? 0) - b.AttackerTurn); };
        AdjRTurn.ValueChanged += (_, _) => { if (!updating && Current is Block b) TurnLane(1, (double)(AdjRTurn.Value ?? 0) - b.ReceiverTurn); };
        // the handle in the 3D view
        View.GizmoTurned += (lane, d) => TurnLane(lane, d);
        View.GizmoMoved += (lane, dx, dz) =>
        {
            if (Current is not Block b) return;
            int i = BlockList.SelectedIndex;
            if (lane == 0) { b.AttackerX += dx; b.AttackerZ += dz; } else { b.ReceiverX += dx; b.ReceiverZ += dz; }
            Rebuild(false);
            SelectBlock(i);
        };
        View.WrestlerPicked += lane =>
        {
            handleLane = lane;
            ImportLane.SelectedIndex = lane;
            UpdateGizmo();
            if (Current != null) Status($"The handle now moves the {(lane == 0 ? "attacker" : "receiver")} of {Current.Name}.");
        };
        // copy where both wrestlers are at the playhead (their bodies); paste fits the selected clip's first frame onto them,
        // each wrestler on his own (turn + shift on the mat, the best match of all joints seen from above)
        AdjCopyButton.Click += (_, _) =>
        {
            int f = (int)Math.Round(frame);
            if (f < 0 || f >= jA.Count || jA[f] == null) return;
            copiedBodies = (jA[f]!, f < jR.Count ? jR[f] : null);
            AdjPasteButton.IsEnabled = true;
            Status($"Copied where both wrestlers are at frame {f}. Select a clip and press Paste: it will start there.");
        };
        AdjPasteButton.Click += (_, _) => PasteBodies();
        ReactionBox.SelectionChanged += (_, _) =>
        {
            if (updating || Current is not Block b || comp?.ReactionChoices(b) is not { Count: > 0 } ch || ReactionBox.SelectedIndex < 0) return;
            int i = BlockList.SelectedIndex;
            b.Reaction = ReactionBox.SelectedIndex >= ch.Count ? -1 : ReactionBox.SelectedIndex;
            Rebuild(false);
            SelectBlock(i);
        };
        AdjResetButton.Click += (_, _) =>
        {
            if (Current is not Block b) return;
            int i = BlockList.SelectedIndex;
            b.AttackerTurn = b.AttackerX = b.AttackerZ = b.ReceiverTurn = b.ReceiverX = b.ReceiverZ = 0;
            Rebuild(false);
            SelectBlock(i);
        };
        SelFrames.ValueChanged += (_, _) => { if (updating || Current is not Block b) return; updating = true; SelSpeed.Value = (decimal)Math.Round((b.Out - b.In + 1) / (double)Math.Max(1, SelFrames.Value ?? 1) * 100, 1); updating = false; };
        SelSpeed.ValueChanged += (_, _) => { if (updating || Current is not Block b) return; updating = true; SelFrames.Value = Math.Max(1, (int)Math.Round((b.Out - b.In + 1) / ((double)(SelSpeed.Value ?? 100) / 100))); updating = false; };
        ImportButton.Click += async (_, _) => { if (Current != null) await ImportInto(BlockList.SelectedIndex, ImportLane.SelectedIndex); };
        Opened += async (_, _) =>
        {
            var game = Environment.GetEnvironmentVariable("ANIMIT_GAME") ?? LoadSetting("game") ?? "";
            GameBox.Text = game;
            if (Directory.Exists(game)) await Scan();
            // ANIMIT_DEMO=id,id,... adds those moves as blocks (for screenshots and quick checks)
            foreach (var s in (Environment.GetEnvironmentVariable("ANIMIT_DEMO") ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries))
                if (int.TryParse(s, out int id) && allMoves.FirstOrDefault(m => m.Id == id) is MoveItem m)
                {
                    Pick(m);
                    AddButton.IsEnabled = true;
                    AddBlock();
                }
            if (blocks.Count > 0) { SetFrame(compo!.Frames * double.Parse(Environment.GetEnvironmentVariable("ANIMIT_DEMO_AT") ?? "0.3", System.Globalization.CultureInfo.InvariantCulture)); SetPlaying(false); }
            if (Environment.GetEnvironmentVariable("ANIMIT_DEMO_CUT") is string dc && blocks.Count > 0) CutAt(double.Parse(dc));
            try {
            switch (Environment.GetEnvironmentVariable("ANIMIT_DEMO_DIALOG"))
            {
                case "speed": await SpeedDialogFor(blocks.Count - 1); break;
                case "import": await ImportInto(blocks.Count - 1, 1); break;
                case "save": await SaveToGame(); break;
            }
            } catch (Exception e) { File.WriteAllText(Path.Combine(Path.GetTempPath(), "animit_demo_error.txt"), e.ToString()); }
        };
        Closing += (_, _) => timer.Stop();
    }

    Block? Current => BlockList.SelectedIndex >= 0 && BlockList.SelectedIndex < blocks.Count ? blocks[BlockList.SelectedIndex] : null;

    // ---------------------------------------------------------------- settings
    internal static string? LoadSetting(string key)
    {
        try { return JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(SettingsPath))?.GetValueOrDefault(key); }
        catch { return null; }
    }

    internal static void SaveSetting(string key, string value)
    {
        try
        {
            Dictionary<string, string> d;
            try { d = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(SettingsPath)) ?? new(); } catch { d = new(); }
            d[key] = value;
            Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
            File.WriteAllText(SettingsPath, JsonSerializer.Serialize(d));
        }
        catch { /* (settings are a convenience) */ }
    }

    // ---------------------------------------------------------------- game
    async Task Browse()
    {
        var r = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = "Your SvR 2011 game folder (the one with pac\\m.pac)" });
        var p = r.FirstOrDefault()?.TryGetLocalPath();
        if (p == null) return;
        GameBox.Text = p;
        await Scan();
    }

    async Task Scan()
    {
        string game = (GameBox.Text ?? "").Trim().Trim('"');
        Status("Reading the game's moves…");
        ScanButton.IsEnabled = false;
        PortButton.IsEnabled = false;
        try
        {
            var (c, s) = await Task.Run(() =>
            {
                var c = new GameCatalog(game);
                string tmpl = Path.Combine(game, "pac", "ch", "ch125.pac");
                if (!File.Exists(tmpl)) tmpl = Directory.GetFiles(Path.Combine(game, "pac", "ch"), "ch*.pac").OrderBy(x => x).First();
                return (c, Skeleton.Load(tmpl));
            });
            cat = c; skel = s; comp = new Composer(c, s);
            lastPort = null;
            try
            {
                var rr = await Task.Run(() => new MovePack(c, comp).ReadRecords());
                recs = rr;
                var exh = rr.Exh;
                comp.Exh = (id, x) => exh.TryGetValue(id, out var l) && l.Count > 0 ? (l.FirstOrDefault(q => q.R[2] == x).R ?? l[0].R) : null;
            }
            catch { /* (no EXH: reactions use the defaults) */ }
            View.SetSkeleton(s);
            allMoves = c.Moves.Values.OrderBy(m => !m.Usable && m.Pack == null).ThenBy(m => m.Name, StringComparer.OrdinalIgnoreCase).Select(m => new MoveItem(m)).ToList();
            // the type list with how many moves each has (the ones the library shows: pickable + installed packs)
            var shown = allMoves.Where(m => m.Info.Usable || m.Info.Pack != null).ToList();
            int keep = Math.Max(0, CategoryBox.SelectedIndex);
            CategoryBox.ItemsSource = new[] { $"All types ({shown.Count})" }.Concat(MoveCategory.Names.Select(n => $"{n} ({shown.Count(m => m.Info.Category == n)})")).ToList();
            CategoryBox.SelectedIndex = keep;
            if (Environment.GetEnvironmentVariable("ANIMIT_GAME") == null) SaveSetting("game", game);   // (a test run keeps your folder)
            blocks.Clear();
            Rebuild(true);
            Filter();
            Status($"{c.Moves.Count} moves read from the game and {c.Packs.Count} installed packs.");
        }
        catch (Exception e) { Status("Could not read the game: " + e.Message); }
        finally { ScanButton.IsEnabled = true; PortButton.IsEnabled = cat != null; }
    }

    async Task PortMoves()
    {
        if (cat == null) return;
        SetPlaying(false);
        var dialog = new PorterDialog(cat, skel, lastPort);
        var result = await dialog.ShowDialog<AnimationPorter.BuiltPack?>(this) ?? dialog.Built;
        if (result == null) return;
        lastPort = result;
        try
        {
            if (!result.Ids.All(id => cat.Moves.TryGetValue(id, out var move) && move.Pack == Path.GetFileName(result.Directory)))
                cat.LoadPreviewPack(result.Directory);
            if (comp != null)
            {
                recs = new MovePack(cat, comp).ReadRecords();
                var records = recs.Exh;
                comp.Exh = (id, x) => records.TryGetValue(id, out var list) && list.Count > 0 ? list.FirstOrDefault(q => q.R[2] == x).R ?? list[0].R : null;
            }
            allMoves = cat.Moves.Values.OrderBy(m => !m.Usable && m.Pack == null).ThenBy(m => m.Name, StringComparer.OrdinalIgnoreCase).Select(m => new MoveItem(m)).ToList();
            CategoryBox.SelectedIndex = 0;
            foreach (var toggle in new[] { FilterAll, FilterStock, FilterPacks }) toggle.IsChecked = toggle == FilterAll;
            SearchBox.Text = result.Ids[0].ToString(); Filter();
            MoveList.SelectedItem = (MoveList.ItemsSource as IEnumerable<MoveItem>)?.FirstOrDefault(m => m.Id == result.Ids[0]);
            Status($"Ported move ready in the library. Add it to the timeline to preview or splice it. Pack: {result.Directory}");
        }
        catch (Exception e) { Status("Could not load the ported pack: " + e.Message); }
    }

    void Filter()
    {
        string q = (SearchBox.Text ?? "").Trim();
        bool stock = FilterStock.IsChecked == true, packs = FilterPacks.IsChecked == true;
        string? type = CategoryBox.SelectedIndex > 0 ? MoveCategory.Names[CategoryBox.SelectedIndex - 1] : null;
        var l = allMoves.Where(m =>
            (type == null || m.Info.Category == type) &&
            (!stock || m.Info.Pack == null) && (!packs || m.Info.Pack != null) &&
            (stock || packs || m.Info.Usable || m.Info.Pack != null) &&
            (q.Length == 0 || m.Name.Contains(q, StringComparison.OrdinalIgnoreCase) || m.Id.ToString() == q)).Take(600).ToList();
        MoveList.ItemsSource = l;
        CountText.Text = cat == null ? "no game" : $"{l.Count} shown · {allMoves.Count} moves";
    }

    sealed record PhaseItem(int X, string Text) { public override string ToString() => Text; }
    int pickPhase;

    void Pick(MoveItem? m)
    {
        picked = m;
        AddButton.IsEnabled = m != null && comp != null;
        if (m == null) { PickText.Text = "Choose a move from the library."; PickPhase.IsVisible = false; return; }
        // a move's parts by plain name (Jump, Landing on a standing opponent, Hit, Hold...: PhaseGuide); 1-frame
        // placeholders are left out
        var tr = TrackOf(m);
        var phases = m.Info.Phases.Where(x => PhaseGuide.Shown(m.Info, x)).DefaultIfEmpty(m.Info.MainPhase)
            .Select(x => new PhaseItem(x, PhaseGuide.Label(m.Info, x, tr))).ToList();
        pickingPhase = true;
        PickPhase.ItemsSource = phases;
        PickPhase.SelectedItem = phases.FirstOrDefault(p => p.X == m.Info.MainPhase) ?? phases.FirstOrDefault();
        PickPhase.IsVisible = phases.Count > 1;
        pickingPhase = false;
        SetPhase(m, m.Info.MainPhase);
    }

    void SetPhase(MoveItem m, int x)
    {
        pickPhase = x;
        int n = m.Info.AttackerAt(x) ?? 1;
        PickIn.Maximum = PickOut.Maximum = Math.Max(0, n - 1);
        PickIn.Value = 0;
        PickOut.Value = n - 1;
        string start = "";
        try { start = MotionMath.StartNameOf(comp!.Source(m.Id, x, 0)); } catch { }
        PickText.Text = $"{m.Name}  ·  {PhaseGuide.Name(m.Info, x, TrackOf(m))}  ·  {start}  ·  {n} frames at 30 FPS";
        // "whole move": the parts the game plays one after the other (shown only when it is more than this one part)
        var chain = PhaseGuide.WholeMove(m.Info, x, TrackOf(m));
        WholeButton.IsVisible = chain.Count > 1 && comp != null;
        WholeText.Text = chain.Count > 1 ? PhaseGuide.ChainText(m.Info, chain, TrackOf(m)) : "";
        if (blocks.Count == 0) PreviewMove(m);
        else if (SplitToggle.IsChecked == true) ShowSide(m);
    }

    Func<int, Track?> TrackOf(MoveItem m) => x =>
    {
        try { return comp?.Source(m.Id, x, 0); } catch { return null; }
    };

    /// <summary>every part of the picked move the game plays one after the other, each in full, as clips</summary>
    void AddWholeMove()
    {
        if (picked == null || comp == null) return;
        var chain = PhaseGuide.WholeMove(picked.Info, pickPhase, TrackOf(picked));
        bool first = true;
        foreach (int x in chain)
        {
            int n = picked.Info.AttackerAt(x) ?? 1;
            var b = MakeBlock(picked, 0, n - 1, x);
            if (!first)
            {
                // (the parts of one move share one space: each follows the one before with a cut and the same placement)
                b.Join = JoinMode.HardCut;
                b.Align = RootAlign.Continue;
            }
            blocks.Add(b);
            first = false;
        }
        Rebuild(blocks.Count == chain.Count);
        SelectBlock(blocks.Count - 1);
        Status($"Added the whole {picked.Name}: {PhaseGuide.ChainText(picked.Info, chain, TrackOf(picked))}.");
    }
    bool pickingPhase;

    /// <summary>with an empty timeline, the picked move plays on its own</summary>
    // ---------------------------------------------------------------- split view (library preview next to the timeline)
    readonly DispatcherTimer sideTimer;
    int sideFrames;
    double sideAt;
    bool sideSkel;

    void ShowSide(MoveItem m)
    {
        if (comp == null || skel == null) return;
        try
        {
            var c = comp.Compose(new[] { MakeBlock(m, 0, (m.Info.AttackerAt(pickPhase) ?? 1) - 1) });
            var (A, R) = comp.Joints(c);
            if (!sideSkel) { SideView.SetSkeleton(skel); sideSkel = true; }
            SideView.SetStage(c.Stage.Yaw, c.Stage.Shift, c.Stage.AttackerY, c.Stage.ReceiverY);
            SideView.SetFrames(A, R, true);
            SideView.SetPartner(comp.PartnerJoints(c));
            sideFrames = c.Frames; sideAt = 0;
            SideView.CurrentFrame = 0;
            SideTitle.Text = $"{m.Name}  ·  {PhaseGuide.Name(m.Info, pickPhase, TrackOf(m))}";
            SetSplit(true);
        }
        catch (Exception e) { Status("Could not preview: " + e.Message); }
    }

    void SetSplit(bool on)
    {
        on &= blocks.Count > 0;
        SidePane.IsVisible = on;
        TimelineTag.IsVisible = on;
        StageGrid.ColumnDefinitions[1].Width = on ? new GridLength(1, GridUnitType.Star) : new GridLength(0);
        if (on) sideTimer.Start(); else sideTimer.Stop();
    }

    void SideTick()
    {
        if (sideFrames <= 1) return;
        sideAt = sideAt + 1 > sideFrames - 1 ? 0 : sideAt + 1;
        SideView.CurrentFrame = sideAt;
        SideFrame.Text = $"frame {sideAt:0} / {sideFrames - 1}";
    }

    void PreviewMove(MoveItem m)
    {
        if (comp == null) return;
        try
        {
            var b = MakeBlock(m, 0, (m.Info.AttackerAt(pickPhase) ?? 1) - 1);
            compo = comp.Compose(new[] { b });
            var (A, R) = comp.Joints(compo);
            View.SetStage(compo.Stage.Yaw, compo.Stage.Shift, compo.Stage.AttackerY, compo.Stage.ReceiverY);
            View.SetFrames(A, R, true);
            View.SetPartner(comp.PartnerJoints(compo));
            Timeline.SetBlocks(Array.Empty<TimelineBlock>(), compo.Frames);
            SetFrame(0);
            SetPlaying(true);
        }
        catch (Exception e) { Status("Could not preview: " + e.Message); }
    }

    Block MakeBlock(MoveItem m, int fin, int fout) => MakeBlock(m, fin, fout, pickPhase);

    Block MakeBlock(MoveItem m, int fin, int fout, int phase) => new()
    {
        Name = m.Info.Phases.Count(x => PhaseGuide.Shown(m.Info, x)) <= 1 ? m.Name : $"{m.Name} · {PhaseGuide.Name(m.Info, phase, TrackOf(m))}",
        Attacker = new LaneRef(m.Id, phase, 0, $"{m.Id}_{phase}_0"),
        Receiver = m.Info.ReceiverAt(phase) != null ? new LaneRef(m.Id, phase, 1, $"{m.Id}_{phase}_1") : null,
        In = fin, Out = fout, SourceLength = m.Info.AttackerAt(phase) ?? 1,
        Join = blocks.Count > 0 ? JoinMode.Smooth : JoinMode.HardCut, Blend = 10,
        Align = blocks.Count > 0 ? RootAlign.PlanarGroup : RootAlign.None,
    };

    // ---------------------------------------------------------------- blocks
    void AddBlock()
    {
        if (picked == null) return;
        int fin = (int)(PickIn.Value ?? 0), fout = (int)(PickOut.Value ?? 0);
        if (fout < fin) (fin, fout) = (fout, fin);
        blocks.Add(MakeBlock(picked, fin, fout));
        Rebuild(blocks.Count == 1);
        SelectBlock(blocks.Count - 1);
        Status($"Added {picked.Name} to the timeline (frames {fin} – {fout}).");
    }

    /// <summary>cut the clip under frame f in two: the second half continues exactly where the first was (same line-up,
    /// same turn / move, no blend) - then either half can be removed, moved or changed on its own</summary>
    int handleLane;                                                   // which wrestler the 3D handle moves
    (V3[] A, V3[]? R)? copiedBodies;                                  // Copy: both wrestlers' joints at a frame

    /// <summary>Paste: the selected clip starts where the copied wrestlers were. Each wrestler's first pose (raw: the clip with
    /// no line-up and no turn / move) is fitted onto his copied body; the result becomes his Turn / X / Z, line-up Off.</summary>
    void PasteBodies()
    {
        if (Current is not Block b || copiedBodies is not var (qa, qr) || comp == null || skel == null) return;
        int i = BlockList.SelectedIndex;
        var raw = blocks.Select(x => x.Clone()).ToList();
        var rb = raw[i];
        rb.Align = RootAlign.None;
        rb.AttackerTurn = rb.AttackerX = rb.AttackerZ = rb.ReceiverTurn = rb.ReceiverX = rb.ReceiverZ = 0;
        var c0 = comp.Compose(raw);
        var (fa, fr) = c0.Firsts[i];
        (double, double, double) Fit(Pose from, V3[] to)
        {
            var (yaw, shift, _) = Composer.FitPlanar(skel.Fk(from).Select(p => (p, 1.0)).ToList(), to.ToList());
            return (yaw * 180 / Math.PI, shift.X, shift.Z);
        }
        b.Align = RootAlign.None;
        (b.AttackerTurn, b.AttackerX, b.AttackerZ) = Fit(fa, qa);
        if (fr != null && qr != null) (b.ReceiverTurn, b.ReceiverX, b.ReceiverZ) = Fit(fr, qr);
        Rebuild(false);
        SelectBlock(i);
        Status($"{b.Name} now starts where the copied wrestlers were (line-up Off; Turn / X / Z hold its placement).");
    }
    List<V3[]?> jA = new(), jR = new();                               // the last composed joints (world)

    /// <summary>turn one wrestler of the selected clip by d degrees where he stands at the clip start (keeps that spot)</summary>
    void TurnLane(int lane, double d)
    {
        if (Current is not Block b || Math.Abs(d) < 1e-9) return;
        int i = BlockList.SelectedIndex;
        var c0 = LaneSpot(i, lane);
        var R = M3.Ry(d * Math.PI / 180);
        if (lane == 0)
        {
            var s = R * new V3(b.AttackerX - c0.X, 0, b.AttackerZ - c0.Z);
            b.AttackerTurn += d; b.AttackerX = c0.X + s.X; b.AttackerZ = c0.Z + s.Z;
        }
        else
        {
            var s = R * new V3(b.ReceiverX - c0.X, 0, b.ReceiverZ - c0.Z);
            b.ReceiverTurn += d; b.ReceiverX = c0.X + s.X; b.ReceiverZ = c0.Z + s.Z;
        }
        Rebuild(false);
        SelectBlock(i);
    }

    /// <summary>where a wrestler of clip i stands (hips, on the mat) on the clip's first frame, in the world</summary>
    V3 LaneSpot(int i, int lane)
    {
        // (the first pose before the blend in: a blended first frame is mostly the clip before)
        if (compo == null || i < 0 || i >= compo.Firsts.Count || skel == null) return new V3(0, 0, 0);
        var (fa, fr) = compo.Firsts[i];
        var p = lane == 1 && fr != null ? fr : fa;
        var h = skel.Fk(p)[skel.RootJoint];
        return new V3(h.X, 0, h.Z);
    }

    void UpdateGizmo()
    {
        int i = BlockList.SelectedIndex;
        if (Current is not Block b || compo == null) { View.HideGizmo(); return; }
        int lane = handleLane == 1 && b.Receiver != null ? 1 : 0;
        View.SetGizmo(lane, LaneSpot(i, lane));
    }

    void CutAt(double f)
    {
        if (compo == null || blocks.Count == 0) return;
        int fr = (int)Math.Round(f);
        for (int i = 0; i < blocks.Count; i++)
        {
            var (start, len) = compo.Spans[i];
            if (fr <= start || fr >= start + len) continue;
            var b = blocks[i];
            int sf = Math.Clamp(b.In + (int)Math.Round((fr - start) * b.Speed), b.In + 1, b.Out);
            var b2 = b.Clone();
            b2.In = sf; b2.Join = JoinMode.HardCut; b2.Align = RootAlign.Continue;
            b.Out = sf - 1;
            blocks.Insert(i + 1, b2);
            Rebuild(false);
            SelectBlock(i + 1);
            Status($"Cut {b.Name} at frame {fr}: frames {b.In} – {b.Out} and {b2.In} – {b2.Out}. Select a part and press Remove to drop it.");
            return;
        }
        Status(fr <= 0 ? "Move the playhead into a clip to cut it." : "The playhead is on a join already: nothing to cut.");
    }

    void RemoveBlock(int i)
    {
        if (i < 0 || i >= blocks.Count) return;
        blocks.RemoveAt(i);
        if (blocks.Count > 0) { blocks[0].Join = JoinMode.HardCut; blocks[0].Align = RootAlign.None; }
        Rebuild(false);
        SelectBlock(Math.Min(i, blocks.Count - 1));
    }

    void MoveBlock(int from, int to)
    {
        if (from == to || from < 0 || to < 0 || from >= blocks.Count || to >= blocks.Count) return;
        var b = blocks[from];
        blocks.RemoveAt(from);
        blocks.Insert(to, b);
        Rebuild(false);
        SelectBlock(to);
    }

    void Trim(int i, int edge, int d)
    {
        if (i < 0 || i >= blocks.Count) return;
        var b = blocks[i];
        int max = Math.Max(0, b.SourceLength - 1);
        int step = (int)Math.Round(d * b.Speed);
        if (step == 0) step = Math.Sign(d);
        if (edge < 0) b.In = Math.Clamp(b.In + step, 0, b.Out);
        else b.Out = Math.Clamp(b.Out + step, b.In, max);
        Rebuild(false);
        SelectBlock(i);
    }

    void SelectBlock(int i)
    {
        updating = true;
        BlockList.SelectedIndex = i;
        Timeline.Selected = i;
        var b = Current;
        Inspector.IsEnabled = b != null;
        if (b != null)
        {
            SelName.Text = b.Name;
            SelAttacker.Text = $"{b.Attacker.Label}  ·  {(cat?.Names.GetValueOrDefault(b.Attacker.Id) ?? "")}";
            SelReceiver.Text = b.Receiver is LaneRef r ? $"{r.Label}  ·  {(cat?.Names.GetValueOrDefault(r.Id) ?? "")}" : "none: keeps the last pose";
            // a strike with no receiver track: choose which of the game's hit reactions the preview shows
            var choices = comp?.ReactionChoices(b);
            ReactionPanel.IsVisible = choices is { Count: > 0 };
            if (choices is { Count: > 0 })
            {
                SelReceiver.Text = "the game's hit reaction (no track in the move)";
                ReactionBox.ItemsSource = choices.Select((k, n) => $"{k.Id}_{k.X}_{k.Y}" + (n == 0 ? "  (seen most)" : "")).Append("No reaction").ToList();
                ReactionBox.SelectedIndex = b.Reaction < 0 ? choices.Count : Math.Min(b.Reaction, choices.Count - 1);
            }
            SelIn.Maximum = SelOut.Maximum = Math.Max(0, b.SourceLength - 1);
            SelIn.Value = b.In;
            SelOut.Value = b.Out;
            SelFrames.Value = b.Length;
            SelSpeed.Value = (decimal)Math.Round(b.Speed * 100, 1);
            SelJoin.SelectedIndex = (int)b.Join;
            SelBlend.Value = b.Blend;
            SelAlign.SelectedIndex = (int)b.Align;
            AdjATurn.Value = (decimal)b.AttackerTurn; AdjAX.Value = (decimal)b.AttackerX; AdjAZ.Value = (decimal)b.AttackerZ;
            AdjRTurn.Value = (decimal)b.ReceiverTurn; AdjRX.Value = (decimal)b.ReceiverX; AdjRZ.Value = (decimal)b.ReceiverZ;
            AdjRTurn.IsEnabled = AdjRX.IsEnabled = AdjRZ.IsEnabled = b.Receiver != null;
            bool first = i == 0;
            SelJoin.IsEnabled = SelBlend.IsEnabled = SelAlign.IsEnabled = !first;
            ImportText.Text = b.Attacker.Id != (b.Receiver?.Id ?? b.Attacker.Id) ? "The tracks come from different moves" : "Both tracks come from the same move";
        }
        else SelName.Text = "—";
        updating = false;
        UpdateGizmo();
    }

    void ApplyInspector()
    {
        if (Current is not Block b) return;
        int i = BlockList.SelectedIndex;
        b.In = (int)(SelIn.Value ?? 0);
        b.Out = Math.Max(b.In, (int)(SelOut.Value ?? b.In));
        b.Speed = Math.Max(0.05, (double)(SelSpeed.Value ?? 100) / 100);
        b.Join = (JoinMode)Math.Clamp(SelJoin.SelectedIndex, 0, 2);
        b.Blend = (int)(SelBlend.Value ?? 8);
        b.Align = (RootAlign)Math.Max(0, SelAlign.SelectedIndex);
        if (i == 0) { b.Join = JoinMode.HardCut; b.Align = RootAlign.None; }
        Rebuild(false);
        SelectBlock(i);
        Status($"Applied: {b.Name}, frames {b.In} – {b.Out}, {b.Length} output frames.");
    }

    void ShowLaneMenu(int i, int lane, Avalonia.Point at)
    {
        var menu = new ContextMenu
        {
            ItemsSource = new object[]
            {
                new MenuItem { Header = "Cut at the playhead (C)", Command = new Cmd(() => { CutAt(frame); return Task.CompletedTask; }) },
                new MenuItem { Header = "Swap this track…", Command = new Cmd(async () => await ImportInto(i, lane)) },
                new MenuItem { Header = "Speed / Duration…", Command = new Cmd(async () => await SpeedDialogFor(i)) },
                new Separator(),
                new MenuItem { Header = "Remove clip", Command = new Cmd(() => { RemoveBlock(i); return Task.CompletedTask; }) },
            },
        };
        menu.Open(Timeline);
    }

    async Task ImportInto(int i, int lane)
    {
        if (cat == null || i < 0 || i >= blocks.Count) return;
        var dlg = new ImportDialog(cat, lane);
        var r = await dlg.ShowDialog<LaneRef?>(this);
        if (r == null) return;
        var b = blocks[i];
        if (lane == 0)
        {
            b.Attacker = r;
            b.SourceLength = comp!.Source(r.Id, r.X, r.Y).Frames.Count;
            b.In = 0; b.Out = b.SourceLength - 1;
        }
        else b.Receiver = r;
        Rebuild(false);
        SelectBlock(i);
        Status($"{r.Label} is now the {(lane == 0 ? "attacker" : "receiver")} track of {b.Name}.");
        await SpeedDialogFor(i, lane);
    }

    async Task SpeedDialogFor(int i, int lane = 0)
    {
        if (i < 0 || i >= blocks.Count || comp == null) return;
        var b = blocks[i];
        var src = lane == 1 && b.Receiver is LaneRef r ? r : b.Attacker;
        var dlg = new SpeedDialog(b, src, cat?.Names.GetValueOrDefault(src.Id) ?? "");
        if (await dlg.ShowDialog<bool>(this))
        {
            Rebuild(false);
            SelectBlock(i);
        }
    }

    // ---------------------------------------------------------------- compose + play
    void Rebuild(bool reframe)
    {
        if (comp == null) return;
        if (blocks.Count == 0) SetSplit(false);
        try
        {
            compo = comp.Compose(blocks);
            var (A, R) = comp.Joints(compo);
            jA = A; jR = R;
            View.SetStage(compo.Stage.Yaw, compo.Stage.Shift, compo.Stage.AttackerY, compo.Stage.ReceiverY);
            View.SetFrames(A, R, reframe);
            View.SetPartner(comp.PartnerJoints(compo));
            var tb = new List<TimelineBlock>();
            for (int i = 0; i < blocks.Count; i++)
            {
                var b = blocks[i];
                var (s, n) = compo.Spans[i];
                var (ha, hr) = compo.Holds[i];
                tb.Add(new TimelineBlock(s, n, b.Name, b.Attacker.Label, b.Receiver?.Label ?? (i < compo.ReactionLabels.Count && compo.ReactionLabels[i] is string rl ? rl : "— hold"), ha, hr ?? (b.Receiver == null && !(i < compo.ReactionLabels.Count && compo.ReactionLabels[i] != null) ? 0 : null),
                                         i > 0 && b.Join != JoinMode.HardCut, b.Blend));
            }
            Timeline.SetBlocks(tb, compo.Frames);
            RefreshEvents();
            Timeline.Zoom = ZoomSlider.Value;
            updating = true;
            int sel = BlockList.SelectedIndex;
            int acc = 0;
            BlockList.ItemsSource = blocks.Select((b, k) => { var it = new BlockItem(b, k, acc); acc += b.Length; return it; }).ToList();
            BlockList.SelectedIndex = Math.Min(sel, blocks.Count - 1);
            updating = false;
            SaveButton.IsEnabled = blocks.Count > 0;
            CutButton.IsEnabled = blocks.Count > 0;
            BlockCountText.Text = blocks.Count == 0 ? "no clips yet" : $"{blocks.Count} clip{(blocks.Count == 1 ? "" : "s")} on the timeline";
            SetFrame(Math.Min(frame, Math.Max(0, compo.Frames - 1)));
            if (blocks.Count > 0)
                Status($"{blocks.Count} clip{(blocks.Count == 1 ? "" : "s")} · {compo.Frames} frames ({compo.Frames / 30.0:0.0} s)");
        }
        catch (Exception e) { Status("Could not build the preview: " + e.Message); }
    }

    void SetFrame(double f)
    {
        int n = compo?.Frames ?? 0;
        frame = n == 0 ? 0 : Math.Clamp(f, 0, n - 1);
        View.CurrentFrame = frame;
        Timeline.Frame = frame;
        EvLine.CurrentFrame = frame;
        FrameText.Text = $"{frame:0.00}";
    }

    void SetPlaying(bool on)
    {
        playing = on && (compo?.Frames ?? 0) > 1;
        PlayButton.Content = new Avalonia.Controls.Shapes.Path { Fill = Avalonia.Media.Brushes.White, Data = Avalonia.Media.Geometry.Parse(playing ? "M 0,0 H 4 V 13 H 0 Z M 7,0 H 11 V 13 H 7 Z" : "M 0,0 L 11,6.5 L 0,13 Z") };
        if (playing) timer.Start(); else timer.Stop();
    }

    void Tick()
    {
        int n = compo?.Frames ?? 0;
        if (n <= 1) { SetPlaying(false); return; }
        double f = frame + 1;
        if (f > n - 1)
        {
            if (LoopToggle.IsChecked == true) f = 0;
            else { SetPlaying(false); f = n - 1; }
        }
        SetFrame(f);
    }

    async Task SaveToGame()
    {
        if (cat == null || comp == null || blocks.Count == 0) return;
        var first = blocks[0];
        var pack = new MovePack(cat, comp);
        string suggested = LoadSetting("lastName") is string ln && ln.Length > 0 ? ln : "[AnimIT] " + first.Name;
        // donors: stock moves in a list, with match records, normal (YMKs) animation, not taunts
        var usable = cat.Moves.Values.Where(m => m.Pack == null && cat.Usable(m.Id) && cat.WazeIds.Contains(m.Id) && m.Category != "Taunts"
                                                 && (recs == null || recs.Exh.ContainsKey(m.Id)) && m.Phases.Any()
                                                 && cat.Best(m.Id, m.MainPhase, 0)?.Kind == "YMKs").ToList();
        var choice = await new SaveDialog(cat, suggested, first.Attacker.Id, usable, compo?.Frames ?? 0, pack.PackDir("animit_moves")).ShowDialog<SaveChoice?>(this);
        if (choice == null) return;
        string name = choice.Name;
        string donorName = cat.Names.GetValueOrDefault(choice.Donor) ?? $"move {choice.Donor}";
        SaveSetting("lastName", name);
        Status("Saving to the game…");
        SaveButton.IsEnabled = false;
        try
        {
            var list = blocks.ToList();
            var r = await Task.Run(() => pack.Save(list, name, choice.Donor));
            Status($"Saved '{r.Name}' as move {r.Id} in {r.Dir}");
            await new MessageDialog("Saved", "Saved to the game",
                $"'{r.Name}' is move {r.Id} in the pack {r.Dir}.\n\n" +
                "To test it:\n1. Start (or restart) the game: packs are read when it starts.\n" +
                $"2. Create-a-Moveset: open the {r.Category} list (the same list as '{donorName}'), find '{r.Name}' and give it to a wrestler.\n" +
                "3. Play a match and do the move.\n\nDetails:\n" + string.Join("\n", r.Notes.Select(n => "- " + n))).ShowDialog(this);
        }
        catch (Exception e)
        {
            Status("Could not save: " + e.Message);
            await new MessageDialog("Not saved", "Could not save", e.Message).ShowDialog(this);
        }
        finally { SaveButton.IsEnabled = blocks.Count > 0; }
    }

    void Status(string s) => StatusText.Text = s;

    // ---------------------------------------------------------------- the spliced move's events
    List<MovePack.SpliceEvent> evList = new();
    (int Block, string Key)? evSel;

    MovePack.SpliceEvent? SelEvent() => evSel is var (bi, k) ? evList.FirstOrDefault(e => e.Block == bi && e.Key == k) : null;

    /// <summary>the events the saved move will have (MovePack.Events: what the save writes), on the event lanes</summary>
    void RefreshEvents()
    {
        evList = new();
        if (cat != null && comp != null && compo != null && blocks.Count > 0 && compo.Spans.Count == blocks.Count)
        {
            try { evList = new MovePack(cat, comp).Events(blocks, compo, recs); }
            catch (Exception e) { Status("Could not read the events: " + e.Message); }
        }
        bool all = EventsAll.IsChecked == true;
        var marks = new List<EventTimeline.Mark>();
        object? selTag = null;
        foreach (var e in evList)
        {
            var ev = new PackEvents.Ev { Track = e.Lane, Frame = e.Frame, Orig = e.Base, Op = e.Sound ? -1 : e.Op, Data = e.Data, Sound = e.Sound ? Bin.U16(e.Data, 0) : 0 };
            var (name, color, major) = PackEvents.Describe(ev);
            if (!major && !all) continue;
            marks.Add(new EventTimeline.Mark(e, e.Sound ? 2 + e.Lane : e.Lane, e.Frame, name, Avalonia.Media.Color.Parse(color), major));
            if (evSel is var (bi, k) && e.Block == bi && e.Key == k) selTag = e;
        }
        if (selTag == null) evSel = null;
        EvLine.Set(marks, Math.Max(1, compo?.Frames ?? 1));
        EvLine.Select(selTag);
        EvLine.CurrentFrame = frame;
        EventsReset.IsEnabled = blocks.Any(b => b.EventShift.Count > 0);
        EventsText();
    }

    void EventsText()
    {
        var e = SelEvent();
        EventsNudgeL.IsEnabled = e != null;
        if (blocks.Count == 0) { EventsInfo.Text = "Add clips to see the move's events."; return; }
        if (e == null)
        {
            int hits = evList.Count(x => !x.Sound && x.Op is 86 or (>= 32 and <= 36));
            int shakes = evList.Count(x => !x.Sound && x.Op is >= 96 and <= 99);
            int sounds = evList.Count(x => x.Sound);
            int moved = blocks.Sum(b => b.EventShift.Count);
            EventsInfo.Text = $"{hits} hit · {shakes} ring shake · {sounds} sounds" + (moved > 0 ? $" · {moved} moved by you" : "") +
                              "  —  drag a mark to move it; Save to game uses these frames";
            return;
        }
        var ev = new PackEvents.Ev { Track = e.Lane, Op = e.Sound ? -1 : e.Op, Data = e.Data, Sound = e.Sound ? Bin.U16(e.Data, 0) : 0 };
        EventsInfo.Text = $"{PackEvents.Describe(ev).Name} · {(e.Lane == 0 ? "attacker" : "receiver")} · from clip {e.Block + 1} ({blocks[e.Block].Name}) · frame {e.Frame}" +
                          (e.Frame != e.Base ? $" (was {e.Base})" : "");
    }

    void MoveEvent(MovePack.SpliceEvent e, int f)
    {
        if (e.Block >= blocks.Count) return;
        int n = Math.Max(1, compo?.Frames ?? 1);
        int to = Math.Clamp(f, 0, n - 1), sh = to - e.Base;
        var d = blocks[e.Block].EventShift;
        if (sh == 0) d.Remove(e.Key); else d[e.Key] = sh;
        evSel = (e.Block, e.Key);
        RefreshEvents();
        SetPlaying(false);
        SetFrame(to);
        Status("Event moved. Play the move to check it; Save to game writes it at this frame.");
    }
}

/// <summary>a small ICommand for menu items</summary>
sealed class Cmd : System.Windows.Input.ICommand
{
    readonly Func<Task> run;
    public Cmd(Func<Task> run) => this.run = run;
#pragma warning disable CS0067
    public event EventHandler? CanExecuteChanged;
#pragma warning restore CS0067
    public bool CanExecute(object? p) => true;
    public async void Execute(object? p) => await run();
}
