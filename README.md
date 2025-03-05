# SharpIppNextServer

[![GitHub downloads](https://img.shields.io/github/downloads/danielklecha/SharpIppNextServer/total.svg)](https://github.com/danielklecha/SharpIppNextServer/releases)
[![License](https://img.shields.io/badge/license-MIT-green.svg)](https://github.com/danielklecha/SharpIppNextServer/blob/master/LICENSE.txt)

IPP printer (web app) based on `SharpIppNext` library.

## Installation

The printer should be compatible with any IPP client.

### Windows (Printer wizard)

1. Open "printers & scanners"
2. Click "Add device"
3. Click "Add a new device manually"
4. Select "Select a shared printer by name" and use url http://127.0.0.1:631/SharpIppNext
5. Click "Next"
6. Click "Windows Update" if you don't see printer from next point. Process could teke few minutes. 
6. Select "Microsoft" as "Manufacturer and "Microsoft Print to PDF" as printer.
7. Click "OK"
8. Click "Next"
9. Click "Print a  test page" (optionally)
10. Click "Finish"

### Windows (Script)

```powershell
Add-Printer -Name "SharpIppNext" -PortName "http://127.0.0.1:631/SharpIppNext" -DriverName "Microsoft Print To PDF"
```

All steps are described in `Setup\Add-printer.ps1`

### Android

Use the NetPrinter App.

## Requirements

ASP.NET Core 8.0 Runtime needs to be installed.

## Integration testing (OpenSUSE)

1. Install package: `sudo zypper install cups-client cups-backends`
2. Obtain correct IP: `ip route`
3. Make test: `ipptool -t ipp://172.25.192.1 ipp-1.1.test`
