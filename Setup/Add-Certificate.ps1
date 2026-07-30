$certSubject = "CN=IppPrinterSelfSigned"
$dnsNames = @("localhost", "127.0.0.1", $env:COMPUTERNAME)
$friendlyName = "IppPrinter Local Server"
$validityYears = 100

$ErrorActionPreference = "Stop"

function Grant-PrivateKeyAccess {
    param(
        [System.Security.Cryptography.X509Certificates.X509Certificate2]$Cert,
        [string]$SidString
    )
    
    try {
        $rsa = [System.Security.Cryptography.X509Certificates.RSACertificateExtensions]::GetRSAPrivateKey($Cert)
        $keyName = ""
        $keyPath = ""

        if ($rsa -is [System.Security.Cryptography.RSACng]) {
            $keyName = $rsa.Key.UniqueName
            $keyPath = "$env:ALLUSERSPROFILE\Microsoft\Crypto\Keys\$keyName"
        } elseif ($Cert.HasPrivateKey) {
            $keyName = $Cert.PrivateKey.CspKeyContainerInfo.UniqueKeyContainerName
            $keyPath = "$env:ALLUSERSPROFILE\Microsoft\Crypto\RSA\MachineKeys\$keyName"
        }

        if ($keyPath -and (Test-Path $keyPath)) {
            $acl = Get-Acl -Path $keyPath
            $sid = New-Object System.Security.Principal.SecurityIdentifier($SidString)
            $rule = New-Object System.Security.AccessControl.FileSystemAccessRule($sid, "Read", "Allow")
            $acl.AddAccessRule($rule)
            Set-Acl -Path $keyPath -AclObject $acl
            Write-Output "Granted Read access to SID $SidString for the private key."
        } else {
            Write-Warning "Could not find physical private key file to set permissions."
        }
    } catch {
        Write-Warning "Could not grant private key access automatically: $_"
    }
}

try {
    Write-Output "Checking for existing certificate with subject: $certSubject"

    # Check if it already exists in the Personal store
    $cert = Get-ChildItem -Path Cert:\LocalMachine\My | Where-Object { $_.Subject -eq $certSubject }

    if (-not $cert) {
        Write-Output "Generating self-signed certificate for $certSubject..."
        $notAfter = (Get-Date).AddYears($validityYears)
        
        # Create the self-signed certificate
        $cert = New-SelfSignedCertificate -Subject $certSubject -DnsName $dnsNames -CertStoreLocation "Cert:\LocalMachine\My" -NotAfter $notAfter -FriendlyName $friendlyName
        
        Write-Output "Certificate generated. Thumbprint: $($cert.Thumbprint)"
    } else {
        # If multiple exist, take the first one
        if ($cert -is [array]) {
            $cert = $cert[0]
        }
        Write-Output "Certificate already exists. Thumbprint: $($cert.Thumbprint)"
    }

    # Grant read access to the private key for Local System (S-1-5-18) and BUILTIN\Users (S-1-5-32-545)
    Grant-PrivateKeyAccess -Cert $cert -SidString "S-1-5-18"
    Grant-PrivateKeyAccess -Cert $cert -SidString "S-1-5-32-545"

    # Ensure the certificate is in the Trusted Root Certification Authorities store
    $rootStore = New-Object System.Security.Cryptography.X509Certificates.X509Store("Root", "LocalMachine")
    $rootStore.Open("ReadWrite")

    $trusted = $rootStore.Certificates | Where-Object { $_.Thumbprint -eq $cert.Thumbprint }

    if (-not $trusted) {
        Write-Output "Adding certificate to Trusted Root store..."
        $rootStore.Add($cert)
        Write-Output "Certificate added to Trusted Root store."
    } else {
        Write-Output "Certificate is already in Trusted Root store."
    }

    $rootStore.Close()
} catch {
    Write-Error "Failed to add certificate: $_"
    exit 1
}
