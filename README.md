# IppPrinter

[![GitHub downloads](https://img.shields.io/github/downloads/danielklecha/IppPrinter/total.svg)](https://github.com/danielklecha/IppPrinter/releases)
[![License](https://img.shields.io/badge/license-MIT-green.svg)](LICENSE.txt)

**IppPrinter** is a lightweight, cross-platform IPP (Internet Printing Protocol) print server web application built with ASP.NET Core and powered by the [`SharpIppNext`](https://github.com/danielklecha/SharpIppNext) library.

## Features

- **Full IPP Protocol Support**: Implements standard IPP 1.1 and 2.0 operations (`Print-Job`, `Create-Job`, `Send-Document`, `Get-Printer-Attributes`, `Get-Job-Attributes`, `Get-Jobs`, `Cancel-Job`, subscriptions, etc.) via `SharpIppNext`.
- **Automatic Document Conversion**: Converts incoming document payloads automatically to PDF format for storage and processing:
  - **PWG Raster** (`image/pwg-raster`)
  - **Apple Raster / URF** (`image/urf`)
  - **Images** (`image/png`, `image/jpeg`, `image/gif`, `image/bmp`)
  - **Plain Text** (`text/plain`) with automatic line wrapping and PDF layout generation
  - **Native PDF** (`application/pdf`) pass-through
- **mDNS / Zeroconf Discovery**: Automatic network printer advertisement (`PrinterDiscoveryService`) allowing clients on the network (Windows, macOS, iOS AirPrint, Android, Linux CUPS) to discover the printer automatically.
- **Multiple IPP Endpoints**: Supports common IPP path patterns (`/`, `/ipp`, `/ipp/print`, `/printers/{PrinterName}`, `/{PrinterName}`).
- **Cross-Platform & Windows Service**: Runs on Windows, Linux, and macOS. Includes native Windows Service hosting and Event Log integration.
- **Background Job Queue**: Async job queue with background processing and job timeout handling.
- **Easy Deployment & Setup**: Comes with PowerShell automation scripts (`Add-Printer.ps1` / `Remove-Printer.ps1`) and an optional Inno Setup GUI installer.

## Requirements

- [.NET 10.0 Runtime](https://dotnet.microsoft.com/download/dotnet/10.0) installed.

## Installation

The printer is compatible with any standard IPP client.

### Windows (GUI Installer)

1. Download `IppPrinterSetup.exe` from [Releases](https://github.com/danielklecha/IppPrinter/releases).
2. Run the installer wizard as Administrator.
3. The setup automatically installs the binaries, registers and starts the Windows Service, configures the firewall rule for port `631`, and registers the `IppPrinter` print queue.

### Windows (Printer Wizard)

1. Open **Printers & scanners** in Windows Settings.
2. Click **Add device**.
3. Click **Add a new device manually**.
4. Select **Add a printer using an IP address or hostname**.
5. Select **IPP Device** as the device type.
6. Enter `http://127.0.0.1:631/` or `https://127.0.0.1:631/` as the **Hostname or IP address**.
7. Click **Next** to finish adding the printer.
8. (Optionally) Click **Print a test page**.
9. Click **Finish**.

### Windows (PowerShell Script)

```powershell
Add-Printer -Name "IppPrinter" -IppURL "http://127.0.0.1:631/ipp/print"
```

All installation and removal steps are also automated via `Setup/Add-Printer.ps1` and `Setup/Remove-Printer.ps1`.

### Android

Tested with the `NetPrinter` app and standard Android IPP print services.

## Contributing & Testing

For local development setup, building the installer, and integration testing instructions, see [CONTRIBUTION.md](CONTRIBUTION.md).

## License

`IppPrinter` is provided as-is under the [MIT license](LICENSE.txt).

For details on third-party dependencies and their licenses, see [THIRD-PARTY-NOTICES.txt](THIRD-PARTY-NOTICES.txt).