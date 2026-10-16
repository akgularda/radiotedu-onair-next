#!/usr/bin/env python3
"""CI-only Windows codec build. No runtime fallback; no GPL/nonfree configuration."""
import hashlib, json, os, pathlib, re, shutil, subprocess, sys, tarfile

REPO = pathlib.Path(__file__).resolve().parents[2]
SCRIPT = pathlib.Path(__file__).resolve()
LOCK_PATH = SCRIPT.with_name('windows-codec.lock.json')
LOCK = json.loads(LOCK_PATH.read_text())
OUT = REPO / 'artifacts/windows-codec-runtime'
WORK = pathlib.Path(os.environ.get('RUNNER_TEMP', '/tmp')) / 'onair-codec-build'

def run(args, cwd=None, env=None):
    print('+ ' + ' '.join(map(str, args)), flush=True)
    subprocess.run(list(map(str, args)), cwd=cwd, env=env, check=True)

def sha(p):
    h = hashlib.sha256()
    with open(p, 'rb') as f:
        for b in iter(lambda: f.read(1024 * 1024), b''):
            h.update(b)
    return h.hexdigest()

def source(name):
    spec = LOCK[name]
    root = WORK / name
    run(['git', 'init', root])
    run(['git', 'remote', 'add', 'origin', spec['repository']], root)
    run(['git', 'fetch', '--depth=1', 'origin', 'refs/tags/' + spec['tag'] + ':refs/tags/' + spec['tag']], root)
    for ref, expected in [('refs/tags/' + spec['tag'], spec['tagObject']), ('refs/tags/' + spec['tag'] + '^{}', spec['commit'])]:
        actual = subprocess.check_output(['git', 'rev-parse', ref], cwd=root, text=True).strip()
        if actual != expected:
            raise RuntimeError(name + ' immutable upstream ref differs: ' + ref)
    run(['git', 'checkout', '--detach', spec['commit']], root)
    run(['git', 'archive', '--format=tar.gz', '--prefix=' + name + '-' + spec['tag'] + '/', '--output=' + str(OUT / 'Sources' / (name + '-' + spec['tag'] + '.tar.gz')), spec['commit']], root)
    return root

def main():
    if os.environ.get('GITHUB_ACTIONS') != 'true' or os.environ.get('RUNNER_OS') != 'Linux':
        raise RuntimeError('Build only on a disposable Linux GitHub runner, never the broadcasting PC.')
    if OUT.exists() or WORK.exists():
        raise RuntimeError('A fresh isolated build directory is required; refusing recursive replacement.')
    for folder in [OUT / 'bin', OUT / 'Sources', OUT / 'Licenses', WORK]:
        folder.mkdir(parents=True)
    prefix = WORK / 'prefix'
    fdk = source('fdk')
    ffmpeg = source('ffmpeg')
    cc = 'x86_64-w64-mingw32-gcc-win32'
    cxx = 'x86_64-w64-mingw32-g++-win32'
    env = dict(os.environ, CC=cc, CXX=cxx, CFLAGS='-O2 -march=x86-64 -mtune=generic',
               CXXFLAGS='-O2 -march=x86-64 -mtune=generic', LDFLAGS='-static-libgcc -static-libstdc++')
    run(['./autogen.sh'], fdk, env)
    fdk_args = ['./configure', '--host=x86_64-w64-mingw32', '--prefix=' + str(prefix), '--enable-shared', '--disable-static']
    run(fdk_args, fdk, env)
    run(['make', '-j4'], fdk, env)
    run(['make', 'install'], fdk, env)
    env['PKG_CONFIG_LIBDIR'] = str(prefix / 'lib/pkgconfig')
    env['PKG_CONFIG_PATH'] = ''
    ff_args = ['./configure', '--prefix=' + str(prefix), '--target-os=mingw32', '--arch=x86_64', '--cpu=generic',
               '--enable-cross-compile', '--cross-prefix=x86_64-w64-mingw32-', '--cc=' + cc, '--cxx=' + cxx,
               '--pkg-config=pkg-config', '--enable-shared', '--disable-static', '--disable-gpl', '--disable-version3',
               '--disable-nonfree', '--disable-autodetect', '--enable-libfdk-aac', '--enable-schannel',
               '--enable-w32threads', '--disable-pthreads', '--disable-avdevice', '--disable-doc', '--disable-debug',
               '--disable-ffplay', '--extra-cflags=-O2 -march=x86-64 -mtune=generic',
               '--extra-ldflags=-static-libgcc -static-libstdc++']
    run(ff_args, ffmpeg, env)
    config = (ffmpeg / 'ffbuild/config.mak').read_text()
    if not re.search(r'^!CONFIG_GPL=yes$', config, re.M) or not re.search(r'^!CONFIG_NONFREE=yes$', config, re.M):
        raise RuntimeError('GPL/nonfree must remain disabled in the actual build configuration.')
    run(['make', '-j4'], ffmpeg, env)
    run(['make', 'install'], ffmpeg, env)
    for f in sorted((prefix / 'bin').iterdir()):
        if f.suffix.lower() in ('.dll', '.exe'):
            shutil.copy2(f, OUT / 'bin' / f.name)
    if not (OUT / 'bin/ffmpeg.exe').exists() or not (OUT / 'bin/ffprobe.exe').exists():
        raise RuntimeError('Both Windows tools must be built.')
    bundled = {f.name.lower() for f in (OUT / 'bin').iterdir()}
    system = {'kernel32.dll', 'msvcrt.dll', 'user32.dll', 'advapi32.dll', 'ws2_32.dll', 'bcrypt.dll', 'crypt32.dll',
              'secur32.dll', 'ncrypt.dll', 'shell32.dll', 'ole32.dll', 'oleaut32.dll', 'gdi32.dll', 'psapi.dll', 'winmm.dll',
              'shlwapi.dll', 'version.dll', 'setupapi.dll', 'mfplat.dll', 'mfuuid.dll', 'strmiids.dll', 'vfw32.dll',
              'avicap32.dll', 'd3d11.dll', 'dxgi.dll', 'd3d9.dll', 'opengl32.dll', 'glu32.dll', 'imm32.dll',
              'ntdll.dll', 'comdlg32.dll', 'comctl32.dll', 'dxva2.dll', 'dwmapi.dll', 'usp10.dll'}
    imports = {}
    for f in sorted((OUT / 'bin').iterdir()):
        detail = subprocess.check_output(['x86_64-w64-mingw32-objdump', '-p', str(f)], text=True)
        deps = sorted(set(re.findall(r'DLL Name:\s*(\S+)', detail, re.I)))
        imports[f.name] = deps
        unknown = [d for d in deps if d.lower() not in bundled | system and not d.lower().startswith(('api-ms-win-', 'ext-ms-win-'))]
        if unknown:
            raise RuntimeError('Unbundled non-system DLL dependency in ' + f.name + ': ' + str(unknown))
    shutil.copy2(fdk / 'NOTICE', OUT / 'Licenses/FDK-AAC-NOTICE.txt')
    for name in ['COPYING.LGPLv2.1', 'LICENSE.md']:
        shutil.copy2(ffmpeg / name, OUT / 'Licenses' / name)
    shutil.copy2(LOCK_PATH, OUT / LOCK_PATH.name)
    shutil.copy2(SCRIPT, OUT / 'Sources' / SCRIPT.name)
    (OUT / 'Sources/build-configurations.json').write_text(json.dumps({'fdk': fdk_args, 'ffmpeg': ff_args, 'environment': {k: env[k] for k in ['CC','CXX','CFLAGS','CXXFLAGS','LDFLAGS','PKG_CONFIG_LIBDIR']}}, indent=2))
    (OUT / 'Sources/ffmpeg-config.mak').write_text(config)
    (OUT / 'Sources/toolchain.txt').write_text(subprocess.check_output([cc, '--version'], text=True) + subprocess.check_output(['x86_64-w64-mingw32-ld', '--version'], text=True))
    manifest = {'schemaVersion':1, 'target':'win-x64', 'dependencyLockSha256':sha(LOCK_PATH), 'buildScriptSha256':sha(SCRIPT),
                'upstreams':LOCK, 'imports':imports, 'files':[]}
    for f in sorted(OUT.rglob('*')):
        if f.is_file():
            manifest['files'].append({'path':f.relative_to(OUT).as_posix(), 'bytes':f.stat().st_size, 'sha256':sha(f)})
    (OUT / 'runtime-manifest.json').write_text(json.dumps(manifest, indent=2) + '\n')
    print('Built source-preserving Windows runtime: ' + str(len(manifest['files'])) + ' files; every imported non-system DLL bundled.', flush=True)

if __name__ == '__main__':
    try:
        main()
    finally:
        diagnostics = REPO / 'artifacts/windows-codec-build'
        diagnostics.mkdir(parents=True, exist_ok=True)
        for label, candidate in [('ffmpeg-config.log', WORK / 'ffmpeg/ffbuild/config.log'), ('fdk-config.log', WORK / 'fdk/config.log')]:
            if candidate.is_file():
                shutil.copy2(candidate, diagnostics / label)
