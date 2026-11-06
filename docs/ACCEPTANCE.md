# Release acceptance

Version 0.1.0 is an experimental release candidate. The following destructive/privileged scenarios must be verified on disposable Windows 11 x64 installations before production release. Do not run them on a daily-use computer as a smoke test.

- [ ] Windows 11 Home: firmware virtualization on, VMP off, no WSA/ADB/.NET desktop runtime. Run extracted WsaSetup.exe, approve UAC, restart, verify sign-in resume, framework registration and WSA boot.
- [ ] Windows 11 Pro with VMP already enabled: verify existing feature state is preserved.
- [ ] Non-administrator Windows account: supply a separate administrator at UAC and verify WSA/framework registration remains in the original user's account.
- [ ] Firmware virtualization off, low RAM, insufficient disk, pending restart, offline downloads, GitHub rate/unavailability and failed hash: actionable error; no unjournaled destructive change.
- [ ] Decline UAC, cancel download, cancel extraction, close during DISM, interrupt before/after WSA registration. Resume or Remove leaves a correct recoverable state.
- [ ] Existing Store/community WSA: reuse without registration, update or data deletion; Remove preserves it and its data.
- [ ] Fresh WSA: follow Developer mode and debugging-approval prompts. Verify cold-start reconnection and unauthorized/offline handling.
- [ ] Right-click APK → Open with registered app. Also browse directly to installed WsaHelper.exe; file paths with spaces, Unicode and apostrophes. APK automatically installs; incompatible update clearly fails.
- [ ] Repeat Explorer Open with while the manager is already open or busy; each file is processed once, and reboot recovery handles pending files.
- [ ] Start manager without a file: list user-installed Android packages. Filter, Ctrl/Shift select, select all, cancel bulk removal, remove several and report partial failures.
- [ ] Uninstall from real compiled binary-manifest APK; malformed and split APK files fail safely.
- [ ] Verify an attached physical phone/default-port ADB session is untouched. Only loopback WSA target and private ADB port 5039 are used.
- [ ] Remove through WsaRemove.exe and Windows Installed apps: warn about Android data, unregister owned WSA, remove file registration/tools/shortcuts, close to clean helper binaries, keep recovery journal.
- [ ] Optional feature restore: restore prior developer-policy value (including missing value) and VMP state; restart when needed. Existing enabled VMP remains enabled. Verify other software's dependencies before choosing restore.
- [ ] Subsequent setup/removal cycles and machine-policy restore after an earlier removal with settings retained.
- [ ] Windows dark/light/high-contrast themes, 125–200% scaling, keyboard navigation, screen reader names, and small screen sizing.

Record Windows build, edition, test account privilege, source archive hashes, APK identities and each result in the release notes. Never upload credentials or private APKs. Code signing, a tested runtime-source update process and a completed checklist are release-maintainer responsibilities.
