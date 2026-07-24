[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'

Push-Location "$PSScriptRoot\.."

try {
    Write-Host "Fetching outdated packages..."
    $jsonOutput = dotnet list package --include-transitive --outdated --format json
    if ($LASTEXITCODE -ne 0) {
        Write-Error "Failed to fetch outdated packages."
        exit $LASTEXITCODE
    }

    $parsedJson = $jsonOutput | ConvertFrom-Json

    # Initialize an empty dictionary
    $packageVersions = @{}

    # Iterate over each project
    if ($null -ne $parsedJson.projects) {
        foreach ($project in $parsedJson.projects) {
            if ($null -ne $project.frameworks) {
                foreach ($framework in $project.frameworks) {
                    # Process top-level packages
                    if ($null -ne $framework.topLevelPackages) {
                        foreach ($package in $framework.topLevelPackages) {
                            if ($package.latestVersion -match '^\d' -and -not $packageVersions.ContainsKey($package.id)) {
                                $packageVersions[$package.id] = $package.latestVersion
                            }
                        }
                    }
                    # Process transitive packages
                    if ($null -ne $framework.transitivePackages) {
                        foreach ($package in $framework.transitivePackages) {
                            if ($package.latestVersion -match '^\d' -and -not $packageVersions.ContainsKey($package.id)) {
                                $packageVersions[$package.id] = $package.latestVersion
                            }
                        }
                    }
                }
            }
        }
    }

    if ($packageVersions.Count -eq 0) {
        Write-Host "No outdated packages found."
        exit 0
    }

    Write-Host "Found $($packageVersions.Count) outdated package(s)."

    $xmlFilePath = Join-Path (Get-Location) "Directory.Packages.props"
    if (-not (Test-Path $xmlFilePath)) {
        Write-Error "Could not find Directory.Packages.props at $xmlFilePath"
        exit 1
    }

    # Load the XML file
    [xml]$xml = Get-Content $xmlFilePath

    # Find an ItemGroup that contains PackageVersion, or just the first ItemGroup
    $itemGroup = $xml.SelectSingleNode("//ItemGroup[PackageVersion]")
    if (-not $itemGroup) {
        $itemGroup = $xml.SelectSingleNode("//ItemGroup")
    }

    $updatedCount = 0
    $addedCount = 0

    # Iterate over the dictionary to update or add PackageVersion elements
    foreach ($packageId in $packageVersions.Keys) {
        $existingPackage = $xml.SelectNodes("//PackageVersion") | Where-Object { $_.Include -eq $packageId }
        
        if ($existingPackage) {
            # Update the existing package version
            if ($existingPackage.Version -ne $packageVersions[$packageId]) {
                $existingPackage.Version = $packageVersions[$packageId]
                $updatedCount++
            }
        }
        else {
            # Add a new package version if it wasn't found
            if ($itemGroup) {
                $newPackage = $xml.CreateElement("PackageVersion")
                $newPackage.SetAttribute("Include", $packageId)
                $newPackage.SetAttribute("Version", $packageVersions[$packageId])
                $itemGroup.AppendChild($newPackage) | Out-Null
                $addedCount++
            } else {
                Write-Warning "Could not find an <ItemGroup> to add the package $packageId."
            }
        }
    }

    if ($updatedCount -gt 0 -or $addedCount -gt 0) {
        Write-Host "Saving changes to $xmlFilePath..."
        
        $settings = New-Object System.Xml.XmlWriterSettings
        $settings.Indent = $true
        $settings.IndentChars = "  "
        $settings.OmitXmlDeclaration = $true
        
        $writer = [System.Xml.XmlWriter]::Create($xmlFilePath, $settings)
        try {
            $xml.Save($writer)
        }
        finally {
            $writer.Close()
            $writer.Dispose()
        }
        
        Write-Host "Updated $updatedCount package(s) and added $addedCount package(s)."
    } else {
        Write-Host "No changes were made to Directory.Packages.props."
    }
}
finally {
    Pop-Location
}