# Local validation

Validated on Linux x64 with .NET SDK `10.0.401`, published DMCBK `0.1.0-preview.6` and published UMPK `0.9.0-beta.6`. NuGet.org was the only package source. Fresh package caches and package repository metadata confirmed the exact published releases.

| Check | Result |
| --- | --- |
| Clean Release build | Passed, zero warnings and errors |
| Plugin tests | 773 passed, zero failed or skipped |
| Test-count guard | Passed; the four excluded plugins account for 37 removed cases |
| Release tooling tests | 15 passed |
| Formatting | Applied and verified |
| Workflow validation | Passed with actionlint 1.7.12 |
| Marketplace metadata | Both retained and generated indexes passed MCC's validator |
| Release archives | All 25 passed checksum, path and metadata checks |
| Clean-host archive loading | All 25 loaded and activated through published DMCBK packages |
| Scripted session | Passed with all 25 archive plugins loaded |
| Cross-builds | All 11 supported Windows, Linux glibc/musl and macOS targets passed |
| Independent templates | Compiled and mixed examples built outside the source checkouts |

The ordinary test project's OpenAI preload masked a missing dependency in the original archive output. The clean-host check exposed it. DMCBK `0.1.0-preview.6` fixes packaging and loading of the private `System.ClientModel` and `System.Memory.Data` dependencies. CI now runs that archive check before release.

The archive session uses protocol frames in memory. It does not prove gameplay on a live Minecraft server. External AI-provider calls were disabled. Cross-builds do not establish native execution on other operating systems or architectures; the CI runtime matrix provides those checks when it runs.

MCC `feat/mcc-2.0` also passed a clean Release build, all 915 CLI tests, localization, documentation checks, a documentation build, Linux distribution publishing and its help smoke test with DMCBK preview.6. The DMCBK release itself passed all three operating-system builds and test jobs before NuGet publication.

Full local logs and archive digests are under `/tmp/marketplace-validation` and `/tmp/mcc-preview6-validation`. Rehearsal archives are not committed or published plugin releases.

## Publication follow-up, 2026-10-10

A second clean validation used published DMCBK `0.1.0-preview.7` and UMPK `0.9.0-beta.6` from NuGet.org with a fresh cache. The Release build had no warnings or errors. All 773 plugin tests and 19 release tooling tests passed. Formatting and workflow lint passed.

All 25 bumped plugin archives passed checksum, manifest and dependency checks. A self-contained Linux x64 host loaded and activated every archive, then ran a scripted in-memory session. The MCP HTTP endpoint returned 401 without a token and completed initialization with its configured token. This check reproduces the distribution layout that previously exposed missing framework and private AI SDK dependencies. It does not claim real-server gameplay coverage.
