"""Publish exact rehearsed assets, refusing replacement on retry."""
import hashlib
import json
import os
from pathlib import Path
import subprocess
import tempfile
from urllib.parse import quote


def gh(*arguments: str, check: bool = True) -> subprocess.CompletedProcess[str]:
    return subprocess.run(["gh", *arguments], check=check, capture_output=True, text=True)


def digest(path: Path) -> str:
    with path.open("rb") as stream:
        return hashlib.file_digest(stream, "sha256").hexdigest()


def main() -> None:
    tag, identity, version = (os.environ[name] for name in ("RELEASE_TAG", "PLUGIN_ID", "PLUGIN_VERSION"))
    root = Path("out/releases") / identity / version
    files = sorted(path for path in root.iterdir() if path.is_file())
    if not files or not any(path.suffix == ".zip" for path in files):
        raise ValueError("release.artifacts-missing")
    for path in files:
        if path.is_symlink() or not path.name.endswith((".zip", ".zip.sha256", ".zip.catalogue.toml")):
            raise ValueError(f"release.unexpected-file:{path.name}")
    lookup = gh("api", f"repos/{os.environ['GITHUB_REPOSITORY']}/releases/tags/{quote(tag, safe='')}", check=False)
    if lookup.returncode:
        if "HTTP 404" not in lookup.stderr:
            raise RuntimeError(lookup.stderr)
        gh("release", "create", tag, "--verify-tag", "--title", f"{identity} {version}", "--generate-notes")
        existing: set[str] = set()
    else:
        existing = {asset["name"] for asset in json.loads(lookup.stdout)["assets"]}
    for path in files:
        if path.name in existing:
            with tempfile.TemporaryDirectory(prefix="dmcbk-published-") as temporary:
                gh("release", "download", tag, "--pattern", path.name, "--dir", temporary)
                if digest(Path(temporary) / path.name) != digest(path):
                    raise ValueError(f"release.existing-payload-differs:{path.name}")
        else:
            gh("release", "upload", tag, str(path))
    print(json.dumps({"published": len(files), "tag": tag}))


if __name__ == "__main__":
    main()
