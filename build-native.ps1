param([string]$Zig = "$PSScriptRoot/tmp/toolchain/ziglang/zig.exe")
$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot
New-Item -Type Directory -Force dist,tmp/native-build | Out-Null
$units = (Get-ChildItem src/native/playfair/*.c).FullName
& $Zig cc -shared -O2 -fno-strict-aliasing -fwrapv src/native/fairplay.c @units -o tmp/native-build/iscreenx-fp.dll
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
Copy-Item tmp/native-build/iscreenx-fp.dll dist/iscreenx-fp.dll -Force
Copy-Item src/native/playfair/LICENSE.md dist/PlayFair-LICENSE.md -Force
Copy-Item src/native/SOURCE.txt dist/ThirdParty.txt -Force
