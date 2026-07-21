$printerName = "IppPrinter"

function Assert-Administrator {
    $isAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
    if (-not $isAdmin) {
        Write-Error "This script must be run as Administrator. Please open PowerShell as Administrator and run the script again."
        exit 1
    }
}

function Restart-Spooler {
    try {
        Write-Output "Restarting Spooler service..."
        Restart-Service -Name 'Spooler' -ErrorAction Stop
        Start-Sleep -Seconds 3
        Write-Output "Spooler service restarted successfully."
    } catch {
        Write-Error "Error restarting Spooler service: $_"
        exit 1
    }
}

function Remove-IppPrinter {
    if (Get-Printer -Name $printerName -ErrorAction SilentlyContinue) {
        try {
			Write-Output "Removing printer: $printerName"
			Remove-Printer -Name $printerName -ErrorAction Stop
			Write-Output "Printer $printerName removed successfully."
		} catch {
			Write-Error "Error removing printer '$printerName': $_"
			exit 1
		}
    }
	else {
		Write-Output "Printer '$printerName' does not exist."
	}
}

# Execute steps
Assert-Administrator
Restart-Spooler
Remove-IppPrinter

Write-Output "Printer removal completed successfully."