using System;
using System.Collections.Generic;
using System.Linq;

namespace AnimIT.Core;

public enum JoinMode { HardCut, Crossfade, Smooth }
public enum RootAlign { None, PlanarGroup, FollowAttacker, FollowReceiver, Continue }

/// <summary>a lane's source: a move's track (an import puts another move's track into one lane)</summary>
public sealed record LaneRef(int Id, int X, int Y, string Label);

/// <summary>one synchronized block of the timeline</summary>
public sealed class Block
{
    public LaneRef Attacker = new(0, 0, 0, "");
    public LaneRef? Receiver;
    public string Name = "";
    public int In, Out;
    public double Speed = 1.0;
    public JoinMode Join = JoinMode.HardCut;
    public int Blend = 8;
    public RootAlign Align = RootAlign.None;
    public int SourceLength;                 // the attacker source's frames (for the UI)
    /// <summary>your own placement for the whole block (every frame), on top of the alignment: each wrestler turned
    /// about his own start point (degrees) and moved on the ground (model units)</summary>
    public double AttackerTurn, ReceiverTurn, AttackerX, AttackerZ, ReceiverX, ReceiverZ;
    /// <summary>which game hit reaction to show for a strike with no receiver track (-1 none)</summary>
    public int Reaction;
    /// <summary>your event timing for this clip: event key (SpliceEvent.Key) -> frames moved from where it would play</summary>
    public Dictionary<string, int> EventShift = new();
    public int Length => Math.Max(1, (int)Math.Round((Out - In + 1) / Math.Max(0.05, Speed)));
    public Block Clone() { var c = (Block)MemberwiseClone(); c.EventShift = new(EventShift); return c; }
}

public sealed class Composition
{
    public List<Pose?> Attacker = new(), Receiver = new();
    /// <summary>a third wrestler (track 2: the partner of a double-team move), placed like the receiver; preview only</summary>
    public List<Pose?> Partner = new();
    /// <summary>per block, each wrestler's whole placement (degrees, shift on the mat): p -> Ry(turn) p + shift</summary>
    public List<(double ATurn, V3 AShift, double RTurn, V3 RShift)> Places = new();
    /// <summary>per block, each wrestler's first pose after its placement, before any blend in (world / attacker space)</summary>
    public List<(Pose A, Pose? R)> Firsts = new();
    /// <summary>per block, the game reaction shown on the receiver (null: none / the move's own receiver)</summary>
    public List<string?> ReactionLabels = new();
    public List<(int Start, int Length)> Spans = new();
    /// <summary>per block and lane: the output frame from which that lane holds its last pose, or null</summary>
    public List<(int? Attacker, int? Receiver)> Holds = new();
    public int? Start;
    public (double K, double D) Place = (15.0, Math.PI);   // the first block's receiver placement
    /// <summary>where in the ring the first attacker starts (view only): his facing and his spot on the mat</summary>
    public (double Yaw, V3 Shift, double AttackerY, double ReceiverY) Stage = (0, new V3(0, 0, 0), 0, 0);
    public int Frames => Attacker.Count;
}

/// <summary>The splicer. Spaces: the attacker's track is the move's space; the receiver's is his own - 2011 places
/// him by the move's start class. While composing, the receiver is brought into the attacker's space with his
/// block's placement, aligned there, and at the end put back into the space of the FIRST block's placement: how
/// the game places him for the finished move. Root alignment "planar group": the block is moved on the ground and
/// turned about y so the attacker starts where / how the previous attacker ended; the receiver moves alike, so the
/// pair keeps its authored spacing. A lane shorter than its block holds its last pose.</summary>
public sealed class Composer
{
    readonly GameCatalog cat;
    readonly Skeleton skel;
    readonly Dictionary<(int, int, int), Track> cache = new();
    readonly Queue<(int, int, int)> order = new();

    public Composer(GameCatalog cat, Skeleton skel) { this.cat = cat; this.skel = skel; }

    /// <summary>a move's EXH record for a phase (set by the window after loading: misc.pac WAZA/DATA)</summary>
    public Func<int, int, byte[]?>? Exh;

    /// <summary>the game reactions a block's receiver can show: only for a move with no receiver track at all</summary>
    public IReadOnlyList<(int Id, int X, int Y)>? ReactionChoices(Block b)
    {
        if (b.Receiver != null || !cat.Moves.TryGetValue(b.Attacker.Id, out var mv) || mv.Tracks.Keys.Any(k => k.Y == 1)) return null;
        if (mv.Category == "Taunts" || mv.Category == "Ring in / out") return null;
        var exh = Exh?.Invoke(b.Attacker.Id, b.Attacker.X);
        return HitReactions.For(exh != null && exh.Length > 0x10 ? exh[0x10] : null, mv.Category).Where(k => cat.Best(k.Id, k.X, k.Y) != null).ToList();
    }

    public Track Source(int id, int x, int y)
    {
        var k = (id, x, y);
        if (cache.TryGetValue(k, out var t)) return t;
        var m = cat.Best(id, x, y) ?? throw new KeyNotFoundException($"move {id} has no track ({x}, {y})");
        // (taunts and holds are YMBs: ADPCM channels in their bank, decoded with the bank's own channel tables)
        t = m.Kind == "YMBs" && m.File == null
            ? Ymbs.Decode(cat.BankBytes(m.Bank), id, x, y, skel) ?? throw new InvalidOperationException($"YMBs motion {id}_{x}_{y}: layout not understood")
            : Track.Decode(cat.Data(m), cat.T(m), m.Packed, m.Frames);
        t.Id = id; t.X = x; t.Y = y; t.Bank = m.Bank;
        if (order.Count > 64) cache.Remove(order.Dequeue());
        cache[k] = t;
        order.Enqueue(k);
        return t;
    }

    static Pose Sample(List<Pose> fr, double t)
    {
        t = Math.Clamp(t, 0, fr.Count - 1);
        int i = (int)Math.Floor(t);
        double u = t - i;
        if (u < 1e-6 || i + 1 >= fr.Count) return fr[i];
        return MotionMath.Lerp(fr[i], fr[i + 1], u);
    }

    Pose ToAttacker(Pose p, (double K, double D) kd)
    {
        var o = MotionMath.TurnRoot(p, kd.D, skel.RootRest);
        if (ReferenceEquals(o, p)) o = p.Clone();
        o.Root = M3.Ry(kd.D) * p.Root + new V3(0, 0, -kd.K);
        return o;
    }

    Pose ToOwn(Pose p, (double K, double D) kd)
    {
        var o = MotionMath.TurnRoot(p, -kd.D, skel.RootRest);
        if (ReferenceEquals(o, p)) o = p.Clone();
        o.Root = M3.Ry(-kd.D) * (p.Root - new V3(0, 0, -kd.K));
        return o;
    }

    public Composition Compose(IReadOnlyList<Block> blocks)
    {
        var c = new Composition();
        (double K, double D)? first = null;
        (double Yaw, V3 Shift)? lastPlace = null;   // the previous block's line-up (for a cut-off half)
        bool afterLeadIn = false;                   // the previous block was this move's own approach (see below)
        for (int bi = 0; bi < blocks.Count; bi++)
        {
            var b = blocks[bi];
            var a = Source(b.Attacker.Id, b.Attacker.X, b.Attacker.Y);
            var r = b.Receiver is LaneRef rr ? Source(rr.Id, rr.X, rr.Y) : null;
            var kd = MotionMath.PlacementOf(a);
            string? moveCat = cat.Moves.GetValueOrDefault(b.Attacker.Id)?.Category;
            // corner / rope moves (op 84 04): both on the anchor spot, the attacker facing the corner / rope. The receiver
            // faces out (Corner Forehand Chop, measured: facing 180, his root 3 in front) or into the corner (the rear
            // corner list, bit 51 / 59: Snake Eyes, Corner Bulldog, Reverse Superplex... - his root starts ~6 BEHIND his
            // spot, so facing out he stood behind the post: 2026-11-05 user report). His own start root decides: the
            // facing that keeps him inside the ring.
            bool cornerAnchor = a.Start == null && a.Anchor == 4 && (moveCat == "Corner" || moveCat == "Rope");
            if (cornerAnchor && r != null && r.Frames.Count > 0)
                kd = (kd.K, r.Frames[0].Root.Z >= 0 ? 0.0 : Math.PI);
            if (first == null) { first = kd; c.Start = a.Start; c.Place = kd; c.Stage = MotionMath.StageOf(a, cat.Moves.GetValueOrDefault(b.Attacker.Id)?.Category, cat.Flags(b.Attacker.Id)); }
            double speed = Math.Max(0.05, b.Speed);
            int fin = Math.Max(0, b.In), fout = Math.Max(fin, b.Out), length = b.Length;
            List<Pose> Lane(Track t, bool receiver)
            {
                var l = new List<Pose>(length);
                for (int j = 0; j < length; j++)
                {
                    var p = Sample(t.Frames, fin + j * speed);
                    l.Add(receiver ? ToAttacker(p, kd) : p);
                }
                return l;
            }
            int? HoldFrom(Track t) => t.Frames.Count - 1 < fout ? (int)Math.Ceiling((t.Frames.Count - 1 - fin) / speed) + 1 : null;
            var la = Lane(a, false);
            var lr = r != null ? Lane(r, true) : null;
            // a strike with no receiver track: the game plays one of its own hit reactions on the receiver when it lands -
            // shown here (preview only: a saved move stays attacker-only, so the game still picks the reaction itself)
            string? reactLabel = null;
            if (r == null && b.Reaction >= 0 && ReactionChoices(b) is { Count: > 0 } choices)
            {
                var (rid, rx, ry) = choices[Math.Min(b.Reaction, choices.Count - 1)];
                var rt = Source(rid, rx, ry);
                var mv = cat.Moves.GetValueOrDefault(b.Attacker.Id);
                int hit = HitReactions.HitFrame(a, Exh?.Invoke(b.Attacker.Id, b.Attacker.X));
                int hitOut = (int)Math.Round((hit - fin) / speed);
                // (a corner / rope move's reaction: the receiver stands on the anchor spot, 2 inside, facing out - not 12.5
                // ahead of the attacker, which was past the post)
                var kdR = cornerAnchor ? (-2.0, Math.PI) : (HitReactions.Distance(mv?.Category ?? ""), Math.PI);
                lr = new List<Pose>(length);
                for (int j = 0; j < length; j++)
                {
                    var p = rt.Frames[Math.Clamp(j - hitOut, 0, rt.Frames.Count - 1)];
                    lr.Add(ToAttacker(p, kdR));
                }
                reactLabel = $"game reaction {rid}_{rx}_{ry}";
            }
            c.ReactionLabels.Add(reactLabel);
            // a third wrestler only in double-team moves (their track 2 is anchored: op 84 05). Elsewhere track 2 is another
            // version of the attacker's own motion (People's Elbow 0_2: same start as 0_0, shorter, no events; Suplex 6,
            // the finisher variants...) - not a person to show
            var t2 = cat.Best(b.Attacker.Id, b.Attacker.X, 2) != null ? Source(b.Attacker.Id, b.Attacker.X, 2) : null;
            bool third = t2 != null && (t2.Anchor == 5 || cat.Moves.GetValueOrDefault(b.Attacker.Id)?.Category == "Double team");
            var lp = third ? Lane(t2!, true) : null;
            // root alignment: this block starts where / how the previous block ended, both performers moved together
            // (one turn about y + a shift on the ground, so their authored spacing stays). The turn comes from a 2D fit of
            // the bodies' joints seen from above (a facing measured on one bone flipped for bent / lying / flying poses):
            //   PlanarGroup     both wrestlers, each weighted by how well his own pose continues: the one who carries on
            //                   (a receiver lying on the mat into a dive) keeps his place, the other takes the difference
            //   FollowAttacker  the attacker's body only;  FollowReceiver  the receiver's body only
            //   Continue        a clip cut from the one before: the same turn / shift as that one (so the two halves join
            //                   seamlessly), not a new fit
            // a move's own approach (an attacker-only part - Clothesline 10's run-up, phase 0) followed by its hit (phase 10,
            // with the receiver): in the game both share one space - the approach ends where the hit starts and the hit is
            // placed by the receiver. So the approach is lined up by the HIT's receiver (his first pose fitted onto the
            // receiver as he is now), not by the attacker's body (a running pose fitted to the last clip's turned him away:
            // 2026-11-05 user report), and the hit then keeps the same placement.
            var nb = bi + 1 < blocks.Count ? blocks[bi + 1] : null;
            bool leadIn = r == null && nb?.Receiver != null && nb.Attacker.Id == b.Attacker.Id && nb.Attacker.X != b.Attacker.X;
            bool continues = afterLeadIn;
            afterLeadIn = leadIn;
            double yaw = 0; var shift = new V3(0, 0, 0);
            if (b.Align == RootAlign.Continue && lastPlace is var (ly, ls)) { yaw = ly; shift = ls; }
            else if (continues && b.Align != RootAlign.None && lastPlace is var (ly2, ls2)) { yaw = ly2; shift = ls2; }
            else if (leadIn && b.Align != RootAlign.None && c.Receiver.Count > 0 && c.Receiver[^1] is Pose prevR0)
            {
                var na = Source(nb!.Attacker.Id, nb.Attacker.X, nb.Attacker.Y);
                var nr = Source(nb.Receiver!.Id, nb.Receiver.X, nb.Receiver.Y);
                var rf = ToAttacker(Sample(nr.Frames, Math.Max(0, nb.In)), MotionMath.PlacementOf(na));
                (yaw, shift, _) = FitPlanar(skel.Fk(rf).Select(p => (p, 1.0)).ToList(), skel.Fk(prevR0).ToList());
            }
            else if (b.Align != RootAlign.None && b.Align != RootAlign.Continue && c.Attacker.Count > 0 && c.Attacker[^1] is Pose prev)
            {
                var prevR = c.Receiver.Count > 0 ? c.Receiver[^1] : null;
                List<(V3 P, double W)> Pts(Pose from, double w) => skel.Fk(from).Select(p => (p, w)).ToList();
                var pa = Pts(la[0], 1); var qa = skel.Fk(prev).ToList();
                bool hasR = lr != null && prevR != null;
                var pr = hasR ? Pts(lr![0], 1) : new(); var qr = hasR ? skel.Fk(prevR!).ToList() : new();
                double wa = 1, wr = hasR ? 1 : 0;
                if (b.Align == RootAlign.FollowAttacker) wr = 0;
                else if (b.Align == RootAlign.FollowReceiver && hasR) wa = 0;
                else if (hasR && Down(qr) && Down(pr.Select(x => x.P).ToList()))
                    wa = 0;   // the receiver lies on the mat in both: he stays exactly where he is, the attacker repositions
                else if (hasR)
                {
                    // how well each one's pose continues (rms after fitting him alone): weight 1 / (rms^2 + 1)
                    double ea = FitPlanar(pa, qa).Rms, er = FitPlanar(pr, qr).Rms;
                    wa = 1 / (ea * ea + 1); wr = 1 / (er * er + 1);
                }
                var P = pa.Select(x => (x.P, wa)).Concat(pr.Select(x => (x.P, wr))).ToList();
                var Q = qa.Concat(qr).ToList();
                (yaw, shift, _) = FitPlanar(P, Q);
            }
            if (Math.Abs(yaw) > 1e-12 || shift.Length > 1e-12)
            {
                var Rm = M3.Ry(yaw);
                List<Pose> Move(List<Pose> l) => l.Select(p =>
                {
                    var o = MotionMath.TurnRoot(p, yaw, skel.RootRest);
                    if (ReferenceEquals(o, p)) o = p.Clone();
                    var d = Rm * p.Root;
                    o.Root = new V3(d.X + shift.X, p.Root.Y, d.Z + shift.Z);
                    return o;
                }).ToList();
                la = Move(la);
                if (lr != null) lr = Move(lr);
                if (lp != null) lp = Move(lp);
            }
            // your own placement of each wrestler for this block, as a rigid move on the mat about the attacker's start
            // spot (the origin): turned by Turn, then shifted by (X, Z). Stored that way so one clip's placement can be
            // copied onto another exactly; turning "where he stands" (the handle, or typing a Turn) is converted to this
            // by the window (it updates X / Z too). A cut-off half has the same values, so the halves stay together.
            lastPlace = (yaw, shift);
            List<Pose> Adjust(List<Pose> l, double deg, double dx, double dz)
            {
                if (l.Count == 0 || (Math.Abs(deg) < 1e-9 && Math.Abs(dx) < 1e-9 && Math.Abs(dz) < 1e-9)) return l;
                double ay = deg * Math.PI / 180;
                var R = M3.Ry(ay);
                return l.Select(p =>
                {
                    var o = MotionMath.TurnRoot(p, ay, skel.RootRest);
                    if (ReferenceEquals(o, p)) o = p.Clone();
                    var d = R * new V3(p.Root.X, 0, p.Root.Z);
                    o.Root = new V3(d.X + dx, p.Root.Y, d.Z + dz);
                    return o;
                }).ToList();
            }
            la = Adjust(la, b.AttackerTurn, b.AttackerX, b.AttackerZ);
            if (lr != null) lr = Adjust(lr, b.ReceiverTurn, b.ReceiverX, b.ReceiverZ);
            if (lp != null) lp = Adjust(lp, b.ReceiverTurn, b.ReceiverX, b.ReceiverZ);
            c.Firsts.Add((la[0], lr?[0]));
            // the block's whole placement per wrestler (line-up then your move), for copy / paste: p -> Ry(turn) p + shift
            {
                double ra = b.AttackerTurn * Math.PI / 180, rRec = b.ReceiverTurn * Math.PI / 180;
                var sa = M3.Ry(ra) * shift + new V3(b.AttackerX, 0, b.AttackerZ);
                var sr = M3.Ry(rRec) * shift + new V3(b.ReceiverX, 0, b.ReceiverZ);
                c.Places.Add((yaw * 180 / Math.PI + b.AttackerTurn, sa, yaw * 180 / Math.PI + b.ReceiverTurn, sr));
            }
            // join: crossfade from the previous block's last pose
            if (b.Join == JoinMode.Crossfade && b.Blend > 0)
            {
                void Fade(List<Pose> l, List<Pose?> outLane)
                {
                    if (outLane.Count == 0 || outLane[^1] is not Pose pv) return;
                    for (int j = 0; j < Math.Min(b.Blend, l.Count); j++)
                    {
                        double u = (j + 1) / (double)(b.Blend + 1);
                        u = u * u * (3 - 2 * u);
                        l[j] = MotionMath.Lerp(pv, l[j], u);
                    }
                }
                Fade(la, c.Attacker);
                if (lr != null) Fade(lr, c.Receiver);
                if (lp != null) Fade(lp, c.Partner);
            }
            // join: smooth - the new clip moves from its first frame; the gap (and the old clip's speed) fades out
            else if (b.Join == JoinMode.Smooth && b.Blend > 0)
            {
                void Carry(List<Pose> l, List<Pose?> outLane)
                {
                    if (outLane.Count == 0 || outLane[^1] is not Pose pv) return;
                    MotionMath.SmoothInto(l, pv, outLane.Count > 1 ? outLane[^2] : null, b.Blend);
                }
                Carry(la, c.Attacker);
                if (lr != null) Carry(lr, c.Receiver);
                if (lp != null) Carry(lp, c.Partner);
            }
            int at = c.Attacker.Count;
            c.Attacker.AddRange(la);
            if (lr != null) c.Receiver.AddRange(lr);
            else
            {
                var last = c.Receiver.Count > 0 ? c.Receiver[^1] : null;
                for (int j = 0; j < length; j++) c.Receiver.Add(last);
            }
            if (lp != null) c.Partner.AddRange(lp);
            else { var lastP = c.Partner.Count > 0 ? c.Partner[^1] : null; for (int j = 0; j < length; j++) c.Partner.Add(lastP); }
            c.Spans.Add((at, length));
            c.Holds.Add((HoldFrom(a), r != null ? HoldFrom(r) : null));
        }
        if (first is (double, double) f0)
            for (int i = 0; i < c.Receiver.Count; i++)
                if (c.Receiver[i] is Pose p) c.Receiver[i] = ToOwn(p, f0);
        if (first is (double, double) f1)
            for (int i = 0; i < c.Partner.Count; i++)
                if (c.Partner[i] is Pose p) c.Partner[i] = ToOwn(p, f1);
        return c;
    }

    /// <summary>the turn about y + ground shift that best maps the points P onto Q seen from above (weighted 2D Kabsch):
    /// Ry(yaw) * p + shift ~ q. Ry maps (x, z) -> (x cos + z sin, -x sin + z cos).</summary>
    /// <summary>lying on the mat: the hips below 4 units (standing ~8.5; game y is down, the mat is y 0)</summary>
    bool Down(List<V3> joints) => -joints[skel.IndexOf("koshi")].Y < 4.0;

    public static (double Yaw, V3 Shift, double Rms) FitPlanar(List<(V3 P, double W)> P, List<V3> Q)
    {
        double sw = 0, px = 0, pz = 0, qx = 0, qz = 0;
        for (int i = 0; i < P.Count; i++) { var (p, w) = P[i]; sw += w; px += w * p.X; pz += w * p.Z; qx += w * Q[i].X; qz += w * Q[i].Z; }
        px /= sw; pz /= sw; qx /= sw; qz /= sw;
        double sc = 0, ss = 0;
        for (int i = 0; i < P.Count; i++)
        {
            var (p, w) = P[i];
            double ax = p.X - px, az = p.Z - pz, bx = Q[i].X - qx, bz = Q[i].Z - qz;
            sc += w * (bx * ax + bz * az); ss += w * (bx * az - bz * ax);
        }
        double yaw = Math.Atan2(ss, sc);
        var R = M3.Ry(yaw);
        var c = R * new V3(px, 0, pz);
        var shift = new V3(qx - c.X, 0, qz - c.Z);
        double e = 0;
        for (int i = 0; i < P.Count; i++)
        {
            var (p, w) = P[i];
            var m = R * new V3(p.X, 0, p.Z) + shift;
            e += w * ((m.X - Q[i].X) * (m.X - Q[i].X) + (m.Z - Q[i].Z) * (m.Z - Q[i].Z));
        }
        return (yaw, shift, Math.Sqrt(e / Math.Max(1e-9, sw)));
    }

    /// <summary>world joints per frame for the viewer (attacker space; the receiver placed as the move starts)</summary>
    /// <summary>the third wrestler's world joints (null frames where the move has none)</summary>
    public List<V3[]?> PartnerJoints(Composition c) =>
        c.Partner.Select(p => p is Pose q ? MotionMath.PlaceVictim(skel.Fk(q), c.Place) : null).ToList();

    public (List<V3[]?> Attacker, List<V3[]?> Receiver) Joints(Composition c)
    {
        var A = c.Attacker.Select(p => p is Pose q ? skel.Fk(q) : null).ToList();
        var R = c.Receiver.Select(p => p is Pose q ? MotionMath.PlaceVictim(skel.Fk(q), c.Place) : null).ToList();
        return (A, R);
    }
}
