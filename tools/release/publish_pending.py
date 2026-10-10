"""Publish unreleased manifest versions after a successful master validation."""
from __future__ import annotations

import argparse
import json
import os
from pathlib import Path
import re
import shutil
import subprocess
import tomllib

import publish_assets

ROOT = Path(__file__).resolve().parents[2]


def run(*arguments: str, capture: bool = False) -> str:
    result = subprocess.run(arguments, cwd=ROOT, check=True, text=True,
                            stdout=subprocess.PIPE if capture else None)
    return result.stdout.strip() if capture else ""


def pending(root: Path) -> list[dict[str, str]]:
    selected = []
    for path in sorted((root / "src").glob("*/plugin.toml")):
        manifest = tomllib.loads(path.read_text(encoding="utf-8"))
        identity, version = manifest["id"], manifest["version"]
        if not re.fullmatch(r"[a-z0-9][a-z0-9-]*", identity):
            raise ValueError("release.identity-invalid")
        if not re.fullmatch(r"[0-9]+\.[0-9]+\.[0-9]+(?:-[0-9A-Za-z.-]+)?(?:\+[0-9A-Za-z.-]+)?", version):
            raise ValueError("release.version-invalid")
        catalogue = root / "marketplace/catalog" / f"{identity}.toml"
        recorded = tomllib.loads(catalogue.read_text(encoding="utf-8"))["releases"] if catalogue.exists() else []
        # Recorded versions are immutable, including deliberately yanked versions.
        if any(release["version"] == version for release in recorded):
            continue
        selected.append({"id": identity, "version": version, "tag": f"plugin/{identity}/v{version}"})
    return selected


def ensure_tag(item: dict[str, str], revision: str) -> None:
    reference = "refs/tags/" + item["tag"]
    lookup = subprocess.run(["git", "show-ref", "--verify", "--quiet", reference], cwd=ROOT)
    if lookup.returncode == 0:
        if run("git", "cat-file", "-t", reference, capture=True) != "tag":
            raise ValueError("release.annotated-tag-required")
        if run("git", "rev-parse", reference + "^{commit}", capture=True) != revision:
            raise ValueError("release.tag-revision-mismatch: retry the original source revision or bump the version")
        return
    if lookup.returncode != 1:
        raise RuntimeError("release.tag-lookup-failed")
    run("git", "tag", "-a", item["tag"], revision, "-m", f"{item['id']} {item['version']}")
    run("git", "push", "origin", reference)


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--publish", action="store_true")
    args = parser.parse_args()
    selected = pending(ROOT)
    print(json.dumps({"pending": selected}), flush=True)
    if not args.publish or not selected:
        return
    revision = run("git", "rev-parse", "HEAD", capture=True)
    expected = os.environ["VALIDATED_SHA"]
    if revision != expected:
        raise ValueError("release.unvalidated-checkout")
    run("git", "fetch", "origin", "master", "--tags")
    if run("git", "rev-parse", "origin/master", capture=True) != revision:
        print("A newer master revision exists; its validation will publish the pending versions.")
        return
    run("git", "config", "user.name", "github-actions[bot]")
    run("git", "config", "user.email", "41898282+github-actions[bot]@users.noreply.github.com")
    for item in selected:
        ensure_tag(item, revision)
        os.environ.update(RELEASE_TAG=item["tag"], PLUGIN_ID=item["id"], PLUGIN_VERSION=item["version"])
        publish_assets.main()
        run("dotnet", "tools/PluginPack/bin/Release/net10.0/PluginPack.dll", "verify-assets",
            f"out/catalogue/catalog/{item['id']}.toml")
    staged = ROOT / "out/pending-releases"
    for item in selected:
        source = ROOT / "out/releases" / item["id"] / item["version"]
        shutil.copytree(source, staged / item["id"] / item["version"], dirs_exist_ok=True)
    # Only verified, uploaded assets become visible in the current catalogue.
    run("git", "fetch", "origin", "master")
    run("git", "checkout", "-B", "catalogue-publication", "origin/master")
    run("dotnet", "tools/PluginPack/bin/Release/net10.0/PluginPack.dll", "catalogue", str(staged), "marketplace/catalog")
    run("dotnet", "tools/PluginPack/bin/Release/net10.0/PluginPack.dll", "index", "src", "marketplace/catalog", "marketplace/mcc-marketplace.toml")
    changed = run("git", "diff", "--name-only", capture=True).splitlines()
    if any(not path.startswith("marketplace/") for path in changed):
        raise ValueError("release.unexpected-catalogue-change")
    run("git", "add", "marketplace")
    run("git", "commit", "-m", f"chore: publish {len(selected)} plugin releases")
    run("git", "push", "origin", "HEAD:master")
    print(json.dumps({"published": selected}), flush=True)


if __name__ == "__main__":
    main()
