param([Parameter(Mandatory)][string]$OutputDirectory, [string]$SdkRoot = $env:ANDROID_HOME)
$ErrorActionPreference = 'Stop'
if (Test-Path -LiteralPath $OutputDirectory) { throw 'Choose a new, task-owned output directory.' }
$tools = Join-Path $SdkRoot 'build-tools\36.0.0'
$androidJar = Join-Path $SdkRoot 'platforms\android-33\android.jar'
$java = Join-Path $env:JAVA_HOME 'bin\java.exe'
$keytool = Join-Path $env:JAVA_HOME 'bin\keytool.exe'
foreach ($file in @((Join-Path $tools 'aapt2.exe'), (Join-Path $tools 'zipalign.exe'), (Join-Path $tools 'lib\apksigner.jar'), $androidJar, $java, $keytool)) {
    if (!(Test-Path -LiteralPath $file)) { throw "Required Android build tool is missing: $file" }
}
New-Item -ItemType Directory -Path $OutputDirectory | Out-Null
$id = 't' + [guid]::NewGuid().ToString('N')
$env:WSA_HELPER_SMOKE_KEY_PASSWORD = [guid]::NewGuid().ToString('N')
$key = Join-Path $OutputDirectory 'test-only-signing-key.p12'
$apks = @()
try {
    & $keytool -genkeypair -keystore $key -storetype PKCS12 -alias smoke -keyalg RSA -keysize 2048 -validity 2 -dname 'CN=WSA Helper disposable validation' -storepass:env WSA_HELPER_SMOKE_KEY_PASSWORD -keypass:env WSA_HELPER_SMOKE_KEY_PASSWORD
    if ($LASTEXITCODE) { throw 'Test-only key generation failed.' }
    foreach ($suffix in @('single', 'bulkone', 'bulktwo')) {
        $package = "io.github.wsahelper.validation.$id.$suffix"
        $manifest = Join-Path $OutputDirectory "$suffix-manifest.xml"
        $unsigned = Join-Path $OutputDirectory "$suffix-unsigned.apk"
        $aligned = Join-Path $OutputDirectory "$suffix-aligned.apk"
        $signed = Join-Path $OutputDirectory "WSA validation $suffix ü.apk"
        @"
<manifest xmlns:android="http://schemas.android.com/apk/res/android" package="$package" android:versionCode="1" android:versionName="1.0">
  <uses-sdk android:minSdkVersion="23" android:targetSdkVersion="33" />
  <application android:label="WSA Helper disposable validation" android:hasCode="false" android:allowBackup="false" />
</manifest>
"@ | Set-Content -LiteralPath $manifest -Encoding utf8
        & (Join-Path $tools 'aapt2.exe') link -I $androidJar --manifest $manifest -o $unsigned
        if ($LASTEXITCODE) { throw 'Compiling test manifest failed.' }
        & (Join-Path $tools 'zipalign.exe') -f 4 $unsigned $aligned
        if ($LASTEXITCODE) { throw 'APK alignment failed.' }
        & $java -jar (Join-Path $tools 'lib\apksigner.jar') sign --ks $key --ks-key-alias smoke --ks-pass env:WSA_HELPER_SMOKE_KEY_PASSWORD --key-pass env:WSA_HELPER_SMOKE_KEY_PASSWORD --out $signed $aligned
        if ($LASTEXITCODE) { throw 'Signing test APK failed.' }
        & $java -jar (Join-Path $tools 'lib\apksigner.jar') verify $signed
        if ($LASTEXITCODE) { throw 'Test APK signature verification failed.' }
        $apks += [IO.Path]::GetFullPath($signed)
    }
    @{ schema = 1; runId = $id; apks = $apks } | ConvertTo-Json -Depth 3 | Set-Content -LiteralPath (Join-Path $OutputDirectory 'fixtures.json') -Encoding utf8
    Write-Output (Join-Path $OutputDirectory 'fixtures.json')
} finally {
    [Environment]::SetEnvironmentVariable('WSA_HELPER_SMOKE_KEY_PASSWORD', $null, 'Process')
    # This key has no purpose beyond these disposable fixtures; do not retain it.
    if (Test-Path -LiteralPath $key) { Remove-Item -LiteralPath $key }
}
