"""Check archive paths, checksums and agreement with generated release metadata."""
import hashlib
import json
from pathlib import Path
import stat
import sys
import tomllib
import zipfile

PRIVATE_SDK_ASSEMBLIES = {'system.clientmodel', 'system.memory.data', 'microsoft.extensions.ai', 'microsoft.extensions.ai.abstractions'}

SHARED_ASSEMBLIES = {
    'dmcbk', 'dmcbk.core', 'dmcbk.commands', 'dmcbk.configuration', 'dmcbk.beacon',
    'dmcbk.pluginsdk', 'dmcbk.plugins', 'dmcbk.marketplace', 'dmcbk.testing',
    'netstandard', 'mscorlib', 'system', 'microsoft.csharp',
    'microsoft.extensions.logging.abstractions', 'brigadier.net', 'tomlet',
}


def check_name(name: str) -> None:
    if not name or '\\' in name or ':' in name or any(part in {'', '.', '..'} for part in name.split('/')):
        raise ValueError(f'release.unsafe-path:{name}')


def verify(root: Path) -> int:
    archives = sorted(root.rglob('*.zip'))
    if not archives:
        raise ValueError('release.archives-missing')
    for path in archives:
        if path.is_symlink() or path.stat().st_size > 128 * 1024 * 1024:
            raise ValueError(f'release.archive-invalid:{path.name}')
        with path.open('rb') as stream:
            digest = hashlib.file_digest(stream, 'sha256').hexdigest()
        if Path(str(path) + '.sha256').read_text(encoding='utf-8') != f'{digest}  {path.name}\n':
            raise ValueError(f'release.checksum-mismatch:{path.name}')
        catalogue = tomllib.loads(Path(str(path) + '.catalogue.toml').read_text(encoding='utf-8'))
        with zipfile.ZipFile(path) as archive:
            manifest = tomllib.loads(archive.read('plugin.toml').decode('utf-8'))
            aspnetcore = 'aspnetcore' in manifest.get('needs', [])
            entries = archive.infolist()
            if len(entries) > 10000 or sum(entry.file_size for entry in entries) > 512 * 1024 * 1024:
                raise ValueError(f'release.archive-limits:{path.name}')
            seen = set()
            for entry in entries:
                check_name(entry.filename)
                folded = entry.filename.casefold()
                if folded in seen:
                    raise ValueError(f'release.duplicate-path:{entry.filename}')
                seen.add(folded)
                mode = entry.external_attr >> 16
                if stat.S_ISLNK(mode):
                    raise ValueError(f'release.archive-link:{entry.filename}')
                if entry.filename.lower().endswith('.dll'):
                    name = Path(entry.filename).stem.lower()
                    if name in SHARED_ASSEMBLIES or name.startswith('umpk.') or (name.startswith('system.') and name not in PRIVATE_SDK_ASSEMBLIES) or (aspnetcore and name not in PRIVATE_SDK_ASSEMBLIES and (name == 'microsoft.aspnetcore' or name.startswith(('microsoft.aspnetcore.', 'microsoft.extensions.')))):
                        raise ValueError(f'release.host-assembly:{entry.filename}')
            names = archive.namelist()
            if 'LICENSE.md' not in names:
                raise ValueError(f'release.license-missing:{path.name}')
            manifest = tomllib.loads(archive.read('plugin.toml').decode('utf-8'))
            releases = catalogue.get('releases', [])
            if catalogue.get('schema-version') != 2 or manifest.get('schema-version') != 2 or len(releases) != 1:
                raise ValueError(f'release.metadata-invalid:{path.name}')
            release = releases[0]
            assets = release.get('assets', [])
            if len(assets) != 1:
                raise ValueError(f'release.asset-count:{path.name}')
            asset = assets[0]
            if catalogue['id'] != manifest['id'] or release['version'] != manifest['version'] or asset['sha256'] != digest:
                raise ValueError(f'release.metadata-mismatch:{path.name}')
            for field in ['api-version', 'dmcbk', 'umpk', 'framework', 'needs', 'requires', 'optional', 'hosts']:
                fallback = [] if field == 'needs' else {} if field in {'requires', 'optional', 'hosts'} else None
                if release.get(field, fallback) != manifest.get(field, fallback):
                    raise ValueError(f'release.compatibility-mismatch:{path.name}:{field}')
            if asset['kind'] != manifest['kind'] or asset['target'] != manifest['target']:
                raise ValueError(f'release.asset-mismatch:{path.name}')
            required = [manifest['entry'], *manifest.get('deps', [])]
            required.extend(name for name in manifest.get('exports', {}).get('assemblies', []) if name != 'entry')
            for name in required:
                check_name(name)
                if name not in names:
                    raise ValueError(f'release.payload-missing:{path.name}:{name}')
    return len(archives)


if __name__ == '__main__':
    print(json.dumps({'verified_archives': verify(Path(sys.argv[1]))}))
