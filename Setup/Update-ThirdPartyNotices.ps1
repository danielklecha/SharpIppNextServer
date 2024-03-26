Push-Location
cd "$PSScriptRoot\..\SharpIppNextServer"
dotnet-thirdpartynotices --output-filename "THIRD-PARTY-NOTICES.txt"
Pop-Location