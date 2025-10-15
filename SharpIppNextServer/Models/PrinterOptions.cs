using SharpIpp.Protocol.Models;

namespace SharpIppNextServer.Models;

public class PrinterOptions
{
    public string Name { get; set; } = "SharpIppNext";
    public string DnsSdName { get; set; } = "SharpIppNext [231076]";
    public Guid UUID { get; set; } = new Guid("d178b387-3a93-4d17-a561-007f876c4901");
    public string FirmwareName { get; set; } = "SIN22183498";
    public Sides[] Sides { get; set; } = [SharpIpp.Protocol.Models.Sides.OneSided];
    public PrintScaling[] PrintScaling { get; set; } = [SharpIpp.Protocol.Models.PrintScaling.Auto];
    public string[] Media { get; set; } = [
        "iso_a4_210x297mm",
        "na_executive_7.25x10.5in",
        "na_letter_8.5x11in",
        "na_legal_8.5x14in",
        "na_govt-letter_8x10in",
        "na_invoice_5.5x8.5in",
        "iso_a5_148x210mm",
        "jis_b5_182x257mm",
        "jpn_hagaki_100x148mm",
        "iso_a6_105x148mm",
        "na_index-4x6_4x6in",
        "na_index-5x8_5x8in",
        "na_index-3x5_3x5in",
        "na_monarch_3.875x7.5in",
        "na_number-10_4.125x9.5in",
        "iso_dl_110x220mm",
        "iso_c5_162x229mm",
        "iso_c6_114x162mm",
        "na_a2_4.375x5.75in",
        "jpn_chou3_120x235mm",
        "jpn_chou4_90x205mm",
        "oe_photo-l_3.5x5in",
        "jpn_photo-2l_127x177.8mm",
        "na_5x7_5x7in",
        "oe_photo_4x5in",
        "na_personal_3.625x6.5in",
        "iso_b5_176x250mm",
        "om_small-photo_100x150mm",
        "na_foolscap_8.5x13in",
        "custom_min_3x5in",
        "custom_max_8.5x14in",
        "stationery",
        "photographic-glossy"
    ];
    public Resolution[] Resolution { get; set; } = [new(600, 600, ResolutionUnit.DotsPerInch)];
    public Finishings[] Finishings { get; set; } = [SharpIpp.Protocol.Models.Finishings.None];
    public PrintQuality[] PrintQuality { get; set; } = [SharpIpp.Protocol.Models.PrintQuality.High];
    public int JobPriority { get; set; } = 1;
    public int Copies { get; set; } = 1;
    public Orientation Orientation { get; set; } = Orientation.Portrait;
    public JobHoldUntil JobHoldUntil { get; set; } = JobHoldUntil.NoHold;
    public string DocumentFormat { get; set; } = "application/pdf";
    public string[] OutputBin { get; set; } = ["top"];
    public PrintColorMode[] PrintColorModes { get; set; } = [PrintColorMode.Color];
}