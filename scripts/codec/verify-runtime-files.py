#!/usr/bin/env python3
"""Bounded file verification for fresh and cached dependency builds."""
import hashlib, json, pathlib, sys

def digest(path):
    h=hashlib.sha256()
    with path.open('rb') as f:
        for block in iter(lambda:f.read(1024*1024),b''):h.update(block)
    return h.hexdigest()

def main():
    root=pathlib.Path(sys.argv[1]).resolve(strict=True)
    script=pathlib.Path(__file__).resolve().parent
    manifest=json.loads((root/'runtime-manifest.json').read_text())
    if manifest['schemaVersion']!=1 or manifest['target']!='win-x64':raise ValueError('Unexpected runtime manifest')
    if manifest['dependencyLockSha256']!=digest(script/'windows-codec.lock.json') or manifest['buildScriptSha256']!=digest(script/'build-windows-codecs.py'):raise ValueError('Runtime inputs changed')
    known=set()
    for row in manifest['files']:
        candidate=root/row['path']
        if candidate.is_symlink():raise ValueError('Symlink forbidden')
        path=candidate.resolve(strict=True)
        if not path.is_relative_to(root) or path in known:raise ValueError('Invalid or duplicate runtime path')
        known.add(path)
        if path.stat().st_size!=row['bytes'] or digest(path)!=row['sha256']:raise ValueError('Runtime file integrity failed: '+row['path'])
    actual={p.resolve() for p in root.rglob('*') if p.is_file() and p.name!='runtime-manifest.json'}
    if actual!=known:raise ValueError('Runtime contains unmanifested files')
    required={'bin/ffmpeg.exe','bin/ffprobe.exe','Licenses/FDK-AAC-NOTICE.txt','Licenses/COPYING.LGPLv2.1','Sources/build-windows-codecs.py'}
    if not required.issubset({row['path'] for row in manifest['files']}):raise ValueError('Incomplete runtime distribution')
    print('Verified complete cached/fresh codec runtime: '+str(len(known))+' exact files')

if __name__=='__main__':main()
