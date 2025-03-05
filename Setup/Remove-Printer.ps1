$printerName = "SharpIppNext"

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
			Write-Host "Removing printer: $printerName"
			Remove-Printer -Name $printerName
			Write-Host "Printer $printerName removed successfully."
		} catch {
			Write-Error "Error adding printer '$printerName': $_"
			exit 1
		}
    }
	else {
		Write-Output "Printer '$printerName' does not exist."
	}
}

# Execute steps
Restart-Spooler
Remove-IppPrinter

Write-Output "Printer setup completed successfully."