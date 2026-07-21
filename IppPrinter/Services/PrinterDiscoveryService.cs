using Makaretu.Dns;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using IppPrinter.Models;
using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace IppPrinter.Services;

public class PrinterDiscoveryService : BackgroundService
{
    private readonly IServer _server;
    private readonly IHostApplicationLifetime _hostApplicationLifetime;
    private readonly PrinterOptions _printerOptions;
    private readonly ILogger<PrinterDiscoveryService> _logger;
    private MulticastService? _multicastService;
    private ServiceDiscovery? _serviceDiscovery;

    public PrinterDiscoveryService(
        IServer server,
        IHostApplicationLifetime hostApplicationLifetime,
        IOptions<PrinterOptions> printerOptions,
        ILogger<PrinterDiscoveryService> logger)
    {
        _server = server;
        _hostApplicationLifetime = hostApplicationLifetime;
        _printerOptions = printerOptions.Value;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Wait until the application is fully started so we can inspect the addresses
        var appStartedTask = new TaskCompletionSource();
        await using var reg = _hostApplicationLifetime.ApplicationStarted.Register(() => appStartedTask.SetResult());

        try
        {
            await appStartedTask.Task.WaitAsync(stoppingToken);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        var addressesFeature = _server.Features.Get<IServerAddressesFeature>();
        if (addressesFeature is null || !addressesFeature.Addresses.Any())
        {
            _logger.LogWarning("No listening addresses found. Cannot emit mDNS information.");
            return;
        }

        // Try to parse the port from the first listening address
        int port = 631;
        var address = addressesFeature.Addresses.FirstOrDefault();
        if (address != null && Uri.TryCreate(address, UriKind.Absolute, out var uri))
        {
            port = uri.Port;
        }
        else
        {
            _logger.LogWarning("Could not parse port from address '{Address}', using default {Port}", address, port);
        }

        try
        {
            _multicastService = new MulticastService();
            _serviceDiscovery = new ServiceDiscovery(_multicastService);

            var profile = new ServiceProfile(_printerOptions.DnsSdName, "_ipp._tcp", (ushort)port);
            
            // Standard IPP TXT records
            profile.AddProperty("txtvers", "1");
            profile.AddProperty("qtotal", "1");
            profile.AddProperty("rp", "ipp/print");
            profile.AddProperty("ty", _printerOptions.Model);
            profile.AddProperty("adminurl", $"http://{System.Net.Dns.GetHostName()}:{port}/");
            profile.AddProperty("note", _printerOptions.Location);
            profile.AddProperty("priority", _printerOptions.JobPriority.ToString());
            profile.AddProperty("product", $"({_printerOptions.Model})");
            // Windows often expects application/octet-stream or image/urf for driverless printing
            profile.AddProperty("pdl", "application/pdf,image/pwg-raster,image/urf,image/jpeg,image/png,image/tiff,text/plain,application/octet-stream");
            profile.AddProperty("Color", "T");
            profile.AddProperty("Duplex", "T");
            profile.AddProperty("UUID", _printerOptions.UUID.ToString());
            
            // Required for Apple AirPrint / Mopria (Windows driverless)
            profile.AddProperty("URF", "W8,SRGB24,CP1,RS600");
            profile.AddProperty("mopria-certified", "1.3");
            profile.AddProperty("kind", "document,envelope,photo");

            _serviceDiscovery.Advertise(profile);

            // Advertise generic HTTP (helps Windows Network Explorer)
            var httpProfile = new ServiceProfile(_printerOptions.DnsSdName, "_http._tcp", (ushort)port);
            httpProfile.AddProperty("adminurl", $"http://{System.Net.Dns.GetHostName()}:{port}/");
            _serviceDiscovery.Advertise(httpProfile);

            _multicastService.Start();

            _logger.LogInformation("Started mDNS advertising for {DnsSdName} on port {Port}", _printerOptions.DnsSdName, port);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to start mDNS advertising");
        }

        try
        {
            await Task.Delay(Timeout.Infinite, stoppingToken);
        }
        catch (TaskCanceledException)
        {
            // Expected when the application is shutting down
        }
    }

    public override Task StopAsync(CancellationToken cancellationToken)
    {
        try
        {
            _serviceDiscovery?.Unadvertise();
            _multicastService?.Stop();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error while stopping mDNS advertising");
        }

        return base.StopAsync(cancellationToken);
    }

    public override void Dispose()
    {
        _serviceDiscovery?.Dispose();
        _multicastService?.Dispose();
        base.Dispose();
    }
}
