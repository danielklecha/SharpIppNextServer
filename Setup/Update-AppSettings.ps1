param(
    [string]$Scheme = "http",
    [string]$AppDir,
    [string]$JobsDir
)

$ErrorActionPreference = "Stop"

try {
    $jsonPath = Join-Path $AppDir "appsettings.Production.json"

    if (Test-Path $jsonPath) {
        Write-Output "Configuring $jsonPath for $Scheme..."
        $config = Get-Content $jsonPath | ConvertFrom-Json

        if ($Scheme -eq "https") {
            $config.Kestrel = @{
                Endpoints = @{
                    Https = @{
                        Url = "https://0.0.0.0:631"
                        Certificate = @{
                            Subject = "IppPrinterSelfSigned"
                            Store = "My"
                            Location = "LocalMachine"
                        }
                    }
                }
            }
        } else {
            $config.Kestrel = @{
                Endpoints = @{
                    Http = @{
                        Url = "http://0.0.0.0:631"
                    }
                }
            }
        }

        if (-not [string]::IsNullOrEmpty($JobsDir)) {
            if (-not $config.Printer) {
                $config | Add-Member -NotePropertyName "Printer" -NotePropertyValue @{}
            }
            $config.Printer.JobsPath = $JobsDir
            Write-Output "Set JobsPath to $JobsDir"
        }

        $config | ConvertTo-Json -Depth 10 | Set-Content $jsonPath -Encoding UTF8
        Write-Output "Updated Kestrel and Printer configuration."
    } else {
        throw "appsettings.Production.json not found in $AppDir"
    }
} catch {
    Write-Error "Failed to update AppSettings: $_"
    exit 1
}
