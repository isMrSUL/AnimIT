"""UE 4.26/4.27 IoStore (zen) package reader: name map, export map, tagged properties, DataTable rows - read only.

    python -I zenpkg.py names <uasset>
    python -I zenpkg.py exports <uasset>
    python -I zenpkg.py table <uasset> [row filter]     -> DataTable rows as JSON-ish text
"""
import json
import struct
import sys


class Pkg:
    def __init__(self, data):
        self.d = d = data
        (self.name_i, self.name_n, self.src_i, self.src_n, self.flags, self.header_size, self.names_off, self.names_size,
         self.hashes_off, self.hashes_size, self.imports_off, self.exports_off, self.bundles_off, self.graph_off,
         self.graph_size) = struct.unpack_from('<4I2I9i', d, 0)
        self.names = []
        o, end = self.names_off, self.names_off + self.names_size
        while o < end:
            h0, h1 = d[o], d[o + 1]
            wide = h0 & 0x80
            n = ((h0 & 0x7F) << 8) | h1
            o += 2
            if wide:
                if (o - self.names_off) & 1:   # (utf-16 names are 2-byte aligned)
                    o += 1
                self.names.append(d[o:o + 2 * n].decode('utf-16-le'))
                o += 2 * n
            else:
                self.names.append(d[o:o + n].decode('latin1'))
                o += n
        self.exports = []
        o = self.exports_off
        while o + 72 <= self.bundles_off:
            off, size = struct.unpack_from('<QQ', d, o)
            ni, nn = struct.unpack_from('<II', d, o + 16)
            outer, cls, sup, tmpl, gimp = struct.unpack_from('<5Q', d, o + 24)
            self.exports.append(dict(serial_off=off, serial_size=size, name=self.fname(ni, nn), cls=cls))
            o += 72

    def fname(self, i, n=0):
        i &= 0x3FFFFFFF
        s = self.names[i] if i < len(self.names) else '?%d' % i
        return s if n == 0 else '%s_%d' % (s, n - 1)

    def bundle_order(self):
        """export indices in serialization order: export bundle headers {first entry, entry count} followed by
        entries {local export index, command (0 create, 1 serialize)} between the bundles offset and the graph"""
        d, o, end = self.d, self.bundles_off, self.graph_off
        first, count = struct.unpack_from('<II', d, o)
        nbundles = 1
        # (more bundles: headers come first; the first header's entry count tells where entries start)
        while True:
            entries_at = o + 8 * nbundles
            if entries_at + 8 * (first + count) <= end and nbundles < 64:
                f2, c2 = struct.unpack_from('<II', d, o + 8 * nbundles) if nbundles * 8 + o < end else (None, None)
                if f2 == first + count and c2 is not None and c2 > 0 and nbundles * 8 + o + 8 <= end:
                    first, count = f2, c2
                    nbundles += 1
                    continue
            break
        entries_at = o + 8 * nbundles
        order = []
        k = entries_at
        while k + 8 <= end:
            idx, cmd = struct.unpack_from('<II', d, k)
            k += 8
            if cmd == 1 and idx < len(self.exports):
                order.append(idx)
        return order

    def export_data(self):
        """the exports' bytes, back to back after the header, in export bundle (serialize) order"""
        o = self.graph_off + self.graph_size   # (the zen header ends after the graph data; exports follow)
        order = self.bundle_order() or list(range(len(self.exports)))
        out = []
        for i in order:
            e = self.exports[i]
            out.append((e, o, o + e['serial_size']))
            o += e['serial_size']
        return out


class Reader:
    def __init__(self, pkg, start, end):
        self.p, self.d, self.o, self.end = pkg, pkg.d, start, end

    def u8(self):
        v = self.d[self.o]
        self.o += 1
        return v

    def i32(self):
        v, = struct.unpack_from('<i', self.d, self.o)
        self.o += 4
        return v

    def u32(self):
        v, = struct.unpack_from('<I', self.d, self.o)
        self.o += 4
        return v

    def i64(self):
        v, = struct.unpack_from('<q', self.d, self.o)
        self.o += 8
        return v

    def f32(self):
        v, = struct.unpack_from('<f', self.d, self.o)
        self.o += 4
        return v

    def name(self):
        i, n = struct.unpack_from('<II', self.d, self.o)
        self.o += 8
        return self.p.fname(i, n)

    def fstring(self):
        n = self.i32()
        if n == 0:
            return ''
        if n > 0:
            s = self.d[self.o:self.o + n - 1].decode('latin1')
            self.o += n
            return s
        n = -n
        s = self.d[self.o:self.o + 2 * (n - 1)].decode('utf-16-le')
        self.o += 2 * n
        return s

    def guid(self):
        self.o += 16

    # ---- tagged properties ----
    def props(self):
        out = {}
        while True:
            name = self.name()
            if name == 'None':
                return out
            typ = self.name()
            size = self.i32()
            idx = self.i32()
            extra = {}
            if typ == 'StructProperty':
                extra['struct'] = self.name()
                self.guid()
            elif typ == 'BoolProperty':
                extra['bool'] = self.u8()
            elif typ in ('ByteProperty', 'EnumProperty'):
                extra['enum'] = self.name()
            elif typ == 'ArrayProperty':
                extra['inner'] = self.name()
            elif typ == 'SetProperty':
                extra['inner'] = self.name()
            elif typ == 'MapProperty':
                extra['inner'] = self.name()
                extra['value'] = self.name()
            if self.u8():
                self.guid()
            start = self.o
            try:
                val = self.value(typ, size, extra)
            except Exception as e:   # noqa: BLE001
                val = '<%s: %s>' % (typ, e)
            self.o = start + size
            key = name if idx == 0 else '%s[%d]' % (name, idx)
            out[key] = val

    def value(self, typ, size, extra, in_array=False):
        if typ == 'BoolProperty':
            return bool(extra.get('bool')) if not in_array else bool(self.u8())
        if typ == 'IntProperty':
            return self.i32()
        if typ == 'UInt32Property':
            return self.u32()
        if typ == 'Int64Property':
            return self.i64()
        if typ == 'FloatProperty':
            return self.f32()
        if typ == 'NameProperty':
            return self.name()
        if typ == 'StrProperty':
            return self.fstring()
        if typ == 'TextProperty':
            flags = self.u32()
            hist = self.d[self.o] if self.o < len(self.d) else 255
            self.o += 1
            if hist == 0:   # base: namespace, key, source string
                ns, key, src = self.fstring(), self.fstring(), self.fstring()
                return {'ns': ns, 'key': key, 'src': src}
            if hist == 11:   # string table entry
                tbl = self.name()
                key = self.fstring()
                return {'table': tbl, 'key': key}
            if hist == 255:
                if self.u32():
                    return self.fstring()
                return ''
            return '<text history %d>' % hist
        if typ in ('ByteProperty', 'EnumProperty'):
            if typ == 'ByteProperty' and (extra.get('enum') in (None, 'None')):
                return self.u8()
            return self.name()
        if typ == 'ObjectProperty' or typ == 'SoftObjectProperty':
            if typ == 'SoftObjectProperty':
                return {'soft': self.name(), 'sub': self.fstring()}
            return {'obj': self.i64() if size == 8 else self.i32()}
        if typ == 'StructProperty':
            st = extra.get('struct')
            if st == 'Vector':
                return [self.f32(), self.f32(), self.f32()]
            if st == 'Vector2D':
                return [self.f32(), self.f32()]
            if st == 'Rotator':
                return [self.f32(), self.f32(), self.f32()]
            if st == 'Quat':
                return [self.f32(), self.f32(), self.f32(), self.f32()]
            if st == 'Guid':
                self.guid()
                return 'guid'
            if st == 'DataTableRowHandle':
                return self.props()
            return self.props()
        if typ == 'ArrayProperty':
            inner = extra['inner']
            n = self.i32()
            if inner == 'StructProperty':
                # inner tag
                self.name()
                self.name()
                self.i32()
                self.i32()
                st = self.name()
                self.guid()
                if self.u8():
                    self.guid()
                return [self.value('StructProperty', 0, {'struct': st}) for _ in range(n)]
            return [self.value(inner, 0, {}, True) for _ in range(min(n, 100000))]
        if typ == 'MapProperty':
            self.i32()   # removed count
            n = self.i32()
            out = []
            for _ in range(n):
                k = self.value(extra['inner'], 0, {}, True) if extra['inner'] != 'StructProperty' else self.props()
                v = self.value(extra['value'], 0, {}, True) if extra['value'] != 'StructProperty' else self.props()
                out.append([k, v])
            return out
        return '<%s %d bytes>' % (typ, size)


def table(pkg):
    rows = {}
    for e, s, end in pkg.export_data():
        r = Reader(pkg, s, end)
        r.props()          # the DataTable object's own properties
        if r.i32():        # (UObject: bool has-guid + guid)
            r.guid()
        n = r.i32()
        for _ in range(n):
            rn = r.name()
            rows[rn] = r.props()
        break
    return rows


if __name__ == '__main__':
    sys.stdout.reconfigure(encoding='utf-8')
    cmd, path = sys.argv[1], sys.argv[2]
    p = Pkg(open(path, 'rb').read())
    if cmd == 'names':
        for i, n in enumerate(p.names):
            print(i, n)
    elif cmd == 'exports':
        for e in p.exports:
            print(e)
    elif cmd == 'table':
        flt = sys.argv[3].lower() if len(sys.argv) > 3 else ''
        rows = table(p)
        print('# %d rows' % len(rows), file=sys.stderr)
        for k, v in rows.items():
            s = json.dumps(v, ensure_ascii=False, default=str)
            if flt in (k + s).lower():
                print(k, s)
