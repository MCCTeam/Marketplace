"""Build declared plugin assets with the published DMCBK packaging APIs."""
import argparse
import json
import os
from pathlib import Path
import subprocess
import tomllib
from urllib.parse import quote

ROOT = Path(__file__).resolve().parents[2]


def run(*arguments: str) -> None:
    subprocess.run(arguments, cwd=ROOT, check=True)


def provider_assemblies(manifest: dict, manifests: dict[str, dict]) -> list[str]:
    assemblies = set()
    for identity in manifest.get("requires", {}) | manifest.get("optional", {}):
        provider = manifests.get(identity)
        if provider is None:
            continue
        for name in provider.get("exports", {}).get("assemblies", []):
            exported = provider["entry"] if name == "entry" else name
            if exported.endswith(".dll"):
                assemblies.add(Path(exported).stem)
    return sorted(assemblies)


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--id", help="Package only this plugin identity")
    parser.add_argument("--output", type=Path, default=ROOT / "out/releases")
    parser.add_argument("--build-root", type=Path, default=ROOT / "out/build")
    parser.add_argument("--repository", default=os.environ.get("GITHUB_REPOSITORY", "MCCTeam/Marketplace"))
    parser.add_argument("--configuration", default="Release")
    parser.add_argument("--catalogue", type=Path, default=ROOT / "out/catalogue/catalog")
    options = parser.parse_args()
    tool = ROOT / "tools/PluginPack/bin" / options.configuration / "net10.0/PluginPack.dll"
    run("dotnet", "build", "tools/PluginPack", "-c", options.configuration)
    paths = sorted((ROOT / "src").glob("*/plugin.toml"))
    manifests = {tomllib.loads(path.read_text(encoding="utf-8"))["id"]: tomllib.loads(path.read_text(encoding="utf-8")) for path in paths}
    if len(manifests) != len(paths):
        raise ValueError("pack.duplicate-identity")
    selected = 0
    for path in paths:
        manifest = tomllib.loads(path.read_text(encoding="utf-8"))
        identity = manifest["id"]
        if options.id and identity != options.id:
            continue
        selected += 1
        declaration = tomllib.loads((path.parent / "release.toml").read_text(encoding="utf-8"))
        assets = declaration.get("assets", [])
        if declaration.get("schema-version") != 2 or not assets:
            raise ValueError(f"pack.asset-declarations-invalid:{identity}")
        if len({(asset["kind"], asset["target"]) for asset in assets}) != len(assets):
            raise ValueError(f"pack.asset-declarations-duplicate:{identity}")
        tag = quote(f"plugin/{identity}/v{manifest['version']}", safe="")
        base_url = f"https://github.com/{options.repository}/releases/download/{tag}/"
        for asset in assets:
            kind, target, entry = asset["kind"], asset["target"], asset["entry"]
            output = options.output.resolve() / identity / manifest["version"]
            if kind == "source":
                payload = path.parent
            elif kind == "compiled":
                projects = sorted(path.parent.glob("*.csproj"))
                if len(projects) != 1:
                    raise ValueError(f"pack.project-ambiguous:{identity}")
                payload = options.build_root.resolve() / identity / target
                build = ["dotnet", "publish", str(projects[0]), "-c", options.configuration,
                         "--self-contained", "false", "-o", str(payload)]
                if target != "any":
                    build.extend(["-r", target])
                run(*build)
            else:
                raise ValueError(f"pack.kind-invalid:{identity}:{kind}")
            run("dotnet", str(tool), "pack", str(path.parent), str(payload), str(output),
                kind, target, base_url, "--entry", entry, *provider_assemblies(manifest, manifests))
    if selected == 0:
        raise ValueError("pack.identity-missing")
    run("dotnet", str(tool), "catalogue", str(options.output.resolve()), str(options.catalogue.resolve()))
    run("dotnet", str(tool), "index", str(ROOT / "src"), str(options.catalogue.resolve()),
        str(options.catalogue.resolve().parent / "mcc-marketplace.toml"))
    print(json.dumps({"packaged": selected, "output": str(options.output.resolve())}))


if __name__ == "__main__":
    main()
