param(
    [string]$Zig = $(if (Test-Path "$PSScriptRoot/tmp/toolchain/ziglang/zig.exe") { "$PSScriptRoot/tmp/toolchain/ziglang/zig.exe" } else { 'zig' }),
    [string]$OutputDirectory = "$PSScriptRoot/dist"
)
$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot
$iTouchPackage = '3rdparty/iTouch/dist/com.itouch.remote_1.1.1_iphoneos-arm.deb'
if (!(Test-Path -LiteralPath $iTouchPackage -PathType Leaf)) {
    throw 'Initialize iTouch with git submodule update --init'
}
New-Item -ItemType Directory -Path $OutputDirectory,tmp -Force | Out-Null
foreach ($script in 'build-native.ps1','build-audio.ps1') {
    & "$PSScriptRoot/$script" -Zig $Zig -OutputDirectory $OutputDirectory
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
}
$version = '1.0.2903.40'
$sdk = "tmp/webview2-$version"
if (!(Test-Path "$sdk/lib/net462/Microsoft.Web.WebView2.WinForms.dll")) {
    $archive = "tmp/webview2-$version.zip"
    Invoke-WebRequest -UseBasicParsing "https://api.nuget.org/v3-flatcontainer/microsoft.web.webview2/$version/microsoft.web.webview2.$version.nupkg" -OutFile $archive
    if ((Get-FileHash $archive).Hash -ne 'EF128016DD1E51C59178C827ED5B8AA3322C57AFA8675D930F8109505542AD74') { throw 'WebView2 SDK checksum mismatch' }
    Expand-Archive $archive $sdk -Force
}
$assemblies = @("$sdk/lib/net462/Microsoft.Web.WebView2.Core.dll", "$sdk/lib/net462/Microsoft.Web.WebView2.WinForms.dll")
Copy-Item $assemblies $OutputDirectory -Force
Copy-Item "$sdk/runtimes/win-x64/native/WebView2Loader.dll" $OutputDirectory -Force
Copy-Item "$sdk/LICENSE.txt" "$OutputDirectory/WebView2-LICENSE.txt" -Force
Copy-Item "$sdk/NOTICE.txt" "$OutputDirectory/WebView2-NOTICE.txt" -Force
Copy-Item '3rdparty/iTouch/LICENSE' "$OutputDirectory/iTouch-LICENSE.txt" -Force
& "$env:WINDIR\Microsoft.NET\Framework64\v4.0.30319\csc.exe" /nologo /codepage:65001 /platform:x64 /target:winexe /optimize+ "/out:$OutputDirectory/iScreenX.exe" /reference:System.Windows.Forms.dll /reference:System.Drawing.dll /reference:System.Security.dll "/reference:$($assemblies -join ',')" /resource:src\view.html,view.html "/resource:$iTouchPackage,itouch.deb" src\*.cs src\RemoteControl\*.cs
exit $LASTEXITCODE
