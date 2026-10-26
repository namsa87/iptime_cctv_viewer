param(
 [string]$OutputDirectory = 'dist\Release',
 [switch]$ModernPtz,
 [string]$SdkDirectory = $env:C500G_SDK_DIRECTORY
)
$ErrorActionPreference = 'Stop'
$taskRoot = $PSScriptRoot
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (!(Test-Path -LiteralPath $compiler)) { throw '.NET Framework 4.x x64 C# compiler is required.' }
$output = if ([IO.Path]::IsPathRooted($OutputDirectory)) { $OutputDirectory } else { Join-Path $taskRoot $OutputDirectory }
if ($ModernPtz) {
 $nativeBridge = Join-Path $taskRoot 'native\modern-bin\C500G.ModernIoTBridge.dll'
 if (!(Test-Path -LiteralPath $nativeBridge)) { throw 'Build native\build-modern-native.ps1 first.' }
 if ([string]::IsNullOrWhiteSpace($SdkDirectory)) { throw 'Specify -SdkDirectory or C500G_SDK_DIRECTORY. External SDK files are not included.' }
 $sdkFolder = (Resolve-Path -LiteralPath $SdkDirectory).Path
 if (!(Test-Path -LiteralPath (Join-Path $sdkFolder 'IOT_LIB.dll'))) { throw 'The selected SDK directory does not contain IOT_LIB.dll.' }
}
[IO.Directory]::CreateDirectory($output) | Out-Null
$sources = (Get-ChildItem -LiteralPath (Join-Path $taskRoot 'src') -Filter '*.cs').FullName
$arguments = @('/nologo','/target:winexe','/platform:x64','/optimize+','/utf8output',('/out:' + (Join-Path $output 'C500GDesk.exe')),'/r:System.dll','/r:System.Core.dll','/r:System.Drawing.dll','/r:System.Windows.Forms.dll','/r:System.Security.dll','/r:System.Xml.dll','/r:System.Net.Http.dll','/r:System.Web.Extensions.dll') + $sources
& $compiler $arguments
if ($LASTEXITCODE -ne 0) { throw ('Build failed: ' + $LASTEXITCODE) }
if ($ModernPtz) {
 Copy-Item -LiteralPath $nativeBridge -Destination (Join-Path $output 'C500G.ModernIoTBridge.dll') -Force
 [IO.File]::WriteAllText((Join-Path $output 'modern-sdk-path.txt'), $sdkFolder, [Text.UTF8Encoding]::new($false))
}
Write-Output (Join-Path $output 'C500GDesk.exe')
