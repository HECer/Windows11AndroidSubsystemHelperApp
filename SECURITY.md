# Security

Report suspected defects privately through the repository's GitHub Security Advisories if enabled. Do not post private APKs, credentials, local logs containing sensitive paths or app data in public issues.

The runtime is a discontinued Microsoft product distributed by a community project. The helper's integrity pins do not make WSA supported or guarantee its safety. The helper does not elevate APK/ADB operations or execute downloaded installation scripts. Only a fixed embedded PowerShell feature configuration/restoration script requests UAC. Its machine journal is protected by Windows registry permissions.

The local journal and APK request inbox have the same trust boundary as the user's Windows account; processes already executing as that user can install APKs through the same manager. ADB listens privately on loopback port 5039 and targets only the specified loopback WSA endpoint. Android debugging approval is required. Do not expose WSA's debugger to untrusted networks.
