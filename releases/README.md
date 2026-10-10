# Release records

Plugin binaries are immutable GitHub release assets. `tools/release/package_plugins.py` writes rehearsal archives, SHA-256 sidecars and catalogue fragments under ignored `out/`.

The release workflow verifies public downloads before opening a catalogue PR. Merge that PR after checking the release URLs and validation results. See [release instructions](../docs/releases.md).
