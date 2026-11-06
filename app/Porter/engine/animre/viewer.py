"""Skeleton loading extracted from ANIMRE/tools/viewer.py; no viewer or game launch."""
from src.containers import parse as parse_archive
from src.skeleton import parse as parse_skeleton


def model_from(path):
    found = []

    def visit(data):
        if data[:4] in (b'YOBJ', b'JBOY'):
            model = parse_skeleton(data)
            if len(model.bones) >= 50 and 'root' in model.name_index():
                found.append(model)
        elif data[:4] in (b'DPAC', b'PAC ', b'EPAC', b'EPK8', b'PACH', b'BPE '):
            for entry in parse_archive(data).entries:
                visit(entry.data)

    visit(path.read_bytes())
    if not found:
        raise ValueError('No character skeleton in %s' % path)
    return max(found, key=lambda model: len(model.bones))
