Push-Location "$PSScriptRoot\.."
$jsonOutput = dotnet list package --include-transitive --outdated --format json
$parsedJson = $jsonOutput | ConvertFrom-Json

# Initialize an empty dictionary
$packageVersions = @{}

# Iterate over each project
foreach ($project in $parsedJson.projects) {
    foreach ($framework in $project.frameworks) {
        foreach ($package in $framework.topLevelPackages) {
            if ($package.latestVersion -match '^\d' -and -not $packageVersions.ContainsKey($package.id)) {
                $packageVersions[$package.id] = $package.latestVersion
            }
        }
        foreach ($package in $framework.transitivePackages) {
            if ($package.latestVersion -match '^\d' -and -not $packageVersions.ContainsKey($package.id)) {
                $packageVersions[$package.id] = $package.latestVersion
            }
        }
    }
}

# Output the dictionary
$packageVersions

# Determine the script's directory
$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path

# Path to the XML file in the parent directory
$xmlFilePath = Join-Path -Path (Split-Path -Parent $scriptDir) -ChildPath "Directory.Packages.props"

# Load the XML file
[xml]$xml = Get-Content $xmlFilePath

# Iterate over the dictionary to update or add PackageVersion elements
foreach ($packageId in $packageVersions.Keys) {
    $found = $false
    
    # Update the existing package version
    foreach ($package in $xml.Project.ItemGroup.PackageVersion) {
        if ($package.Include -eq $packageId) {
            $package.Version = $packageVersions[$packageId]
            $found = $true
            break
        }
    }
    
    # Add a new package version if it wasn't found
    if (-not $found) {
        $newPackage = $xml.CreateElement("PackageVersion")
        $newPackage.SetAttribute("Include", $packageId)
        $newPackage.SetAttribute("Version", $packageVersions[$packageId])
        $xml.Project.ItemGroup.AppendChild($newPackage) | Out-Null
    }
}

# Save the modified XML file
$xml.Save($xmlFilePath)
Pop-Location