param(
    [string]$Scheme = "http",
    [string]$AppDir,
    [string]$JobsDir,
    [string]$PostProcessName = "",
    [string]$PostProcessArguments = ""
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

        if (-not $config.Printer) {
            $config | Add-Member -NotePropertyName "Printer" -NotePropertyValue @{}
        }

        if (-not [string]::IsNullOrEmpty($JobsDir)) {
            $config.Printer.JobsPath = $JobsDir
            Write-Output "Set JobsPath to $JobsDir"
        }

        $config.Printer.PostProcessName = $PostProcessName
        $config.Printer.PostProcessArguments = $PostProcessArguments
        Write-Output "Set PostProcessName to '$PostProcessName' and PostProcessArguments to '$PostProcessArguments'"

        $jsonText = $config | ConvertTo-Json -Depth 10
        [System.IO.File]::WriteAllText($jsonPath, $jsonText, [System.Text.UTF8Encoding]::new($false))
        Write-Output "Updated Kestrel and Printer configuration."
    } else {
        throw "appsettings.Production.json not found in $AppDir"
    }
} catch {
    Write-Error "Failed to update AppSettings: $_"
    exit 1
}
