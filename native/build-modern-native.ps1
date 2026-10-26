param(
 [string]$CompilerRoot = $env:C500G_MSVC_ROOT,
 [string]$WindowsSdkRoot = $env:C500G_WINDOWS_SDK_ROOT,
 [string]$WindowsSdkVersion = '10.0.19041.0'
)
$ErrorActionPreference = 'Stop'
if ([string]::IsNullOrWhiteSpace($CompilerRoot) -or [string]::IsNullOrWhiteSpace($WindowsSdkRoot)) {
 throw 'Specify -CompilerRoot (MSVC 14.29.30133) and -WindowsSdkRoot, or the corresponding C500G_MSVC_ROOT/C500G_WINDOWS_SDK_ROOT environment variables.'
}
$CompilerRoot = (Resolve-Path -LiteralPath $CompilerRoot).Path
$WindowsSdkRoot = (Resolve-Path -LiteralPath $WindowsSdkRoot).Path
$compilerBin = Join-Path $CompilerRoot 'bin\Hostx64\x64'
$compiler = Join-Path $compilerBin 'cl.exe'
$includes = @((Join-Path $CompilerRoot 'include'))
foreach ($part in @('ucrt','shared','um')) { $includes += Join-Path $WindowsSdkRoot ('Include\' + $WindowsSdkVersion + '\' + $part) }
$libs = @((Join-Path $CompilerRoot 'lib\x64'))
foreach ($part in @('ucrt','um')) {
 $lib = Join-Path $WindowsSdkRoot ('Lib\' + $WindowsSdkVersion + '\' + $part + '\x64')
 if (!(Test-Path -LiteralPath $lib)) { $lib = Join-Path $WindowsSdkRoot ($part + '\x64') }
 $libs += $lib
}
foreach ($required in (@($compiler) + $includes + $libs)) { if (!(Test-Path -LiteralPath $required)) { throw ('Required build input is missing: ' + $required) } }
$outRoot = Join-Path $PSScriptRoot 'modern-bin'
[IO.Directory]::CreateDirectory($outRoot) | Out-Null
$previousInclude=$env:INCLUDE; $previousLib=$env:LIB; $previousPath=$env:PATH
try {
 $env:INCLUDE = $includes -join ';'; $env:LIB = $libs -join ';'; $env:PATH = $compilerBin + ';' + $previousPath
 & $compiler /nologo /LD /MD /O2 /EHsc /std:c++14 /utf-8 /D_ITERATOR_DEBUG_LEVEL=0 /W4 (Join-Path $PSScriptRoot 'ModernIoTBridge.cpp') ('/Fo'+(Join-Path $outRoot 'ModernIoTBridge.obj')) /link ('/OUT:'+(Join-Path $outRoot 'C500G.ModernIoTBridge.dll')) ('/IMPLIB:'+(Join-Path $outRoot 'C500G.ModernIoTBridge.lib'))
 if ($LASTEXITCODE -ne 0) { throw 'Native bridge compilation failed.' }
} finally { $env:INCLUDE=$previousInclude; $env:LIB=$previousLib; $env:PATH=$previousPath }
Write-Output (Join-Path $outRoot 'C500G.ModernIoTBridge.dll')
