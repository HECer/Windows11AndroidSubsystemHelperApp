# App screenshots

These PNGs are rendered from the actual WPF application by the desktop rendering test. The app library uses sample package IDs from an isolated test state. The previews do not establish live WSA connectivity or successful APK acceptance.

The main README automatically selects a light or dark image using the reader's color-scheme preference. All originals were copied without editing; no marks or metadata were removed.

To regenerate the previews on Windows, set `WSA_HELPER_RENDER_DIR` to a local output folder and run:

```powershell
dotnet test tests/WsaHelper.Desktop.Tests/WsaHelper.Desktop.Tests.csproj -c Release --filter FullyQualifiedName~AllScreensConstructAndRenderWithoutRunningSetup
```

Copy the six matching full-size images into this directory. The renderer disables startup actions and uses an isolated state store; it does not install WSA or manipulate Android apps.

## Read-only provenance check

The installed `audit-provenance` skill checked each PNG before publication:

| Images | Located | Verified | Trusted | Scan complete |
| --- | --- | --- | --- | --- |
| Setup, Android apps and Remove, in both themes | NO | UNKNOWN | UNKNOWN | YES |

The verification and trust unknowns state: "No conforming verifier was supplied." No trust-anchor policy was supplied. Proprietary AI watermark detection is unavailable or unverifiable. Absence of a detected mark does not establish human authorship. No general AI-disclosure requirement was established by this check; applicable publication policies still govern disclosure.
