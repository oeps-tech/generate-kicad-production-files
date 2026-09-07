using Oeps.KicadProductionFiles.Core.Checks;
using Oeps.KicadProductionFiles.Core.Kicad;

namespace Oeps.KicadProductionFiles.Core.CheckFilesConfiguration;

/// <summary>Checks the order of the included BOM columns in the saved field list.</summary>
public sealed class FieldOrderCheck : IFileCheck
{
    private const string CheckName = "Field order";
    public string Name => CheckName;

    private static readonly OrderedField[] Order =
    [
        new("#", ["${ITEM_NUMBER}"]),
        new("Qty", ["${QUANTITY}"]),
        new("Reference", ["Reference"]),
        new("Value", ["Value"]),
        new("Tolerance", ["Tolerance"]),
        new("Footprint", ["Footprint"]),
        new("TempCo", ["Temp. Co.", "TempCo", "Temp Co"]),
        new("Voltage", ["Voltage"]),
        new("DNP", ["${DNP}"]),
        new("OEPS PN", ["OEPS PN", "OEPSPN"]),
        new("MPN", ["MPN"]),
        new("LCSC", ["LCSC"], Optional: true),
        new("OEPS Description", ["OEPS Description"])
    ];

    public async Task<CheckResult> RunAsync(CheckContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            var settings = await SymbolFieldsTableReader.ReadAsync(context.ProjectDirectory, cancellationToken).ConfigureAwait(false);
            return Validate(settings);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or ArgumentException or NotSupportedException)
        {
            return new(Name, CheckStatus.Failed, "Could not check the field order.\n" + ex.Message);
        }
    }

    public static CheckResult Validate(SymbolFieldsTableSettings settings)
    {
        var expected = Order.Where(rule => !rule.Optional || settings.Fields.Any(field => rule.Names.Contains(field.Name, StringComparer.Ordinal))).ToArray();
        var actual = settings.Fields.Where(field => field.Included).Select(field => new
        {
            field.Name,
            Rule = Order.FirstOrDefault(rule => rule.Names.Contains(field.Name, StringComparer.Ordinal))
        }).ToArray();

        if (actual.Select(field => field.Rule).SequenceEqual(expected))
            return new(CheckName, CheckStatus.Passed, "");

        var expectedText = string.Join(" → ", expected.Select(field => field.Caption));
        var actualText = actual.Length == 0 ? "(no included fields)" :
            string.Join(" → ", actual.Select(field => field.Rule?.Caption ?? $"{field.Name} (additional field)"));
        return new(CheckName, CheckStatus.Failed,
            $"Set the included fields to the expected sequence.\nExpected: {expectedText}\nActual: {actualText}");
    }

    private sealed record OrderedField(string Caption, string[] Names, bool Optional = false);
}
