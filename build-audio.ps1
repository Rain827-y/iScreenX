param(
    [string]$Zig = $(if (Test-Path "$PSScriptRoot/tmp/toolchain/ziglang/zig.exe") { "$PSScriptRoot/tmp/toolchain/ziglang/zig.exe" } else { 'zig' }),
    [string]$OutputDirectory = "$PSScriptRoot/dist"
)
$ErrorActionPreference='Stop'
Set-Location $PSScriptRoot
$root='3rdparty/fdk-aac'
if (!(Test-Path "$root/libAACdec/include/aacdecoder_lib.h")) {
    throw 'Initialize dependencies with git submodule update --init'
}
New-Item -Type Directory -Force $OutputDirectory,tmp/native-build | Out-Null
$modules='AACdec','MpegTPDec','FDK','SYS','PCMutils','SACdec','SBRdec','ArithCoding','DRCdec'
$flags=@('-shared','-O2','-g0','-fwrapv','-fno-exceptions','-fno-rtti','-Wno-everything')
foreach($module in $modules) {
    $flags+="-I$root/lib$module/include"
    $flags+=(Get-ChildItem "$root/lib$module/src/*.cpp" | ForEach-Object { "$root/lib$module/src/$($_.Name)" })
}
$flags+=@('src/native/audio-decoder.cpp','-o','tmp/native-build/iscreenx-aac.dll')
$flags | Set-Content -Encoding ascii tmp/native-build/aac.rsp
& $Zig c++ '@tmp/native-build/aac.rsp'
if($LASTEXITCODE -ne 0){exit $LASTEXITCODE}
Copy-Item tmp/native-build/iscreenx-aac.dll $OutputDirectory -Force
Copy-Item "$root/NOTICE" "$OutputDirectory/FDK-AAC-NOTICE.txt" -Force
