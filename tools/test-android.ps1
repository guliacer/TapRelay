[CmdletBinding()]
param(
    [string]$JavaHome = "",
    [string[]]$Tasks = @(':app:testDebugUnitTest', ':app:assembleDebug', ':app:lintDebug')
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$workspaceRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$candidates = if ($JavaHome) { @($JavaHome) } else {
    @($env:JAVA_HOME, (Join-Path $workspaceRoot '.gradle-user\jdks\jdk-21.0.4+7'))
}
$selectedJdk = $null
foreach ($candidate in $candidates) {
    if ([string]::IsNullOrWhiteSpace($candidate)) { continue }
    $releaseFile = Join-Path $candidate 'release'
    if ((Test-Path -LiteralPath $releaseFile -PathType Leaf) -and
        (Test-Path -LiteralPath (Join-Path $candidate 'bin\java.exe') -PathType Leaf) -and
        (Select-String -LiteralPath $releaseFile -Pattern '^JAVA_VERSION="21\.0\.4"$' -Quiet)) {
        $selectedJdk = [IO.Path]::GetFullPath($candidate)
        break
    }
}
if (-not $selectedJdk) {
    throw 'JDK 21.0.4 is required. Install it and pass -JavaHome <jdk-path> or set JAVA_HOME.'
}
$previousJavaHome = $env:JAVA_HOME
try {
    $env:JAVA_HOME = $selectedJdk
    Write-Host "Android JDK: $selectedJdk (21.0.4)"
    Push-Location (Join-Path $workspaceRoot 'android-app')
    try {
        & .\gradlew.bat @Tasks
        if ($LASTEXITCODE -ne 0) { throw "Android checks failed with exit code $LASTEXITCODE" }
    }
    finally { Pop-Location }
}
finally { $env:JAVA_HOME = $previousJavaHome }
