"""AEW montage notifies: name, time (s), duration - read only.  python -I aew_notifies.py <montage like 040_00695_000> [A|B]"""
import glob
import os
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import zenpkg  # noqa: E402

ANIMS = os.path.join(os.path.dirname(os.path.abspath(__file__)), 'extract', 'ELITE', 'Content', 'GameData', 'Resources',
                     'Animations', 'Ingame')


def notifies(montage, side='A'):
    """[(time s, name, duration s)] of the montage AM_<montage>_<side>_*, sorted by time"""
    hits = sorted(glob.glob(os.path.join(ANIMS, montage[:9], 'AM_%s_%s_*.uasset' % (montage, side))))
    if not hits:
        return []
    p = zenpkg.Pkg(open(hits[0], 'rb').read())
    out = []
    for e, s, t in p.export_data():
        if not e['name'].startswith('AM_'):
            continue
        for n in zenpkg.Reader(p, s, t).props().get('Notifies', []):
            out.append((float(n.get('LinkValue', 0.0)), n.get('NotifyName', '?'), float(n.get('Duration', 0.0))))
    return sorted(out)


if __name__ == '__main__':
    sys.stdout.reconfigure(encoding='utf-8')
    for t, name, d in notifies(sys.argv[1], sys.argv[2] if len(sys.argv) > 2 else 'A'):
        print('%6.3f s (60 fps frame %4d) %-40s%s' % (t, round(t * 60), name, '  for %.2f s' % d if d else ''))
