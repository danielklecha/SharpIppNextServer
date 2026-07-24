$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Definition
if (-not $scriptDir) {
    $scriptDir = $PSScriptRoot
}
Push-Location $scriptDir
try {
    # Publish the application
    Write-Host "Publishing IppPrinter..." -ForegroundColor Green
    dotnet publish ../IppPrinter/IppPrinter.csproj -c Release -r win-x64 --self-contained false


    # Find ISCC.exe
    $isccPath = "C:\Program Files\Inno Setup 7\ISCC.exe"

    if (-not (Test-Path $isccPath)) {
        Write-Error "Inno Setup compiler (ISCC.exe) was not found at $isccPath."
        Write-Host "Please install Inno Setup 7 and run this script again." -ForegroundColor Yellow
        exit 1
    }

    # Compile Installer
    Write-Host "Compiling installer using: $isccPath..." -ForegroundColor Green
    & $isccPath installer.iss
    Write-Host "Installer compiled successfully! Check Setup/Output/IppPrinterSetup.exe" -ForegroundColor Green
} finally {
    Pop-Location
}
