# WSA Helper 0.1.0 — experimental candidate

Native Windows 11 x64 setup, APK management and removal suite.

- WsaSetup.exe checks prerequisites, configures Windows virtualization/sideloading settings with UAC, downloads hash-pinned community WSA and Google platform-tools, installs bundled Microsoft framework dependencies, and resumes after a required restart.
- WsaHelper.exe registers with Explorer Open with for automatic APK installation. Launch it normally to list user-installed Android packages, install APKs, uninstall using an APK's package identity, or uninstall multiple selected apps.
- WsaRemove.exe removes the helper-owned installation and offers explicit restoration of recorded Windows machine settings. Existing WSA installations and shared Microsoft framework packages are preserved.

Extract the ZIP and run WsaSetup.exe without administrator elevation. Review the bundled README and upstream terms before installing. Verify the ZIP against the attached SHA-256 sidecar. Windows/Android approval prompts and firmware virtualization settings can require user interaction.

Microsoft retired WSA in March 2025. The pinned WSABuilds LTS 8 package has no Google Play, Amazon Appstore or root. WSA and Google's platform-tools are downloaded directly by the installer rather than redistributed in this ZIP. This release is unsigned and unsupported by Microsoft.

Validation: 35 core regression cases and the desktop rendering test pass; the opt-in test passed against the real hash-verified WSA and ADB archives, and all three self-contained entry points passed startup checks. No known vulnerable NuGet dependencies were reported at validation time.

**Draft: do not treat this as a production-tested release.** Clean-machine privileged installation, WSA boot, reboot recovery, real APK/Explorer operations and destructive rollback still require the bundled ACCEPTANCE.md checklist. Removing a helper-owned WSA deletes all Android apps/data in that subsystem. Code signing and runtime compatibility remain release-maintainer responsibilities.
