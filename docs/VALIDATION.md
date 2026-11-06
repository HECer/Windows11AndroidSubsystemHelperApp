# Validation record — 0.1.0 candidate

Recorded November 6, 2026 on Windows x64 using .NET SDK 10.0.203.

- Release build: succeeds with warnings treated as errors.
- Core regression suite: 35 cases cover download integrity and cache replacement, malformed/traversing/link archives, journal recovery, package/endpoint validation, ADB targeting and failure responses, text/binary APK manifests, ownership-aware removal, reboot boundaries and explicit feature restoration.
- Desktop test: constructs and renders Setup, Android apps and Remove on an STA thread without running installation.
- Opt-in integration: downloaded the exact pinned WSA and platform-tools artifacts, verified both SHA-256 hashes, extracted the real archives through the production extractor, checked WSA identity/dependencies/settings entry point, and ran platform-tools 37.0.2 with a separate localhost ADB server. Passed; the server was stopped and extraction directories were cleaned.
- NuGet vulnerability lookup: no known vulnerable dependencies reported at validation time. This is not an independent security audit of WSA or the helper.
- PowerShell scripts: syntax parsed successfully.
- Distribution: Windows x64 self-contained single-file executables, ZIP and SHA-256 sidecar; downloaded WSA and platform-tools are excluded. Runtime and SharpCompress license notices are included.

**Not verified:** privileged feature changes, distinct-admin UAC, WSA registration/boot, reboot/sign-in recovery, real APK installation/removal, Explorer associations and complete rollback on a disposable clean machine. These require the scenarios in [ACCEPTANCE.md](ACCEPTANCE.md). The development computer is low on disk space and is not an appropriate machine for a destructive fresh installation test. No WSA installation or Windows feature/policy change was performed here.

Keep the GitHub binary release as a draft until the clean-machine checklist is complete. The public source repository and local candidate package are available for review. The helper is unsigned; production distribution needs a maintainer decision about signing and update/support policy.

The installed `audit-provenance` skill was applied read-only to the release ZIP. Results: located **UNKNOWN**, verified **UNKNOWN**, trusted **UNKNOWN**, scan complete **NO**. Its stated unknowns are: “The scan was bounded and did not cover the whole asset”, “Only the first 16777216 bytes were inspected”, and “No conforming verifier was supplied”. No trust-anchor policy was supplied; proprietary AI watermark detection is unavailable/unverifiable. No marks or metadata were removed, and no human-authorship claim follows from this audit. C2PA media provenance is not a substitute for Windows executable code signing. No general AI-disclosure requirement was established by this check; any applicable publication policy still governs disclosure.
