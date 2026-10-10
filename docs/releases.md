# Release a plugin

Use the .NET SDK in `global.json`. Dependencies come from NuGet.org. Build and test before tagging a release.

## Prepare a version

1. Update the plugin version in `src/<Plugin>/plugin.toml` and its project file.
2. Check library ranges, capabilities and plugin dependencies.
3. Check the asset declarations in `release.toml`.
4. Run the build, tests and formatting check from the README.
5. Package the plugin into a clean rehearsal directory.

```sh
python3 tools/release/package_plugins.py --id auto-eat
python3 tools/release/verify_archives.py out/releases
```

The helper writes archives and SHA-256 sidecars under `out/releases/<id>/<version>`. It writes proposed catalogue metadata under `out/catalogue`. It leaves the checked-in marketplace unchanged. Use `--output`, `--build-root` and `--catalogue` to choose temporary locations. Use `--repository owner/name` when rehearsing another publisher's URLs.

Each archive contains `plugin.toml` at its root. The packager includes plugin resources and private dependencies. It excludes DMCBK, UMPK, framework assemblies and contracts supplied by plugin providers.

Increase the plugin version when payloads, compatibility, dependencies, URLs or asset lists change. Reuse a clean output directory for an unpublished rehearsal. Never replace a published archive with different bytes.

## Declare platform assets

Portable managed plugins use one `compiled`/`any` archive:

```toml
schema-version = 2

[[assets]]
kind = "compiled"
target = "any"
entry = "ExamplePlugin.dll"
```

For a plugin with platform-specific dependencies, declare each supported target separately. The helper runs a framework-dependent `dotnet publish -r <target>` for each declared compiled target. Include its matching native libraries and dependency metadata. Test native operations on their actual targets before publication.

| Platform | Supported process targets |
| --- | --- |
| Windows | `win-x86`, `win-x64`, `win-arm64` |
| Linux glibc | `linux-x64`, `linux-arm64`, `linux-arm` |
| Linux musl | `linux-musl-x64`, `linux-musl-arm64`, `linux-musl-arm` |
| macOS | `osx-x64`, `osx-arm64` |

The process architecture controls selection. A Windows x86 process needs `win-x86`, even on an x64 machine. A source asset must have one `.cs` entry; runtime installation does not build author projects or restore NuGet dependencies. See [marketplace formats](marketplace-v2.md) for mixed releases and source fallback.

## Configure GitHub once

Enable Actions for the repository. Enable **Allow GitHub Actions to create and approve pull requests** in the repository's Actions settings. Configure the `plugins` environment if release approval is required. Automatic publication uses the built-in GitHub token with `contents: write`. Manual per-plugin publication also needs `pull-requests: write` for its catalogue PR. Plugin publication does not require NuGet credentials.

The reusable validation workflow runs tests on seven native process targets and cross-builds four additional Linux targets. Runner labels follow [GitHub's hosted runner reference](https://docs.github.com/en/actions/reference/runners/github-hosted-runners). Local Linux execution does not establish results on those other targets.

## Publish after a merge

Merge reviewed plugin changes into `master`. After **Plugin validation** succeeds for the master push, **Publish marketplace plugins** builds the archives, tests them in a self-contained host, creates annotated tags for new manifest versions and uploads the release assets. It verifies the public hashes before committing the catalogue update to `master`. Recorded versions, including yanked releases, are never reused.

Increase each changed plugin version before merging. The publisher uses the exact validated commit and skips a stale run when a newer master revision already exists. To retry, run **Publish marketplace plugins** on the same master revision. Existing tags and assets must match their original source and bytes.

The generated catalogue commit uses GitHub's built-in token. GitHub does not start another push workflow for that commit, which prevents a publication loop. The publication job has already checked the archives, public hashes and generated catalogue.

## Tag and publish one plugin manually

Merge the plugin changes into `master` before tagging. Replace the example ID and version below with the plugin's manifest values:

```sh
git switch master
git pull --ff-only
git tag -a plugin/auto-eat/v2.0.2 -m "auto-eat 2.0.2"
git push origin plugin/auto-eat/v2.0.2
```

The manual release workflow checks the tag annotation, manifest version and ancestry in `master`. It runs the full validation matrix, builds the declared archives and verifies their local checksums. The `plugins` environment then publishes the exact archive set to GitHub Releases. Existing assets on retry must match byte for byte.

The workflow downloads the new release assets through their public URLs and checks their hashes before appending catalogue metadata. It opens a PR against `master` containing only marketplace changes. Publication retains prior catalogue entries and regenerates the identity index.

GitHub's built-in token does not automatically trigger PR workflows for a PR it creates. Before merging the catalogue PR, run **Plugin validation** through **Run workflow**, selecting that PR's branch. The release job itself has already validated the tagged plugin source and the generated metadata.

Merge the catalogue PR after reviewing its asset URLs and checks. Users can then refresh the marketplace and install the release. No catalogue entry becomes installable before its archives are uploaded and checked.

## Retry and retain history

Use **Release plugin** → **Run workflow** with the same annotated tag for a partial publication. The workflow uploads missing files and refuses to replace different existing bytes. If the payload needs a correction, prepare a new version and tag.

Set `yanked = true` to withdraw a bad release from ordinary new installs. Retain its catalogue record and archive for existing locks and rollback. A rollback restores plugin selections; it cannot undo game actions or external messages already sent by a plugin.

The imported catalogue records refer to unavailable assets in the former repository. They remain yanked. The prepared patch versions publish new assets here and require DMCBK `0.1.0-preview.7` and UMPK `0.9.0-beta.6` or later within their declared ranges.
