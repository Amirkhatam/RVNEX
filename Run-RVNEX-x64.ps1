$ErrorActionPreference = 'Stop'

$solutionRoot = $PSScriptRoot
$projectPath = Join-Path $solutionRoot 'RVNEX\RVNEX.csproj'
$outputDirectory = Join-Path $solutionRoot 'RVNEX\bin\x64\Debug\net8.0-windows10.0.19041.0\win-x64'
$manifestPath = Join-Path $outputDirectory 'AppxManifest.xml'
$dotnetCommand = Get-Command dotnet -ErrorAction SilentlyContinue
$dotnetPath = if ($dotnetCommand) { $dotnetCommand.Source } else { Join-Path $env:ProgramFiles 'dotnet\dotnet.exe' }

if (-not (Test-Path -LiteralPath $dotnetPath)) {
    throw "The .NET SDK executable was not found at '$dotnetPath'."
}

$buildArguments = @(
    'publish', $projectPath,
    '--configuration', 'Debug',
    '-p:Platform=x64',
    '-p:GenerateAppxPackageOnBuild=true',
    '--no-restore'
)

& $dotnetPath @buildArguments
if ($LASTEXITCODE -ne 0) {
    exit $LASTEXITCODE
}

if (-not (Test-Path -LiteralPath $manifestPath)) {
    throw "The x64 MSIX manifest was not produced at '$manifestPath'."
}

Add-AppxPackage -Register $manifestPath -ErrorAction Stop

[xml]$manifest = Get-Content -LiteralPath $manifestPath
$namespaceManager = [System.Xml.XmlNamespaceManager]::new($manifest.NameTable)
$namespaceManager.AddNamespace('appx', 'http://schemas.microsoft.com/appx/manifest/foundation/windows10')
$identity = $manifest.SelectSingleNode('/appx:Package/appx:Identity', $namespaceManager)
$applicationEntry = $manifest.SelectSingleNode('/appx:Package/appx:Applications/appx:Application', $namespaceManager)
if (-not $identity -or -not $applicationEntry) {
    throw "The generated MSIX manifest does not contain a package identity and application entry."
}

$package = Get-AppxPackage -Name $identity.GetAttribute('Name') |
    Where-Object { $_.Version.ToString() -eq $identity.GetAttribute('Version') } |
    Select-Object -First 1
if (-not $package) {
    throw "RVNEX package registration was not found after registration."
}

$appUserModelId = '{0}!{1}' -f $package.PackageFamilyName, $applicationEntry.GetAttribute('Id')
Start-Process -FilePath 'explorer.exe' -ArgumentList "shell:AppsFolder\$appUserModelId"
Write-Output "Launched packaged RVNEX x64 Debug: $appUserModelId"
