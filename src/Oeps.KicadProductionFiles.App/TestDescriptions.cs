namespace Oeps.KicadProductionFiles.App;

internal sealed record TestDescription(string Name, string Detail, string Fix);

/// <summary>Short operator-facing explanations, in report order.</summary>
internal static class TestDescriptions
{
    private const string Configurable = "Yes, through ‘Configure files’.";

    internal static readonly TestDescription[] Configuration =
    [
        new("Symbol Fields Table",
            "Checks that all required fields are present and included, and that the appropriate fields are grouped. LCSC is optional, but must be included and grouped if present.", Configurable),
        new("Edit Tab metadata",
            "Checks that symbols are grouped, DNP symbols are included, symbols marked ‘Exclude from BOM’ are excluded, and sorting is by Reference in ascending order.", Configurable),
        new("Export configuration",
            "Checks the BOM output path manufacturing/bom/${PROJECTNAME}.csv, comma field and reference delimiters, double-quote string delimiter, empty range delimiter, and removal of tabs and line breaks.", Configurable),
        new("Field order",
            "Checks the included columns follow this order: #, Qty, Reference, Value, Tolerance, Footprint, TempCo, Voltage, DNP, OEPS PN, MPN, LCSC, OEPS Description. LCSC may be absent.", Configurable),
        new("Revision",
            "Checks that the project’s schematic revision setting and the main schematic’s title-block revision match the Revision entry. Letter and numeric revisions accept equivalent prefixes, such as RevB = B and v1.3 = 1.3.", Configurable),
        new("PCB silkscreen revision",
            "Checks that the expected revision appears in visible text on F.SilkS or B.SilkS. It may appear inside a longer string, with a revision or version prefix.", "No. Edit the PCB manually."),
        new("Gerber plot settings",
            "Checks the saved PCB plot options, including X2 and netlist attributes, drill/place origin, DNP cross-out and output folder manufacturing/gerber/. Included layers and zone fills still need your review before generating.", Configurable),
        new("Schematic BOM OEPS PN / MPN / Description database validation",
            "Exports a temporary BOM via KiCad CLI, including linked schematics. Checks that each component has an OEPS PN, MPN and OEPS Description, and that they match the database. Symbols excluded from the BOM are omitted.",
            "Description only, through ‘Configure files’, when OEPS PN and MPN match a database entry with an unambiguous description."),
        new("Schematic BOM duplicate OEPS PN / MPN",
            "Exports a temporary CLI BOM using the saved Symbol Fields Table grouping. Reports OEPS PN or MPN values appearing in multiple grouped rows, with their references. Multiple components within one row are not duplicates. Group symbols must be enabled. Missing or invalid identifiers are covered by the preceding test.", "No.")
    ];

    internal static readonly TestDescription[] Production =
    [
        new("BOM / placement comparison",
            "Compares component counts and references in the newly generated BOM and placement files. Reports components missing from either file, including differences when totals match. Runs only when placement files are generated.", "No."),
        new("BOM required fields: OEPS PN, MPN and OEPS Description",
            "Checks that every generated BOM component has valid, non-empty OEPS PN, MPN and OEPS Description fields. Also detects duplicate references and conflicting OEPS PN fields.", "No."),
        new("BOM vs database: OEPS PN, MPN and OEPS Description",
            "Checks that each generated BOM component’s OEPS PN exists in the database, is paired with its MPN, and has the matching OEPS Description.", "No."),
        new("BOM vs layout: footprints, OEPS PN, MPN and OEPS Description",
            "Checks that each BOM reference has exactly one matching PCB footprint, with OEPS PN, MPN and OEPS Description matching the BOM. Extra PCB footprints outside the BOM are not checked here.", "No.")
    ];
}
