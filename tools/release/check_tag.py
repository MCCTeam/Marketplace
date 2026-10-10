"""Validate an annotated per-plugin release tag against master and its manifest."""
import json
import os
from pathlib import Path
import re
import subprocess
import tomllib

ROOT = Path(__file__).resolve().parents[2]


def git(*arguments: str) -> str:
    return subprocess.run(["git", *arguments], cwd=ROOT, check=True, capture_output=True, text=True).stdout.strip()


def main() -> None:
    tag = os.environ["RELEASE_TAG"]
    match = re.fullmatch(r"plugin/([a-z0-9][a-z0-9-]*)/v([0-9]+\.[0-9]+\.[0-9]+(?:-[0-9A-Za-z.-]+)?(?:\+[0-9A-Za-z.-]+)?)", tag)
    if match is None or git("cat-file", "-t", "refs/tags/" + tag) != "tag":
        raise ValueError("release.annotated-tag-required")
    identity, version = match.groups()
    git("merge-base", "--is-ancestor", tag + "^{commit}", "origin/master")
    if git("rev-parse", "HEAD") != git("rev-parse", tag + "^{commit}"):
        raise ValueError("release.checkout-mismatch")
    manifests = [tomllib.loads(path.read_text(encoding="utf-8")) for path in (ROOT / "src").glob("*/plugin.toml")]
    selected = [manifest for manifest in manifests if manifest["id"] == identity]
    if len(selected) != 1 or selected[0]["version"] != version:
        raise ValueError("release.manifest-version-mismatch")
    values = {"tag": tag, "id": identity, "version": version}
    if output := os.environ.get("GITHUB_OUTPUT"):
        with Path(output).open("a", encoding="utf-8") as stream:
            for key, value in values.items():
                stream.write(f"{key}={value}\n")
    print(json.dumps(values))


if __name__ == "__main__":
    main()
