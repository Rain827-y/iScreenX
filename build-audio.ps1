param([string]$Zig = "$PSScriptRoot/tmp/toolchain/ziglang/zig.exe")
$ErrorActionPreference='Stop'
Set-Location $PSScriptRoot
$revision='716f4394641d53f0d79c9ddac3fa93b03a49f278'
$root="tmp/aac-src/fdk-aac-$revision"
New-Item -Type Directory -Force dist,tmp/native-build | Out-Null
if (!(Test-Path "$root/libAACdec")) {
    $archive='tmp/fdk.zip'
    Invoke-WebRequest -UseBasicParsing "https://codeload.github.com/mstorsjo/fdk-aac/zip/$revision" -OutFile $archive
    if ((Get-FileHash $archive).Hash -ne 'C8DB4B4C335D0A6711EA82B0A577AB62FC1578187C391CFE02B2C64AB29F6D2A') { throw 'FDK AAC checksum mismatch' }
    Expand-Archive $archive tmp/aac-src -Force
}
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
Copy-Item tmp/native-build/iscreenx-aac.dll dist -Force
Copy-Item "$root/NOTICE" dist/FDK-AAC-NOTICE.txt -Force
