# Third-party software and terms

The helper's source code is MIT licensed. This does not grant rights to Microsoft, Google, Android or community runtime binaries.

- **SharpCompress 0.50.8**: MIT; https://github.com/adamhathcock/sharpcompress . Its transitive compression dependencies retain their own licenses. NuGet package license metadata is the authoritative inventory. SharpCompress is bundled in the self-contained helper executable.
- **Microsoft .NET 10 / Windows Desktop runtime**: MIT and associated third-party notices; https://github.com/dotnet/runtime/blob/main/LICENSE.TXT and https://github.com/dotnet/runtime/blob/main/THIRD-PARTY-NOTICES.TXT . Runtime components are included by `dotnet publish --self-contained` and runtime license files are kept in release output.
- **Windows Subsystem for Android**: Microsoft proprietary software and included open-source licenses. The downloaded archive contains upstream license notices; these remain in the installed directory. Microsoft discontinued WSA support and distribution through the Store. The helper does not redistribute or relicense WSA.
- **WSABuilds / MagiskOnWSALocal**: https://github.com/MustardChef/WSABuilds . Build/installer source has its own license (including AGPL where indicated). This helper is independently implemented and does not copy or run their installer scripts. Community package availability and users' rights to use the underlying runtime are governed by its upstream terms.
- **Android SDK platform-tools 37.0.2**: downloaded directly from Google at setup time, governed by the Android SDK License Agreement, https://developer.android.com/studio/terms . The SDK's `NOTICE.txt` and associated files are preserved in the private platform-tools folder. Review these terms before choosing Install.

No root, Magisk, Google Play services, Amazon Appstore, or third-party APK is bundled or automatically installed. Trademarks belong to their respective owners. Any runtime or component's availability may change; the helper rejects downloads that no longer match its pinned catalog.
