import importlib.util
import json
import os
from pathlib import Path
import subprocess
import tempfile
import unittest
from unittest.mock import patch
import zipfile
import hashlib

ROOT = Path(__file__).resolve().parents[2]


def load(name):
    spec = importlib.util.spec_from_file_location(name, ROOT / 'tools/release' / f'{name}.py')
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


package = load('package_plugins')
verify = load('verify_archives')
publish = load('publish_assets')
tag = load('check_tag')


class ArchiveTests(unittest.TestCase):
    def setUp(self):
        self.temporary = tempfile.TemporaryDirectory()
        self.root = Path(self.temporary.name)
        self.addCleanup(self.temporary.cleanup)

    def archive(self, extras=None, release_range='>=0.1.0-preview.6 <0.2.0'):
        manifest = '''schema-version = 2
id = "demo"
version = "1.0.0"
api-version = "1.0"
framework = "net10.0"
kind = "compiled"
target = "any"
entry = "Demo.dll"
dmcbk = ">=0.1.0-preview.6 <0.2.0"
umpk = ">=0.9.0-beta.6 <0.10.0"
needs = []
deps = []
'''
        path = self.root / 'demo-1.0.0-compiled-any.zip'
        with zipfile.ZipFile(path, 'w') as archive:
            archive.writestr('plugin.toml', manifest)
            archive.writestr('Demo.dll', b'test payload')
            archive.writestr('LICENSE.md', 'MIT')
            for name, content in (extras or {}).items():
                archive.writestr(name, content)
        digest = hashlib.sha256(path.read_bytes()).hexdigest()
        Path(str(path) + '.sha256').write_text(f'{digest}  {path.name}\n')
        Path(str(path) + '.catalogue.toml').write_text(f'''schema-version = 2
id = "demo"
[[releases]]
version = "1.0.0"
api-version = "1.0"
framework = "net10.0"
dmcbk = "{release_range}"
umpk = ">=0.9.0-beta.6 <0.10.0"
needs = []
[[releases.assets]]
kind = "compiled"
target = "any"
url = "https://github.com/MCCTeam/Marketplace/releases/download/demo/{path.name}"
sha256 = "{digest}"
''')
        return path

    def test_valid_archive(self):
        self.archive()
        self.assertEqual(1, verify.verify(self.root))

    def test_wrong_checksum(self):
        path = self.archive()
        Path(str(path) + '.sha256').write_text('wrong\n')
        with self.assertRaisesRegex(ValueError, 'checksum-mismatch'):
            verify.verify(self.root)

    def test_compatibility_must_match_manifest(self):
        self.archive(release_range='*')
        with self.assertRaisesRegex(ValueError, 'compatibility-mismatch'):
            verify.verify(self.root)

    def test_traversal_is_rejected(self):
        self.archive({'../outside.txt': b'bad'})
        with self.assertRaisesRegex(ValueError, 'unsafe-path'):
            verify.verify(self.root)

    def test_case_insensitive_duplicates_are_rejected(self):
        self.archive({'demo.dll': b'duplicate'})
        with self.assertRaisesRegex(ValueError, 'duplicate-path'):
            verify.verify(self.root)

    def test_host_assembly_is_rejected(self):
        self.archive({'DMCBK.Core.dll': b'host contract'})
        with self.assertRaisesRegex(ValueError, 'host-assembly'):
            verify.verify(self.root)


class ProviderTests(unittest.TestCase):
    def test_required_provider_exports_are_excluded(self):
        providers = {'llm-core': {'entry': 'LlmCore.dll', 'exports': {'assemblies': ['entry', 'lib/Contracts.dll']}}}
        self.assertEqual(['Contracts', 'LlmCore'], package.provider_assemblies({'requires': {'llm-core': '^0.2.1'}}, providers))

    def test_optional_provider_exports_are_excluded(self):
        providers = {'llm-core': {'entry': 'LlmCore.dll', 'exports': {'assemblies': ['entry']}}}
        self.assertEqual(['LlmCore'], package.provider_assemblies({'optional': {'llm-core': '^0.2.1'}}, providers))

    def test_unexported_provider_assembly_is_not_shared(self):
        self.assertEqual([], package.provider_assemblies({'requires': {'other': '*'}}, {'other': {'entry': 'Private.dll'}}))


class PublicationTests(unittest.TestCase):
    def setUp(self):
        self.temporary = tempfile.TemporaryDirectory()
        self.root = Path(self.temporary.name)
        self.addCleanup(self.temporary.cleanup)
        self.folder = self.root / 'out/releases/demo/1.0.0'
        self.folder.mkdir(parents=True)
        self.archive = self.folder / 'demo-1.0.0-compiled-any.zip'
        self.archive.write_bytes(b'immutable payload')
        self.environment = {'RELEASE_TAG': 'plugin/demo/v1.0.0', 'PLUGIN_ID': 'demo', 'PLUGIN_VERSION': '1.0.0', 'GITHUB_REPOSITORY': 'MCCTeam/Marketplace'}

    def execute(self, callback):
        previous = Path.cwd()
        try:
            os.chdir(self.root)
            with patch.dict(os.environ, self.environment), patch.object(publish, 'gh', side_effect=callback):
                publish.main()
        finally:
            os.chdir(previous)

    def test_retry_rejects_different_existing_bytes(self):
        calls = []

        def gh(*arguments, **kwargs):
            calls.append(arguments)
            if arguments[0] == 'api':
                return subprocess.CompletedProcess([], 0, json.dumps({'assets': [{'name': self.archive.name}]}), '')
            if arguments[1] == 'download':
                destination = Path(arguments[arguments.index('--dir') + 1]) / self.archive.name
                destination.write_bytes(b'different bytes')
            return subprocess.CompletedProcess([], 0, '', '')

        with self.assertRaisesRegex(ValueError, 'existing-payload-differs'):
            self.execute(gh)
        self.assertFalse(any(call[:2] == ('release', 'upload') for call in calls))

    def test_retry_accepts_identical_existing_bytes(self):
        calls = []

        def gh(*arguments, **kwargs):
            calls.append(arguments)
            if arguments[0] == 'api':
                return subprocess.CompletedProcess([], 0, json.dumps({'assets': [{'name': self.archive.name}]}), '')
            if arguments[1] == 'download':
                destination = Path(arguments[arguments.index('--dir') + 1]) / self.archive.name
                destination.write_bytes(self.archive.read_bytes())
            return subprocess.CompletedProcess([], 0, '', '')

        self.execute(gh)
        self.assertFalse(any(call[:2] == ('release', 'upload') for call in calls))

    def test_lookup_failure_does_not_create_release(self):
        def gh(*arguments, **kwargs):
            self.assertEqual('api', arguments[0])
            return subprocess.CompletedProcess([], 1, '', 'HTTP 500')

        with self.assertRaisesRegex(RuntimeError, 'HTTP 500'):
            self.execute(gh)

    def test_unexpected_file_is_rejected_before_network(self):
        (self.folder / 'secret.txt').write_text('not a release asset')
        with self.assertRaisesRegex(ValueError, 'unexpected-file'):
            self.execute(lambda *args, **kwargs: self.fail('unexpected network call'))


class TagTests(unittest.TestCase):
    def test_lightweight_tag_is_rejected(self):
        with patch.dict(os.environ, {'RELEASE_TAG': 'plugin/demo/v1.0.0'}), patch.object(tag, 'git', return_value='commit'):
            with self.assertRaisesRegex(ValueError, 'annotated-tag-required'):
                tag.main()

    def test_invalid_version_is_rejected_before_git(self):
        with patch.dict(os.environ, {'RELEASE_TAG': 'plugin/demo/vbad-version'}), patch.object(tag, 'git') as git:
            with self.assertRaisesRegex(ValueError, 'annotated-tag-required'):
                tag.main()
            git.assert_not_called()


if __name__ == '__main__':
    unittest.main()
