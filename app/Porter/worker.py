"""AnimIT's offline porter protocol. All requests/responses are UTF-8 JSON.

Game files are read only. Output must be a fresh directory outside the game.
Recovered engines are kept under engine/ with their original conversion rules.
"""
import contextlib
import json
import os
from pathlib import Path
import re
import struct
import subprocess
import sys
import tempfile
import traceback

HERE = Path(__file__).resolve().parent
ENGINE = HERE / 'engine'
sys.path.insert(0, str(HERE))
sys.path[:0] = [str(ENGINE / x) for x in ('hctp', 'hctp/vendor', 'k26', 'aew', 'animre')]


def clean_name(value):
    value = str(value).strip()
    if not value or len(value) > 100 or any(ord(c) < 32 for c in value):
        raise ValueError('Use a move name of 1–100 characters, without line breaks.')
    return value


def aew_root(source):
    source = Path(source).resolve()
    if source.is_file():
        source = source.parent
    if source.name == 'Ingame':
        return source
    # Also accept one move's directory or the extracted ELITE/content root.
    if source.parent.name == 'Ingame':
        return source.parent
    roots = list(source.glob('**/Animations/Ingame'))
    if len(roots) != 1:
        raise ValueError('Choose the extracted AEW Animations/Ingame folder (or a folder containing it).')
    return roots[0]


def list_source(req):
    source = req['source']
    if not req.get('path', '').strip():
        raise ValueError('Choose a source game folder or animation file.')
    path = Path(req['path']).resolve()
    if source == 'hctp':
        import hctp
        h = hctp.HCTP(str(path))
        return {'moves': [dict(id=str(i), name=h.name(i) or 'HCTP move %d' % i, variant='000')
                          for i in h.ids if 1000 <= i < 16384 and any(k[2] == 0 for k in h.keys(i))]}
    if source == 'aew':
        import aew_archive
        if aew_archive.archives(path):
            return {'moves': aew_archive.list_moves(path)}
        root = aew_root(path)
        names = {}
        with (ENGINE / 'aew/wanted_rows.json').open(encoding='utf-8') as f:
            for name, rows in json.load(f).items():
                for row in rows:
                    names[row['Montage_ID']] = name.title()
        montages = {}
        for file in root.glob('*/A_*_A_*.uasset'):
            match = re.match(r'A_(\d{3}_\d{5}_\d{3})_A_(.+)\.uasset', file.name)
            if match:
                montages[match[1]] = match[2]
        if not montages:
            raise ValueError('No extracted AEW animation sequences found here.')
        import aew_kinds
        return {'moves': aew_kinds.describe(montages, names)}
    if source == 'k26':
        import k26
        files = k26_files(req)
        entries = {}
        for file in files:
            if file.suffix.lower() == '.clip':
                with file.open('rb') as f:
                    header = f.read(0xA0)
                if len(header) < 0xA0:
                    raise ValueError('Truncated .clip header.')
                hash_ = struct.unpack_from('<Q', header)[0]
                mid = struct.unpack_from('<I', header, 8)[0]
                match = re.match(r'(?:Clip #)?(\d+)', file.stem)
                mids = {mid} if mid < 65535 else {int(match[1])} if match else set()
                for m in mids:
                    for v in range(101):
                        if k26.fnv('%da%03d' % (m, v)) == hash_:
                            entries[(str(m), '%03d' % v)] = True
                continue
            hashes, mids = set(), set()
            with file.open('rb') as f:
                head = f.read(24)
                if len(head) != 24:
                    raise ValueError('Truncated .clips container: ' + str(file))
                n = struct.unpack_from('<I', head, 16)[0]
                if n > (file.stat().st_size - 24) // 16:
                    raise ValueError('Invalid .clips directory: ' + str(file))
                table = f.read(n * 16)
                for hash_, size, off in struct.iter_unpack('<QII', table):
                    if off < 24 + n * 16 or size < 0xA0 or off + size > file.stat().st_size:
                        raise ValueError('Invalid clip range in ' + str(file))
                    hashes.add(hash_)
                    f.seek(off + 8)
                    mid = struct.unpack('<I', f.read(4))[0]
                    if mid < 65535:
                        mids.add(mid)
            for mid in mids:
                for v in range(101):
                    variant = '%03d' % v
                    if k26.fnv('%da%s' % (mid, variant)) in hashes:
                        entries[(str(mid), variant)] = True
        return {'moves': [dict(id=m, name='2K26 move ' + m, variant=v) for m, v in sorted(entries)],
                'notes': req.get('_warnings', [])}
    raise ValueError('Unknown source game.')


def clip_files(path):
    if path.is_file():
        if path.suffix.lower() == '.clip':
            return sorted(path.parent.glob('*.clip'))
        if path.suffix.lower() == '.clips':
            return [path]
        raise ValueError('Choose extracted .clip / .clips files, not a .cak archive.')
    files = sorted(path.rglob('*.clips')) + sorted(path.rglob('*.clip'))
    if not files:
        raise ValueError('No extracted .clip or .clips files here. Extract the game archives first.')
    return files


def k26_files(req):
    path = Path(req['path']).resolve()
    files = []
    if path.is_file() and path.suffix.lower() != '.cak':
        return clip_files(path)
    if path.is_dir():
        files = sorted(path.rglob('*.clips')) + sorted(path.rglob('*.clip'))
    cakes = [path] if path.suffix.lower() == '.cak' else list(path.glob('**/*.cak'))
    if cakes:
        if path.is_dir() and not any(p.name.lower().startswith('bakedfile') for p in cakes):
            req.setdefault('_warnings', []).append('No retail bakedfile archives were found here. This list includes only loose animations and readable mod archives. For the full library, choose the folder with the bakedfile*.cak archives, or a folder of extracted .clips banks.')
        exe = req.get('decoder_executable')
        if not exe:
            raise ValueError('AnimIT executable is required to read CAK archives.')
        cache = Path(os.environ.get('LOCALAPPDATA', tempfile.gettempdir())) / 'AnimIT/PortedMoves/sources'
        with tempfile.TemporaryDirectory(prefix='animit-cak-') as temp:
            request, response = Path(temp) / 'request.json', Path(temp) / 'response.json'
            payload = dict(source=str(path), cache=str(cache))
            if req['command'] == 'port':
                payload['move'] = int(req['move'])
            request.write_text(json.dumps(payload), encoding='utf-8')
            child = subprocess.run([exe, '--extractclips', str(request), str(response)], capture_output=True,
                                   creationflags=getattr(subprocess, 'CREATE_NO_WINDOW', 0), timeout=1200)
            result = json.loads(response.read_text(encoding='utf-8')) if response.is_file() else {'ok': False, 'error': 'Archive reader did not return a result.'}
            if result['ok']:
                files.extend(Path(p) for p in result['files'])
                for warning in result.get('warnings', []):
                    req.setdefault('_warnings', []).append(warning)
                    print(warning, file=sys.stderr)
            elif not files:
                raise ValueError(result['error'])
            else:
                req.setdefault('_warnings', []).append(result['error'])
                print(result['error'], file=sys.stderr)
    if not files:
        raise ValueError('No .clips banks or readable CAK archives here. Choose the extracted Animation folder.')
    if req['command'] == 'port':
        mid = int(req['move'])
        files = [file for file in files if not (match := re.search(r'MotionArea_(\d+)_(\d+)', file.stem))
                 or int(match[1]) <= mid < int(match[2])]
    return files


def configure_k26(req, require_decoder=True):
    import k26
    if req.get('decoder_executable'):
        os.environ['ANIMIT_DECODER_EXE'] = req['decoder_executable']
    if require_decoder and not Path(req.get('decoder_executable', '')).is_file():
        raise ValueError('AnimIT native clip decoder was not provided.')
    files = k26_files(req)

    def find_clip(name):
        for file in files:
            if file.suffix.lower() == '.clip':
                data = file.read_bytes()
                if file.stem == name or (len(data) >= 8 and struct.unpack_from('<Q', data)[0] == k26.fnv(name)):
                    return data
        want = k26.fnv(name)
        for file in files:
            if file.suffix.lower() != '.clips':
                continue
            data = file.read_bytes()
            if len(data) < 24:
                raise ValueError('Truncated .clips file.')
            count = struct.unpack_from('<I', data, 16)[0]
            if count > (len(data) - 24) // 16:
                raise ValueError('Invalid .clips directory.')
            for i in range(count):
                hash_, size, off = struct.unpack_from('<QII', data, 24 + 16 * i)
                if hash_ == want:
                    if off < 24 + count * 16 or off + size > len(data):
                        raise ValueError('Clip points outside its container.')
                    return data[off:off + size]
        raise ValueError('Source clip %s was not found; include both attacker and receiver files.' % name)

    k26.find_clip = find_clip

    def pair_variants(mid):
        variants = []
        for v in range(101):
            try:
                find_clip('%da%03d' % (mid, v))
                find_clip('%db%03d' % (mid, v))
                variants.append('%03d' % v)
            except ValueError:
                pass
        return variants
    k26.pair_variants = pair_variants


def aew_tags(path):
    """{montage: attacker file tags} of an AEW source (archives or an extracted folder)"""
    import aew_archive
    if aew_archive.archives(path):
        return aew_archive.montage_tags(path)
    tags = {}
    for file in aew_root(path).glob('*/A_*_A_*.uasset'):
        m = re.match(r'A_(\d{3}_\d{5}_\d{3})_A_(.+)\.uasset', file.name)
        if m:
            tags[m[1]] = m[2]
    return tags


def resolve_dive(req):
    """an AEW dive piece (a jump or a landing) -> ('<AEW jump>/<landing group>', '+std=<v>+lie=<v,v>'): AEW's own jump
    + the landings, standing / lying face-up variants from AEW's own file tags (Coffin Drop 000 / 010,011; Buckshot
    Lariat 011 / none). The game still flies the wrestler from the donor's launch (moved onto AEW's take-off or homing
    marker). Not a dive -> (the move, '')."""
    m = req['move']
    if '/' in m or not re.fullmatch(r'\d{3}_\d{5}_\d{3}', m):
        return m, ''
    import aew_kinds
    tags = aew_tags(req['path'])
    tag = tags.get(m, '')
    if not (aew_kinds._is_jump(tag) or aew_kinds._is_landing(tag)):
        return m, ''
    group = aew_kinds.dive_landing_group(m, tags)
    if not group:
        raise ValueError('AEW has no landing animation for this dive. Choose another piece of the move.')
    jump = aew_kinds.dive_jump(m, group, tags)
    std, lie = aew_kinds.dive_variants(group, tags)
    return '%s/%s' % (jump or '-', group), '+std=%s+lie=%s' % (std or '', ','.join(lie))


def build(req):
    import game11
    import convert
    import ymks
    game = Path(req['game']).resolve()
    out = Path(req['out']).resolve()
    if out == game or out.is_relative_to(game):
        raise ValueError('Build the pack outside the game folder first.')
    if out.exists():
        raise ValueError('Choose a new output directory; existing packs are never overwritten.')
    name = clean_name(req['name'])
    donor = int(req.get('donor', 0))
    game11.DEFAULT_GAME = str(game)
    donor_frames = None
    if req['source'] == 'hctp':
        import porter
        mid = int(req['move'])
        p = porter.Porter(str(Path(req['path']).resolve()), str(game))
        prior_used = p.used_ids
        p.used_ids = lambda skip_pack=None: prior_used(skip_pack) | set(req.get('reserved_ids', []))
        if donor and donor not in p.donors():
            raise ValueError('The selected donor has no usable SvR 2011 motion and gameplay records.')
        report = p.port([mid], str(out), out.name, name, {mid: donor} if donor else None)
        if not report['ported']:
            raise ValueError('; '.join(str(r['why']) for r in report['skipped']))
        rows = report['ported']
    else:
        import retarget as rt
        import port_k26 as pk
        rt.GAME = str(game)
        if req['source'] == 'aew':
            import aew
            import aew_notifies
            import port_aew as pa
            import aew_archive
            req['move'], req['_dive_opts'] = resolve_dive(req)
            root = (aew_archive.extract_move(req['path'], req['move'], out.parent)
                    if aew_archive.archives(req['path']) else aew_root(req['path']))
            aew.ANIMS = aew_notifies.ANIMS = str(root)
        else:
            configure_k26(req)
        # Use the recovered entry points, with an explicit game, fresh output and a
        # single in-memory index. The original tools wrote PNG contact sheets;
        # AnimIT instead previews their actual output in its existing 3D viewport.
        g = game11.Game11(str(game))
        for reserved in req.get('reserved_ids', []):
            g.motions.setdefault((int(reserved), -1, -1), [])
        if donor not in g.waze or not g.usable(donor) or donor not in g.exh or not g.ymks(donor):
            raise ValueError('Choose a stock SvR 2011 donor with gameplay records and normal animation.')
        flags = g.waze[donor]['flags']
        # the move-set lists are flag bits: byte 0 (+ byte 3 04 front / 08 behind) the grapple lists, byte 8 0x10 /
        # 0x20 / 0x40 the springboard / apron dives, byte 11 0x80 the Irish-whip rebounds... A move in no list at all
        # is unused. A grapple with byte 0 = 00 is hidden from its list: made visible (0e) it works (AEWFF pack
        # Scorpion Death Drop 7433, Paradigm Shift 2 5260, seen in game); the other lists don't use byte 0.
        if not int(flags[:30], 16):
            raise ValueError('%s (%d) is in no move-set list - an unused move. Choose another donor.'
                             % (g.waze[donor]['name'], donor))
        if not int(flags[:2], 16) and flags[6:8] in ('04', '08'):
            req.setdefault('_warnings', []).append('donor is hidden in Create-a-Moveset; the ported move is made visible (flags byte 0 -> 0e)')
        if any(y in (14, 15, 17, 18) for x, y in g.ymks(donor)):
            raise ValueError('This donor animates the ropes; choose one without rope tracks.')
        game11.Game11 = lambda *args, **kwargs: g
        sizes = {}
        for entries in g.motions.values():
            for e in entries:
                bank = e['path']
                if bank.startswith('MOT/RR') and not bank.startswith('MOT/RRBS'):
                    sizes[bank] = sizes.get(bank, 0) + len(e['data']) + 16
        pk.RR_BANK = min(sizes, key=sizes.get)
        pk.render = lambda *args, **kwargs: None
        # Retain only valid free WAZE ids; each original CLI allocates from g.waze.
        used = {k[0] for k in g.motions}
        free = [i for i in g.waze if 2000 <= i < 16384 and i not in used]
        if not free:
            raise ValueError('No free SvR 2011 move records remain.')
        g.waze = {i: w for i, w in g.waze.items() if i < 16384}
        if req['source'] == 'aew':
            montage = req['move']
            if not re.fullmatch(r'(?:\d{3}_\d{5}_\d{3}|-)(?:/\d{3}_\d{5})?', montage):
                raise ValueError('Choose an AEW montage such as 040_00014_000.')
            if montage.startswith('053_') and '/' not in montage:
                montage = '-/' + montage[:9]  # AEW dive impacts; keep the donor's game-driven flight
            if '/' in montage and not any(x in (30, 40, 50) and y == 1 for x, y in g.ymks(donor)):
                raise ValueError('This AEW dive needs a diving donor (a SvR dive with landing phases). Use Best matches.')
            opts = ('+run' if req.get('running') else '') + req.get('_dive_opts', '')
            damage = req.get('damage')
            if damage is not None:
                if len(damage) != 4 or any(type(v) is not int or not 0 <= v <= 255 for v in damage):
                    raise ValueError('Damage values must be four bytes (head, body, arms, legs).')
                opts += '+dmg=' + '/'.join(map(str, damage))
            pa.port(['%s:%d%s:%s' % (montage, donor, opts, name), '--out', str(out)])
        else:
            mid, variant = int(req['move']), req.get('variant', '000')
            if not re.fullmatch(r'\d{3}', variant):
                raise ValueError('The clip variant must have three digits, such as 000.')
            pk.main(['%d:%s:%d:%s' % (mid, variant, donor, name), '--out', str(out)])
        with (out / 'report.json').open(encoding='utf-8') as f:
            rows = json.load(f)
        donor_frames = {int(r['donor']): g.ymks(int(r['donor']))[(0, 0)]['frames'] for r in rows
                        if (0, 0) in g.ymks(int(r['donor']))}
        T11 = next(e['T'] for e in g.motions[(718, 0, 0)] if e['kind'] == b'YMKs')
    # Give every source the chosen name and metadata; check all streams before
    # returning success. C# performs a separate structural/pose validation.
    pack = out / 'pack.txt'
    if rows and req.get('_warnings'):
        rows[0].setdefault('notes', []).extend(req['_warnings'])
    lines = pack.read_text(encoding='utf-8').splitlines()
    ids = [int(row['id']) for row in rows]
    for i, line in enumerate(lines):
        p = line.split()
        if p and p[0] == 'wazename':
            lines[i] = 'wazename %s %s [%s]' % (p[1], name, req['source'].upper())
        if p and p[0] == 'motion':
            file = out / ' '.join(p[7:])
            stream = ymks.parse_stream(file.read_bytes())
            if stream.nframes() != int(p[6]) or int(p[6]) < 1:
                raise ValueError('Motion length mismatch: ' + str(file))
    if donor_frames:
        lines = retime_sounds(lines, rows, donor_frames)
    if req['source'] == 'aew' and '/' not in req['move'] and not req['move'].startswith('053_') and rows:
        import event_map
        lines, map_notes = event_map.map_events(out, lines, int(rows[0]['id']), req['move'], T11)
        rows[0].setdefault('notes', []).extend(map_notes)
    lines = add_special_bank(lines)
    pack.write_text('\n'.join(lines) + '\n', encoding='utf-8')
    (out / 'manifest.txt').write_text('type=moves\nid=%s\nname=%s\nauthor=AnimIT\nversion=1.0\n' % (out.name, name), encoding='utf-8')
    return dict(directory=str(out), ids=ids, reports=rows)


def map_events_again(req):
    """(the event editor's button) match a built AEW pack's events to AEW's markers again"""
    import game11
    import aew
    import aew_archive
    import aew_notifies
    import event_map
    out = Path(req['pack']).resolve()
    game = str(Path(req['game']).resolve())
    if out.is_relative_to(Path(game)):
        raise ValueError('Only a built pack outside the game can be changed here.')
    montage = req['move']
    cache = Path(os.environ.get('LOCALAPPDATA', tempfile.gettempdir())) / 'AnimIT/PortedMoves'
    root = (aew_archive.extract_move(req['path'], montage, cache)
            if aew_archive.archives(req['path']) else aew_root(req['path']))
    aew.ANIMS = aew_notifies.ANIMS = str(root)
    g = game11.Game11(game)
    T11 = next(e['T'] for e in g.motions[(718, 0, 0)] if e['kind'] == b'YMKs')
    pack = out / 'pack.txt'
    lines, notes = event_map.map_events(out, pack.read_text(encoding='utf-8').splitlines(), int(req['id']), montage, T11)
    pack.write_text('\n'.join(lines) + '\n', encoding='utf-8')
    return dict(notes=notes)


def retime_sounds(lines, rows, donor_frames):
    """The donor's sounds (evt lines: 4-byte {u16 sound, u16 frame}) moved like its motion events: around the
    porter's landmark ('victim down: frame A here, D in the donor'), so a slam's thud plays on the new impact."""
    import port_k26 as pk
    marks = {}
    for r in rows:
        for n in r.get('notes', []):
            m = re.search(r'(?:victim down|landing|first contact): frame (\d+) here, (\d+) in the donor', n)
            if m:
                marks[int(r['id'])] = (int(m[1]), int(m[2]), int(r['donor']))
                break
            if 'events retimed evenly' in n:
                marks[int(r['id'])] = (0, 0, int(r['donor']))
                break
    nf = {}
    for l in lines:
        f = l.split()
        if f and f[0] == 'motion' and len(f) > 7 and f[4] == '0' and f[5] == '0':
            nf[int(f[3])] = int(f[6])
    out = []
    for l in lines:
        f = l.split()
        if f and f[0] == 'evt' and len(f) == 4:
            rec, ev = bytes.fromhex(f[2]), bytes.fromhex(f[3])
            mid = struct.unpack_from('<H', rec, 0)[0]
            if mid in marks and rec[2] == 0 and mid in nf and marks[mid][2] in donor_frames:
                mark, dmark, donor = marks[mid]
                dn, n = donor_frames[donor], nf[mid]
                items = []
                for k in range(0, len(ev) - 3, 4):
                    snd, fr = struct.unpack_from('<HH', ev, k)
                    to = n - 1 if fr >= dn - 1 else max(0, min(n - 2, pk.warp(fr, dmark, mark, dn, n)))
                    items.append((to, snd))
                ev = b''.join(struct.pack('<HH', snd, to) for to, snd in sorted(items))
                l = 'evt %s %s %s' % (f[1], f[2], ev.hex())
        out.append(l)
    return out


def add_special_bank(lines):
    """The move-set editor previews play from MVMT/SPE1/15 (front, flags byte 3 = 04) or
    MVMT/SPE2/16 (from behind, 08). A donor without a special copy (e.g. 5260, 7433) leaves the
    ported move standing still there, so register each MVMT/STN motion in its special bank too."""
    spe, have = {}, set()
    for l in lines:
        f = l.split()
        if f and f[0] == 'waze' and len(f) == 3 and f[2][6:8] in ('04', '08'):
            spe[int(f[1])] = 'MVMT/SPE1/15' if f[2][6:8] == '04' else 'MVMT/SPE2/16'
        elif f and f[0] == 'motion' and len(f) > 5 and f[2].startswith('MVMT/SPE'):
            have.add((int(f[3]), f[4], f[5]))
    out = []
    for l in lines:
        f = l.split()
        if f and f[0] == 'waze' and len(f) == 3 and f[2][:2] == '00' and f[2][6:8] in ('04', '08'):
            l = 'waze %s 0e%s' % (f[1], f[2][2:])      # (a hidden grapple; the other lists don't use byte 0)
        out.append(l)
        if f and f[0] == 'motion' and len(f) > 7 and f[1] == 'm.pac' and f[2].startswith('MVMT/STN'):
            key = (int(f[3]), f[4], f[5])
            if int(f[3]) in spe and key not in have:
                out.append('motion m.pac %s %s' % (spe[int(f[3])], ' '.join(f[3:])))
                have.add(key)
    return out


FINGERS = {0: 'Thumb', 1: 'Index', 2: 'Middle', 3: 'Ring', 4: 'Pinky'}


def source_clips(req):
    """-> (rig, attacker frames, receiver frames or None, end 60 fps frame, hit 30 fps frame or None): the source move's
    world joints (porter axes, cm, 60 fps); a 2K26 receiver is moved into the attacker's space (J_Opponent)"""
    source = req['source']
    hit, end = None, None
    if source == 'aew':
        import aew
        import aew_archive
        import aew_notifies
        montage = req['move']
        if not re.fullmatch(r'\d{3}_\d{5}_\d{3}', montage):
            raise ValueError('Choose an AEW montage such as 040_00014_000.')
        cache = Path(os.environ.get('LOCALAPPDATA', tempfile.gettempdir())) / 'AnimIT/PortedMoves'
        root = (aew_archive.extract_move(req['path'], montage, cache)
                if aew_archive.archives(req['path']) else aew_root(req['path']))
        aew.ANIMS = aew_notifies.ANIMS = str(root)
        rig = aew.RigAEW()
        try:
            A = aew.clip_world(rig, aew.find(montage, 'A'))[1]
        except KeyError:
            raise ValueError('No attacker animation for ' + montage)
        try:
            B = aew.clip_world(rig, aew.find(montage, 'B'))[1]
        except KeyError:
            B = None
        if req.get('measure') and B is not None:
            import port_aew as pa
            mid, var = pa.mid_of(montage)
            end = pa.move_end(rig, mid, var)[0]
            try:
                da = [t for t, n, _ in aew_notifies.notifies(montage, 'B') if n == 'BP_DA_C']
                hit = round(da[-1] * 30) if da else None
            except Exception:   # noqa: BLE001  (no montage notifies: no hit time)
                hit = None
    elif source == 'k26':
        import k26
        configure_k26(req)
        rig = k26.Rig26()
        mid, var = int(req['move']), req.get('variant', '000')
        try:
            A = k26.clip_world(rig, k26.find_clip('%da%s' % (mid, var)))[1]
        except (ValueError, KeyError):
            raise ValueError('No attacker clip for move %d variant %s' % (mid, var))
        try:
            B = k26.clip_world(rig, k26.find_clip('%db%s' % (mid, var)))[1]
        except (ValueError, KeyError):
            B = None
        if B is not None and 'J_Opponent' in A[0] and 'J_Root' in B[0]:
            sh = A[0]['J_Opponent'][0] - B[0]['J_Root'][0]
            B = [{j: (p + sh, r) for j, (p, r) in w.items()} for w in B]
    else:
        raise ValueError('HCTP moves are measured after Build (they are already in 2011 form).')
    if end is None:
        end = min(len(A), len(B)) if B is not None else len(A)
    return rig, A, B, end, hit


def donor_ranking(req):
    """the best SvR 2011 donors for a source move, with the checks (donor_finder.py)"""
    import retarget as rt
    import game11
    import donor_finder as dfn
    game = str(Path(req['game']).resolve())
    rt.GAME = game11.DEFAULT_GAME = game
    req = dict(req, measure=True)
    if req['source'] == 'aew' and '/' in resolve_dive(req)[0]:
        # a dive: rank SvR dives by their landings (the donor's jump is kept)
        import aew
        import aew_archive
        import donor_dives
        group = resolve_dive(req)[0].split('/')[1]
        cache = Path(os.environ.get('LOCALAPPDATA', tempfile.gettempdir())) / 'AnimIT/PortedMoves'
        root = (aew_archive.extract_move(req['path'], group, cache)
                if aew_archive.archives(req['path']) else aew_root(req['path']))
        aew.ANIMS = str(root)

        def has(v):
            try:
                aew.find('%s_%s' % (group, v), 'A')
                aew.find('%s_%s' % (group, v), 'B')
                return True
            except KeyError:
                return False
        import aew_kinds
        montage, dopts = resolve_dive(req)
        std = next((o[4:] for o in dopts.split('+') if o.startswith('std=')), '000')
        lie = next((o[4:] for o in dopts.split('+') if o.startswith('lie=')), '010,011')
        jtag = aew_tags(req['path']).get(montage.split('/')[0], '').split('_')
        # (AEW's apron jumps = SvR's apron dives - the Apron Springboard list, flags byte 9, MVMT/APRO banks - into the
        # ring or out to the floor; springboards inside the ring = its springboard dives, flags byte 8, MVMT/ROPE)
        family = ('top rope' if 'CorTopToRing' in jtag or 'CorTopToRSide' in jtag
                  else 'apron' if any(t.startswith('Apr') for t in jtag)
                  else 'springboard' if any(t.startswith('Rope') for t in jtag) else None)
        # (into the ring or out to ringside: AEW's jump says it - AprToRing / CorTopToRing... vs AprToRSide / ...ToRSide)
        direction = ('ringside' if any(t.endswith('ToRSide') for t in jtag)
                     else 'ring' if any(t.endswith('ToRing') for t in jtag) else None)
        src = donor_dives.source(aew.RigAEW(), rt.Rig11(), group, has, std, [v for v in lie.split(',') if v], family,
                                 direction)
        pool = donor_dives.pool(game11.Game11(game), game, cache, rt.Rig11())
        return dict(source=donor_dives.summary(src), donors=donor_dives.rank(src, pool, int(req.get('limit', 60))))
    rig, A, B, end, hit = source_clips(req)
    r11 = rt.Rig11()
    src = dfn.source_feats(rig, r11, A, B, end, hit)
    g = game11.Game11(game)
    cache = Path(os.environ.get('LOCALAPPDATA', tempfile.gettempdir())) / 'AnimIT/PortedMoves'
    pool = dfn.pool(g, r11, game, cache)
    return dict(source=dfn.summary(src), donors=dfn.rank(src, pool, int(req.get('limit', 60))))


def sample(req):
    """The source move as it is (no donor, no build): both actors' joints on SvR 2011's skeleton names, scaled to
    2011 size, 30 fps - for AnimIT's 3D stage. AEW and 2K26 share the porter's joint names and axes."""
    import numpy as np
    import retarget as rt
    rt.GAME = str(Path(req['game']).resolve())
    rig, A, B, _, _ = source_clips(req)
    clips = [A] + ([B] if B is not None else [])
    r11 = rt.Rig11()
    s = (np.linalg.norm(r11.rest_pos['l_ashi'] - r11.rest_pos['l_momo'])
         / np.linalg.norm(rig.rest_world['J_Foot_L'][0] - rig.rest_world['J_Leg_L'][0]))
    names = dict(rt.MAP)
    names['root'] = 'J_Hips'
    for side in 'lr':
        for f, part in FINGERS.items():
            for k in range(3):
                names['%s_yubi%d%d' % (side, f, k)] = 'J_%sF%d_%s' % (part, k + 1, side.upper())
    first = clips[0][0]
    names = {n: j for n, j in names.items() if j in first}
    out = []
    for W in clips:
        frames = W[::2]          # 60 -> 30 fps
        out.append([[round(float(v), 3) for n, j in names.items() for v in f[j][0] * s] for f in frames])
    return dict(joints=list(names), attacker=out[0], receiver=out[1] if len(out) > 1 else [])


def main():
    request_file, response_file = map(Path, sys.argv[1:3])
    try:
        with request_file.open(encoding='utf-8-sig') as f:
            req = json.load(f)
        with contextlib.redirect_stdout(sys.stderr):
            result = (list_source(req) if req['command'] == 'list' else build(req) if req['command'] == 'port'
                      else sample(req) if req['command'] == 'sample'
                      else donor_ranking(req) if req['command'] == 'donors'
                      else map_events_again(req) if req['command'] == 'mapevents' else None)
            if result is None:
                raise ValueError('Unknown porter command.')
        response = {'ok': True, **result}
    except Exception as error:
        traceback.print_exc(file=sys.stderr)
        response = {'ok': False, 'error': str(error)}
    response_file.write_text(json.dumps(response, ensure_ascii=False), encoding='utf-8')
    return 0 if response['ok'] else 1


if __name__ == '__main__':
    sys.exit(main())
