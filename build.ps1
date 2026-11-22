$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot
New-Item -ItemType Directory -Path dist -Force | Out-Null
& "$env:WINDIR\Microsoft.NET\Framework64\v4.0.30319\csc.exe" /nologo /platform:x64 /target:winexe /optimize+ /out:dist\iScreenX.exe /reference:System.Windows.Forms.dll /reference:System.Drawing.dll /reference:System.Security.dll /resource:src\view.html,view.html src\*.cs
exit $LASTEXITCODE
