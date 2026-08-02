$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Definition
if (-not $scriptDir) {
    $scriptDir = $PSScriptRoot
}
Push-Location $scriptDir
try {
    # Clean previous build artifacts and publish the application as WinExe for installer
    Write-Host "Cleaning and publishing IppPrinter as WinExe..." -ForegroundColor Green
    dotnet clean ../IppPrinter/IppPrinter.csproj -c Release
    dotnet publish ../IppPrinter/IppPrinter.csproj -c Release -r win-x64 --self-contained false -p:OutputType=WinExe

    # Find ISCC.exe dynamically (checking real installation paths first, then PATH)
    $isccPath = $null
    $candidatePaths = @(
        "C:\Program Files\Inno Setup 7\ISCC.exe",
        "C:\Program Files (x86)\Inno Setup 7\ISCC.exe",
        "C:\Program Files\Inno Setup 6\ISCC.exe",
        "C:\Program Files (x86)\Inno Setup 6\ISCC.exe"
    )
    foreach ($path in $candidatePaths) {
        if (Test-Path $path) {
            $isccPath = $path
            break
        }
    }

    if (-not $isccPath) {
        $cmdIscc = Get-Command "ISCC.exe" -ErrorAction SilentlyContinue
        if ($cmdIscc) {
            $isccPath = $cmdIscc.Source
        }
    }

    if (-not $isccPath) {
        Write-Error "Inno Setup compiler (ISCC.exe) was not found in PATH or standard installation directories."
        Write-Host "Please install Inno Setup 7 (or Inno Setup 6) and run this script again." -ForegroundColor Yellow
        exit 1
    }

    # Compile Installer
    Write-Host "Compiling installer using: $isccPath..." -ForegroundColor Green
    & $isccPath installer.iss
    Write-Host "Installer compiled successfully! Check Setup/Output/IppPrinterSetup.exe" -ForegroundColor Green
}
finally {
    Pop-Location
}
