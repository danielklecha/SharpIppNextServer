$printerName = "IppPrinter"
$ipAddress = "127.0.0.1"
$portNumber = 631
$printerUrl = "http://$ipAddress`:$portNumber/ipp/print"

function Assert-Administrator {
    $isAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
    if (-not $isAdmin) {
        Write-Error "This script must be run as Administrator. Please open PowerShell as Administrator and run the script again."
        exit 1
    }
}

function Import-Modules {
    if ($PSVersionTable.PSVersion.Major -ge 7) {
        Write-Output "Detected PowerShell 7. Importing DISM module..."
        try {
            Import-Module DISM -UseWindowsPowerShell -ErrorAction Stop -WarningAction SilentlyContinue
            Write-Output "DISM module imported successfully."
        } catch {
            Write-Error "Failed to import DISM module. Ensure PowerShell 5.1 is available."
            exit 1
        }
    }
}

function Install-WindowsFeature {
    try {
        Write-Output "Checking Internet Print Client feature status..."
        $features = @("Printing-InternetPrinting-Client", "Printing-Foundation-InternetPrinting-Client")
        foreach ($featureName in $features) {
            $feature = Get-WindowsOptionalFeature -Online -FeatureName $featureName -ErrorAction SilentlyContinue
            if ($feature -and $feature.State -eq "Enabled") {
                Write-Output "$featureName is already enabled."
                return
            }
            if ($feature) {
                Write-Output "Enabling $featureName..."
                Enable-WindowsOptionalFeature -Online -FeatureName $featureName -NoRestart -ErrorAction Stop
                Write-Output "$featureName has been installed successfully."
                return
            }
        }
        Write-Error "Required features not found. Ensure this is a supported Windows version."
        exit 1
    } catch {
        Write-Error "Error enabling Internet Print Client feature: $_"
        exit 1
    }
}

function Restart-Spooler {
    try {
        Write-Output "Restarting Spooler service..."
        Restart-Service -Name 'Spooler' -Force -ErrorAction Stop
        Start-Sleep -Seconds 3
        Write-Output "Spooler service restarted successfully."
    } catch {
        Write-Error "Failed to restart Spooler service: $_"
        exit 1
    }
}

function Add-IppPrinter {
    if (Get-Printer -Name $printerName -ErrorAction SilentlyContinue) {
        Write-Output "Printer '$printerName' already exists."
        return
    }
    try {
        Write-Output "Adding printer: $printerName..."
        Add-Printer -Name $printerName -IppURL $printerUrl -ErrorAction Stop
        Write-Output "Printer '$printerName' added successfully."
    } catch {
        Write-Error "Error adding printer '$printerName': $_"
        exit 1
    }
}

# Execute steps
Assert-Administrator
Import-Modules
Install-WindowsFeature
Restart-Spooler
Add-IppPrinter

Write-Output "Printer setup completed successfully."