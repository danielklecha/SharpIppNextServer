$certSubject = "CN=IppPrinterSelfSigned"

$ErrorActionPreference = "Stop"

try {
    function Remove-CertificatesFromStore {
        param(
            [string]$StoreName
        )
        
        $store = New-Object System.Security.Cryptography.X509Certificates.X509Store($StoreName, "LocalMachine")
        $store.Open("ReadWrite")
        
        $certs = $store.Certificates | Where-Object { $_.Subject -eq $certSubject }
        
        if ($certs) {
            foreach ($cert in $certs) {
                Write-Output "Removing certificate $($cert.Thumbprint) from $StoreName store..."
                $store.Remove($cert)
            }
        } else {
            Write-Output "No matching certificates found in $StoreName store."
        }
        
        $store.Close()
    }

    Write-Output "Starting certificate cleanup..."
    Remove-CertificatesFromStore -StoreName "My"
    Remove-CertificatesFromStore -StoreName "Root"
    Write-Output "Certificate cleanup completed."
} catch {
    Write-Error "Failed to remove certificate: $_"
    exit 1
}
