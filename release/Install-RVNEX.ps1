$ErrorActionPreference = 'Stop'

$packagePath = Join-Path $PSScriptRoot 'RVNEX_1.0.0.0_x64.msix'
$runtimePath = Join-Path $PSScriptRoot 'Dependencies\x64\Microsoft.WindowsAppRuntime.2.msix'

foreach ($path in @($packagePath, $runtimePath)) {
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
        throw "Required release file is missing: $path"
    }
}

Add-Type -AssemblyName System.IO.Compression.FileSystem
$archive = [System.IO.Compression.ZipFile]::OpenRead($packagePath)
try {
    $hasSignature = $null -ne $archive.GetEntry('AppxSignature.p7x')
}
finally {
    $archive.Dispose()
}

if (-not $hasSignature) {
    throw 'This staging MSIX is unsigned. Install the approved publisher certificate and use a signed release package before deployment.'
}

try {
    Add-AppxPackage -Path $packagePath -DependencyPath $runtimePath -ForceApplicationShutdown
}
catch {
    throw "RVNEX installation failed. Confirm that the package publisher certificate is trusted and matches the manifest publisher. $($_.Exception.Message)"
}

Write-Output 'RVNEX is installed. Launch it from the Windows Start menu.'
