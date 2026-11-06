"""Old porter's API backed by AnimIT's recovered native .clip decoder."""
import json
import os
from pathlib import Path
import subprocess
import tempfile


class Clip:
    def __init__(self, data):
        exe = os.environ.get('ANIMIT_DECODER_EXE')
        if not exe or not Path(exe).is_file():
            raise ValueError('AnimIT native decoder executable was not provided.')
        with tempfile.TemporaryDirectory(prefix='animit-clip-') as temp:
            source, result = Path(temp) / 'source.clip', Path(temp) / 'decoded.json'
            source.write_bytes(data)
            child = subprocess.run([exe, '--decodeclip', str(source), str(result)], capture_output=True,
                                   creationflags=getattr(subprocess, 'CREATE_NO_WINDOW', 0), timeout=120)
            if not result.is_file():
                raise ValueError('The native clip decoder did not return a result.')
            decoded = json.loads(result.read_text(encoding='utf-8'))
            if child.returncode or not decoded['ok']:
                raise ValueError(decoded.get('error', 'Clip decoding failed.'))
            self.frames, self.bones = decoded['frames'], decoded['bones']


def decode(clip):
    return (clip.bones,)
