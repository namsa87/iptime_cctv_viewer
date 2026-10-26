param([string[]]$Suites=@('PublicConfigurationTests','CloudOfflineTests','ReconnectTests','AutoConnectTests','AutoLoginTests','AccountVideoTests','AccountHoldTests','IptimePtzTests'))
$ErrorActionPreference='Stop'
$taskRoot=Split-Path -Parent $PSScriptRoot
$compiler=Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$sources=(Get-ChildItem -LiteralPath (Join-Path $taskRoot 'src') -Filter '*.cs').FullName
$known=@('PublicConfigurationTests','CloudOfflineTests','ReconnectTests','AutoConnectTests','AutoLoginTests','AccountVideoTests','AccountHoldTests','IptimePtzTests')
$outRoot=Join-Path $taskRoot 'artifacts\tests'
[IO.Directory]::CreateDirectory($outRoot) | Out-Null
[IO.Directory]::CreateDirectory((Join-Path $outRoot 'native-signing')) | Out-Null
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'native-signing\xiaotun-ptz-vectors.json') -Destination (Join-Path $outRoot 'native-signing\xiaotun-ptz-vectors.json') -Force
foreach($suite in $Suites){
 if($suite -notin $known){throw ('Unknown offline suite: '+$suite)}
 $test=Join-Path $PSScriptRoot ($suite+'.cs')
 $main=if($suite -eq 'CloudOfflineTests'){$suite}else{'C500GDesktop.Tests.'+$suite}
 $target=Join-Path $outRoot ($suite+'.exe')
 $compilerArgs=@('/nologo','/target:exe','/platform:x64','/utf8output','/r:System.dll','/r:System.Core.dll','/r:System.Drawing.dll','/r:System.Windows.Forms.dll','/r:System.Security.dll','/r:System.Xml.dll','/r:System.Net.Http.dll','/r:System.Web.Extensions.dll',('/main:'+$main),('/out:'+$target),$test)+$sources
 & $compiler $compilerArgs
 if($LASTEXITCODE -ne 0){throw ('Compilation failed: '+$suite)}
 & $target
 if($LASTEXITCODE -ne 0){throw ('Test failed: '+$suite)}
}
