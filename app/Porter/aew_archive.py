"""Read AEW's installed IoStore archives; extract only a selected move to cache."""
import hashlib
import os
from pathlib import Path, PurePosixPath
import re
import struct


def archives(source):
    path = Path(source).resolve()
    files = [path] if path.suffix.lower() == '.utoc' else sorted(path.glob('**/*.utoc')) if path.is_dir() else []
    return [file for file in files if file.with_suffix('.ucas').is_file()]


def open_containers(source):
    from ioextract import Container
    out = []
    try:
        for file in archives(source):
            container = Container(str(file))
            if container.toc.flags & 2:
                container.ucas.close()
                raise ValueError('Encrypted AEW IoStore containers are not supported.')
            if container.toc.partition_count > 1:
                container.ucas.close()
                raise ValueError('Split AEW IoStore containers are not supported yet.')
            out.append(container)
        return out
    except Exception:
        for c in out:
            c.ucas.close()
        raise


def configure_oodle(source):
    import ioextract
    root = Path(source)
    if root.is_file():
        root = root.parent
    candidates = [root / 'oo2core_9_win64.dll', Path(__file__).resolve().parent / 'cake/oo2core_9_win64.dll']
    if ioextract.OODLE:
        candidates.append(Path(ioextract.OODLE))
    if not any(file.is_file() for file in candidates):
        candidates += list(root.glob('**/oo2core*_win64.dll'))
    ioextract.OODLE = next((str(file) for file in candidates if file.is_file()), None)
    if ioextract.OODLE is None:
        raise ValueError('Oodle DLL was not found in the source game. Extracted animation folders can also be used.')


def montage_tags(source):
    """{montage: its attacker file's tags} from the archive names only (fast: no DataTables)"""
    containers = open_containers(source)
    tags = {}
    try:
        for c in containers:
            for name in c.names.values():
                match = re.search(r'/Animations/Ingame/[^/]+/A_(\d{3}_\d{5}_\d{3})_A_([^/]*)', name, re.I)
                if match and (match[1] not in tags or '/SSMode/' not in name):
                    tags[match[1]] = match[2].split('.')[0]
    finally:
        for c in containers:
            c.ucas.close()
    return tags


def list_moves(source):
    containers = open_containers(source)
    montages, names = {}, {}         # montage -> its attacker file's tags (the game's own copy over SSMode's)
    try:
        for c in containers:
            for idx, name in c.names.items():
                match = re.search(r'/Animations/Ingame/[^/]+/A_(\d{3}_\d{5}_\d{3})_A_([^/]*)', name, re.I)
                if match and (match[1] not in montages or '/SSMode/' not in name):
                    montages[match[1]] = match[2].split('.')[0]
        # Resolve actual move names from the game's DataTables where available.
        configure_oodle(source)
        import zenpkg
        for c in containers:
            for idx, name in c.names.items():
                if '/DataTable/' not in name or not name.endswith('.uasset') or '/MiniGame/' in name:
                    continue
                try:
                    for row in zenpkg.table(zenpkg.Pkg(c.read(idx))).values():
                        montage, title = row.get('Montage_ID'), row.get('MovesName')
                        if isinstance(montage, str) and isinstance(title, str) and title:
                            names[montage] = title.title()
                except (ValueError, IndexError, KeyError, UnicodeError, AssertionError, struct.error):
                    # Some unrelated tables use custom serialization. Their
                    # absence only leaves a montage id as its display name.
                    continue
        import aew_kinds
        return aew_kinds.describe(montages, names)
    finally:
        for c in containers:
            c.ucas.close()


def extract_move(source, montage, cache_root):
    """Extract one move's animation files into a single flat cache root: Animations/Ingame/<group>/<file>.

    AEW keeps animations in two trees (the game's and SSMode's, the story mode); a file's group is taken from its
    own name (A_/AM_<group>_...), because some sit in a folder of another group. Where both trees have a file
    of the same name, the game's own copy is used."""
    configure_oodle(source)
    files = archives(source)
    stamp = '|'.join('%s:%d:%d' % (p, p.stat().st_size, p.stat().st_mtime_ns) for p in files)
    key = hashlib.sha256(stamp.encode()).hexdigest()[:20]
    root = Path(cache_root) / 'sources' / ('aew2_' + key) / 'Animations' / 'Ingame'
    groups = set(re.findall(r'\d{3}_\d{5}', montage))
    if not groups:
        raise ValueError('Choose an AEW montage before extracting.')
    want = set(re.findall(r'\d{3}_\d{5}_\d{3}', montage))
    # already extracted (a preview, an earlier build): skip opening the archives
    if (all(any((root / m[:9]).glob('A_%s_*.uasset' % m)) for m in want) if want else all(any((root / g).glob('A_*.uasset')) for g in groups)):
        return root
    containers = open_containers(source)
    picked = {}                      # dest -> (from SSMode, container, index)
    try:
        for c in containers:
            for idx, name in c.names.items():
                if '/Animations/Ingame/' not in name or not name.endswith('.uasset'):
                    continue
                parts = PurePosixPath(name).parts
                fname, folder = parts[-1], parts[-2]
                if not re.fullmatch(r'[\w.-]+', fname):
                    continue
                m = re.match(r'AM?_(\d{3}_\d{5})_', fname)
                group = m[1] if m else folder
                if group not in groups:
                    continue
                dest = root / group / fname
                ss = '/SSMode/' in name
                if dest not in picked or (picked[dest][0] and not ss):
                    picked[dest] = (ss, c, idx)
        for dest, (_, c, idx) in picked.items():
            expected = c.toc.offlen[idx][1]
            if not dest.is_file() or dest.stat().st_size != expected:
                data = c.read(idx)
                if len(data) != expected:
                    raise ValueError('Incomplete AEW animation extraction: ' + dest.name)
                dest.parent.mkdir(parents=True, exist_ok=True)
                temp = dest.with_suffix('.partial')
                temp.write_bytes(data)
                os.replace(temp, dest)
    finally:
        for c in containers:
            c.ucas.close()
    if not (any((root / m[:9]).glob('A_%s_*.uasset' % m) for m in want) if want else any(any((root / g).glob('A_*.uasset')) for g in groups)):
        raise ValueError('The animation %s was not found in the AEW archives.' % montage)
    return root
