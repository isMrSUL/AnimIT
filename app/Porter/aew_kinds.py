"""AEW move types and readable names from the animation file names (A_<montage>_A_<gender>_<tags>_<Name>_<nnn>).

The tags are AEW's own: Mov (a move), Gp (grapple) + F / B / R (front, behind, rear-reverse), At (strike), Dsh (running),
Dwn (opponent down), Cor (corner), DS (dive), Rope / Apr (ropes, apron), Sitting, Cou (counter), Cg (lock-up),
Tau (taunt), Pin, Dmg (hit reactions), Wp (weapons). The first match below wins."""
import re

KINDS = ['Front grapples', 'Rear grapples', 'Strikes', 'Running', 'Ground', 'Corner', 'Ropes & apron', 'Dives',
         'Seated', 'Counters', 'Lock-ups', 'Weapons', 'Pins', 'Taunts', 'Reactions', 'Other']

CODES = {'M', 'F', 'O', 'Mov', 'Gp', 'B', 'R', 'L', 'At', 'Dsh', 'Cor', 'Dwn', 'DS', 'Cou', 'DCou', 'Sitting', 'Rope',
         'MidRope', 'Apr', 'CorTop', 'Con', 'CgAt', 'Cg', 'Tau', 'Pin', 'Dmg', 'Sys', 'Nav', 'ST', 'EN', 'LO', 'HSF',
         'HSR', 'HSL', 'Str', 'Left', 'Right', 'Rear', 'Front', 'FU', 'FD', 'DwnFU', 'DwnFD', 'Baf', 'SYS'}


def kind(tag):
    toks = tag.split('_')
    if toks and toks[0] in ('M', 'F', 'O'):
        toks = toks[1:]
    if not toks:
        return 'Other'
    s, first = set(toks), toks[0]
    if first in ('Tau', 'Gesture', 'MatchUpPose'):
        return 'Taunts'
    if first in ('Dmg', 'Baf'):
        return 'Reactions'
    if first == 'Pin':
        return 'Pins'
    if first == 'Cg':
        return 'Lock-ups'
    if first != 'Mov':
        return 'Other'
    if any(t.startswith(('Wp', 'DWp')) for t in toks):
        return 'Weapons'
    if 'DS' in s or 'Diving' in s or any(t.endswith('ToRSide') for t in toks):
        return 'Dives'
    if s & {'Cor', 'CorTop'} or any(t.startswith('Cor') for t in toks):
        return 'Corner'
    if s & {'Rope', 'MidRope', 'Apr'} or any(re.match(r'(Apr|Ring|Rope)To', t) for t in toks):
        return 'Ropes & apron'
    if 'Sitting' in s:
        return 'Seated'
    if 'CgAt' in s:
        return 'Lock-ups'
    if s & {'Cou', 'DCou'}:
        return 'Counters'
    if 'Dsh' in s:
        return 'Running'
    if 'Dwn' in s or any(t.startswith('DwnF') for t in toks):
        return 'Ground'
    if 'Gp' in toks:
        nxt = toks[toks.index('Gp') + 1] if toks.index('Gp') + 1 < len(toks) else ''
        return 'Rear grapples' if nxt in ('B', 'R') else 'Front grapples'
    if 'At' in s:
        return 'Strikes'
    return 'Other'


def nice_name(tag):
    """'M_Mov_Gp_F_DeadEye_001' -> 'Dead Eye' (empty when the tag is only codes)"""
    words = [t for t in tag.split('_') if t not in CODES and not re.fullmatch(r'[A-Z]{0,3}\d*', t)
             and not re.fullmatch(r'(LO|EN|ST)\d*', t) and not re.fullmatch(r'(Cor|CorTop|Apr|Ring|Rope|MidRope)To\w+', t)]
    return ' '.join(re.sub(r'(?<=[a-z])(?=[A-Z])|(?<=[A-Z])(?=[A-Z][a-z])', ' ', w) for w in words).strip()


PLACES = [('CorTopToRSide', 'top rope to the floor'), ('CorTopToRing', 'top rope'), ('CorTopToApr', 'top rope to the apron'),
          ('CorToRSide', 'middle rope to the floor'), ('CorToRing', 'middle rope'), ('CorToApr', 'middle rope to the apron'),
          ('RopeToRSide', 'springboard to the floor'), ('RopeToRing', 'springboard'), ('AprToRSide', 'apron to the floor'),
          ('AprToRing', 'apron to the ring')]
LANDINGS = [('DwnFU', 'onto a lying opponent (face up)'), ('DwnFD', 'onto a lying opponent (face down)')]


def dive_part(tag):
    """a dive piece's place in the dive: AEW splits one dive into a jump (052: attacker only, from a place) and
    landings (053: attacker + opponent, by the opponent's state) - the list shows which piece it is"""
    toks = tag.split('_')
    if 'DS' not in toks:
        return ''
    place = next((label for code, label in PLACES if code in toks), '')
    if '_At_' in '_' + '_'.join(toks) + '_' or any(t in toks for t in ('HSF', 'HSB', 'HSR', 'HSL')):
        state = next((label for code, label in LANDINGS if code in toks), 'onto a standing opponent')
        pin = ', pin' if 'PinCombo' in toks else ''
        return 'landing %s%s' % (state, pin)
    return 'jump from the %s' % place if place else 'jump'


def describe(montage_tags, names):
    """{montage: attacker tag}, {montage: DataTable name} -> the porter's source move list"""
    out = []
    for m in sorted(montage_tags):
        tag = montage_tags[m]
        title = names.get(m)
        name = title or nice_name(tag) or 'AEW ' + m
        part = dive_part(tag) if m[:3] in ('052', '053', '054', '055') else ''
        if part:
            name += ' (%s)' % part
        out.append(dict(id=m, name=name, variant='000', kind=kind(tag), named=bool(title)))
    return out


def _is_landing(tag):
    t = tag.split('_')
    return 'DS' in t and any(x in t for x in ('HSF', 'HSB', 'HSR', 'HSL'))


def _is_jump(tag):
    t = tag.split('_')
    return 'DS' in t and not _is_landing(tag) and ('ST' in t or any(re.match(r'(Cor|CorTop|Apr|Rope)To', x) for x in t))


def _same_move(a, b):
    """AEW names one move's pieces alike - with typos (Buckshot / Backshot Lariat)"""
    import difflib
    return bool(a and b) and (a == b or difflib.SequenceMatcher(None, a.lower(), b.lower()).ratio() >= 0.85)


def dive_jump(montage, group, montage_tags):
    """the AEW jump piece to port with a dive's landings: the picked jump itself, else the same move's jump - the top
    rope into the ring first (most SvR dives), then from the apron, then a springboard"""
    if _is_jump(montage_tags.get(montage, '')):
        return montage
    name = _move_word(montage_tags.get(montage, '')) or _move_word(
        next((t for m, t in montage_tags.items() if m.startswith(group)), ''))
    jumps = [m for m, t in montage_tags.items() if _is_jump(t) and _same_move(_move_word(t), name)]
    order = ('CorTopToRing', 'AprToRing', 'RopeToRing', 'CorToRing')

    def rank(m):
        t = montage_tags[m].split('_')
        return (next((k for k, place in enumerate(order) if place in t), len(order)), m)
    return min(jumps, key=rank) if jumps else None


def dive_variants(group, montage_tags):
    """-> (standing landing variant or None, [lying face-up variants]) of an AEW landing group"""
    std, lie = [], []
    for m, t in sorted(montage_tags.items()):
        if not m.startswith(group) or not _is_landing(t) or 'PinCombo' in t.split('_'):
            continue
        toks = t.split('_')
        if 'DwnFU' in toks:
            lie.append(m[10:])
        elif 'DwnFD' not in toks:
            std.append((0 if 'HSF' in toks else 1, m[10:]))
    return (min(std)[1] if std else None), lie[:2]


def _move_word(tag):
    words = [t for t in tag.split('_') if re.fullmatch(r'[A-Z][a-z]+[A-Za-z]*', t) and t not in CODES
             and not re.fullmatch(r'(Cor|CorTop|Apr|Ring|Rope|MidRope)To\w+', t) and t not in ('Diving', 'PinCombo')]
    return words[-1] if words else None


def dive_landing_group(montage, montage_tags):
    """the landing group (<kind>_<group>) of an AEW dive piece: its own when it is a landing, else the group of the
    same move's landings (by name); None when AEW has none"""
    tag = montage_tags.get(montage, '')
    if _is_landing(tag):
        return montage[:9]
    name = _move_word(tag)
    if not name:
        return None
    hits = sorted({m[:9] for m, t in montage_tags.items() if _is_landing(t) and 'PinCombo' not in t.split('_')
                   and _same_move(_move_word(t), name)})
    return hits[0] if hits else None
