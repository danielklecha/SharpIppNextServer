# SharpIppNextServer

IPP printer (web app) based on `SharIppNext` library.

## Installation

1. Add manually using add printer wizard
2. Add automatically using script
    
	- Windows: `add-printer -Name "SharpIpp on http://127.0.0.1:631" -DriverName "Microsoft IPP Class Driver" -PortName "http://127.0.0.1:631/"`
3. Add to `NetPrinter` app from Google Play Store
4. Add to IPP / CUPS printing for Chrome & Chromebooks extension from Chrome Web Store.

## Integration testing

1. Install package: `sudo zypper install cups-client cups-backends`
2. Obtain correct IP: `ip route`
3. Make test: `ipptool -t ipp://172.25.192.1 ipp-1.1.test`
