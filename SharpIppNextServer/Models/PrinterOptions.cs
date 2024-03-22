using SharpIpp.Protocol.Models;

namespace SharpIppNextServer.Models;

public class PrinterOptions
{
    public string Name { get; set; } = "SharpIpp";
    public Sides Sides { get; set; } = Sides.OneSided;
    public PrintScaling PrintScaling { get; set; } = PrintScaling.Auto;
    public string Media { get; set; } = "iso_a4_210x297mm";
    public Resolution Resolution { get; set; } = new(600, 600, ResolutionUnit.DotsPerInch);
    public Finishings Finishings { get; set; } = Finishings.None;
    public PrintQuality PrintQuality { get; set; } = PrintQuality.High;
    public int JobPriority { get; set; } = 1;
    public int Copies { get; set; } = 1;
    public Orientation Orientation { get; set; } = Orientation.Portrait;
    public JobHoldUntil JobHoldUntil { get; set; } = JobHoldUntil.NoHold;
    public string DocumentFormat { get; set; } = "application/pdf";
}