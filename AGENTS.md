# Marketplace

This repository maintains MCC plugins and the schema-2 official marketplace. Use .NET SDK `10.0.401` from `global.json`.

Build `Marketplace.slnx` in Release mode. Run `dotnet test Marketplace.slnx -c Release --no-build` after building. Run `dotnet format Marketplace.slnx --verify-no-changes --no-restore`.

Restore DMCBK `0.1.0-preview.7` and UMPK `0.9.0-beta.6` from NuGet.org. The `DMCBK` and `UMPK` submodules are reference checkouts. Do not add library project references or local package feeds.

Read the relevant skill through `.skills` before use. `.skills`, `.codex/skills`, `.agents/skills` and `.claude/skills` link to `MCC-Skills/skills`. Edit shared skills in their repository.

Keep plugin resources, manuals and licenses with their source. Use `tools/release` for packaging. Write generated output under ignored `out/` or a temporary directory. Keep credentials and runtime user data outside Git.

Packages use schema 2. Exclude host DMCBK, UMPK and framework assemblies from archives. Exclude assemblies exported by required providers. Increase the plugin version when payloads, compatibility or asset declarations change. Preserve catalogue history. Publish and verify assets before adding an installable catalogue entry.

Run the full plugin suite and packaging rehearsal for dependency or release changes. Check test totals with `tools/testcounts/check_test_counts.py`. Update the baseline only for an intentional test addition or removal. Distinguish cross-builds, in-memory tests and real server execution.

Use Humanizer for prose and ASD-STE100 principles for instructions. Keep prose paragraphs on one physical line. Do not use em dashes. Preserve unrelated changes. Commit, push and publish only when the user authorizes those actions.
