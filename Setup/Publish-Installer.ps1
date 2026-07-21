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
    $isccPath = "iscc"
    if (-not (Get-Command $isccPath -ErrorAction SilentlyContinue)) {
        $possiblePaths = @(
            "C:\Program Files (x86)\Inno Setup 6\ISCC.exe",
            "C:\Program Files\Inno Setup 6\ISCC.exe",
            "C:\Program Files (x86)\Inno Setup 5\ISCC.exe",
            "C:\Program Files\Inno Setup 5\ISCC.exe"
        )
        foreach ($path in $possiblePaths) {
            if (Test-Path $path) {
                $isccPath = $path
                break
            }
        }
    }

    if (-not (Get-Command $isccPath -ErrorAction SilentlyContinue) -and -not (Test-Path $isccPath)) {
        Write-Error "Inno Setup compiler (ISCC.exe) was not found in PATH or standard Program Files locations."
        Write-Host "Please install Inno Setup (e.g., run 'choco install innosetup -y' in an ELEVATED shell) and run this script again." -ForegroundColor Yellow
        exit 1
    }

    # Compile Installer
    Write-Host "Compiling installer using: $isccPath..." -ForegroundColor Green
    & $isccPath installer.iss
    Write-Host "Installer compiled successfully! Check Setup/Output/IppPrinterSetup.exe" -ForegroundColor Green
} finally {
    Pop-Location
}
