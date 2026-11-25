param(
    [string]$Zig = $(if (Test-Path "$PSScriptRoot/tmp/toolchain/ziglang/zig.exe") { "$PSScriptRoot/tmp/toolchain/ziglang/zig.exe" } else { 'zig' }),
    [string]$OutputDirectory = "$PSScriptRoot/dist"
)
$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot
New-Item -Type Directory -Force $OutputDirectory,tmp/native-build | Out-Null
$lib = '3rdparty/UxPlay/lib'
if (!(Test-Path "$lib/playfair/playfair.h")) {
    throw 'Initialize dependencies with git submodule update --init'
}
$units = (Get-ChildItem "$lib/playfair/*.c").FullName
& $Zig cc -shared -O2 -fno-strict-aliasing -fwrapv "-I$lib" src/native/fairplay.c @units -o tmp/native-build/iscreenx-fp.dll
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
Copy-Item tmp/native-build/iscreenx-fp.dll "$OutputDirectory/iscreenx-fp.dll" -Force
Copy-Item "$lib/playfair/LICENSE.md" "$OutputDirectory/PlayFair-LICENSE.md" -Force
Copy-Item src/native/SOURCE.txt "$OutputDirectory/ThirdParty.txt" -Force
