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

## Public releases and MCC default

Master validation passed all seven native targets and four cross-builds. The automatic publication workflow then created 25 public releases and appended their verified catalogue records. Linux x64 independently downloaded every public archive and checked its SHA-256. The published catalogue passed MCC's schema validator.

A clean self-contained MCC build restored only published NuGet packages. All 25 public plugins installed and activated. Installing `llm-test` brought in `llm-core` first. The test checked exact lock versions, URLs and digests, restart and pin persistence, disable/enable/reload/unpin, and uninstall preservation of settings and data. MCP rejected unauthenticated HTTP requests and completed authenticated initialization before and after restart.

A separate real-server test ran all 25 plugins on vanilla Minecraft `1.21.11`, in offline mode on isolated loopback port `55731`. ChatLog recorded a server broadcast, stopped recording after disable, and recorded new broadcasts after enable and reload. MCP authentication also passed during that live connection. MCC exited with code 0 and the owned server stopped through its console. This proves those callbacks; it does not claim live gameplay coverage for every plugin. UMPK `0.9.0-beta.6` package metadata identifies source commit `a867a06e131d997e2fe2ef58801a8dd1f67af856`; DMCBK preview.7 identifies `dc9b23477ebbb7852b03d67273e82c259d25560d`.

After those tests, MCC commit `2d40c7a1a82b85aa9b26c4d3e81d8d80b61cbc30` added the default publisher on `feat/mcc-2.0`. A second clean build passed all 918 CLI tests, owned-source formatting, localization, documentation checks and the documentation build. A fresh runtime installed AutoFishing from the public marketplace without an add command. Explicit removal survived restart, and an existing custom registry remained unchanged byte for byte. Automatic updates start off.

## Dependency update, 2026-10-11

The `chore/umpk-beta7-dmcbk-preview8` branch uses published DMCBK `0.1.0-preview.8` and UMPK `0.9.0-beta.7`. Linux x64 validation used .NET SDK `10.0.401`. All 25 plugin versions received a patch bump, and their compatibility floors now require these library releases. LlmTest requires `llm-core` `^0.2.3`; Alerts uses the same range for its optional provider.

| Check | Result |
| --- | --- |
| Release build | Zero warnings and errors |
| Plugin tests and count gate | 773 passed, zero failed or skipped |
| Release tooling tests | 19 passed |
| Formatting and manifest validation | Passed |
| Rehearsal archives | All 25 passed verification |
| Independent authoring templates | Compiled and mixed examples built from NuGet packages |
| Self-contained Linux x64 archive host | All 25 loaded, activated and handled a scripted session |
| MCP HTTP authentication | Rejected unauthenticated access and completed authenticated initialization |

The session used in-memory protocol frames, with external AI-provider calls disabled. These results establish local Linux x64 behavior; the PR workflow runs the other native and cross-build targets. Rehearsal archives and catalogue records remain outside Git under `/tmp/mcc-marketplace-update-20261011`. The checked-in published catalogue and its history are unchanged. Plugin publication follows merge and successful master validation.
