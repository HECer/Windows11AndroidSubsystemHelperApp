# WSA Helper 0.1.0 — experimental pre-release

Native Windows 11 x64 setup, APK management and removal suite.

Maintained by [Hermann (HECer)](https://github.com/HECer). This public pre-release is available for evaluation; acceptance testing is incomplete.

Prerequisite rollback now records whether the original sideloading policy existed, preserves DWORD bit patterns and recovers interrupted baseline recording. Twelve isolated production-script tests cover these paths without changing real Windows features; actual elevated DISM and reboot acceptance remain pending.

The three screens now share a polished sidebar, coordinated light/dark themes, emerald accents and a matching application icon. The library includes search, selection counts and clear disconnected/empty states. Progress and errors stay in the window, with expandable activity details. Destructive actions retain confirmation. The desktop test renders 28 page/state/theme/size combinations and checks filtering, multi-selection and compact-window bulk actions.

- WsaSetup.exe checks prerequisites, configures Windows virtualization/sideloading settings with UAC, downloads hash-pinned community WSA and Google platform-tools, installs bundled Microsoft framework dependencies, and resumes after a required restart.
- WsaHelper.exe registers with Explorer Open with for automatic APK installation. Launch it normally to list user-installed Android packages, install APKs, uninstall using an APK's package identity, or uninstall multiple selected apps.
- WsaRemove.exe removes the helper-owned installation and offers explicit restoration of recorded Windows machine settings. Existing WSA installations and shared Microsoft framework packages are preserved.

Extract the ZIP and run WsaSetup.exe without administrator elevation. Review the bundled README and upstream terms before installing. Verify the ZIP against the attached SHA-256 sidecar. Windows/Android approval prompts and firmware virtualization settings can require user interaction.

Microsoft retired WSA in March 2025. The pinned WSABuilds LTS 8 package has no Google Play, Amazon Appstore or root. WSA and Google's platform-tools are downloaded directly by the installer rather than redistributed in this ZIP. This release is unsigned and unsupported by Microsoft.

Validation: 72 core regression cases and three desktop tests pass; the opt-in test passed against the real hash-verified WSA and ADB archives. The real existing-WSA setup/removal preservation test also passed. Packaged setup and removal were exercised against the existing subsystem, including Windows registration, shortcuts, delayed executable cleanup and added-file preservation. The manager now waits for debugging approval and continues automatically, with a cancellable two-minute deadline. No known vulnerable NuGet dependencies were reported at validation time. Real-APK testing remains blocked by Android's `unauthorized` debugging state after the authorized WSA restart; no Android apps or debugging keys were changed.

**Experimental pre-release: do not treat this as a production-tested release.** Clean-machine privileged installation, WSA boot, reboot recovery, real APK/Explorer operations and destructive rollback still require the bundled ACCEPTANCE.md checklist. Removing a helper-owned WSA deletes all Android apps/data in that subsystem. Code signing and runtime compatibility remain release-maintainer responsibilities.

Fresh deployment now uses a dedicated elevated worker, a protected Program Files runtime, a second archive integrity check and a protected ownership journal for removal. It requires the same administrator account at UAC and rejects separate administrator credentials or an elevated SID changed by Administrator protection. The new privileged path still requires clean-machine testing. The tested existing-WSA flow does not validate fresh installation; this pre-release is not a finished, fully verified automatic WSA installer.
