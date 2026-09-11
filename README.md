# OEPS KiCad Production Files

Windows desktop infrastructure for checking and exporting KiCad production files. The interface follows `raw-material-sticker`: the same OEPS icon, Segoe UI font, colors, field borders, footer, and **620-pixel default client width** at 100% scaling. The default height is 790 pixels. The app and launcher use per-monitor DPI scaling with a 96-DPI design baseline. The window fits the current screen on startup; shorter windows scroll the fields and report while keeping the footer visible. It remembers its size, position, paths and revision.

![Application preview](docs/production-generation-preview.png)

## Current behavior

Enter or browse for `kicad-cli.exe` and the **project folder** containing the main `.kicad_sch` schematic and `.kicad_pcb` board. The newest KiCad CLI in the standard Windows installation location is detected when no CLI preference has been saved.

Enter the expected **Revision** below KiCad files, for example `revA`, `RevB`, `ver1.2`, `Ver2`, or `v1.3.3`. Paths and revision are saved between sessions. Changing any entry requires a fresh configuration check.

| Button | Available now |
| --- | --- |
| Update database | Downloads the shared OEPS spreadsheet, validates it, and saves a local CSV cache. |
| Check files configuration | Checks Symbol Fields Table fields, Edit tab metadata, export configuration, field order, revision, PCB silkscreen revision, Gerber plot settings, and BOM identifiers against the database via CLI. |
| Configure files | Rechecks the project and offers a separate Yes/No confirmation for each repairable failed check, then saves approved fixes and rechecks the result. |
| Generate production files | Optionally clears manufacturing, exports the BOM first, checks its identifiers against the database and layout, then exports selected Gerbers, placements, drills/maps and IPC-D-356. Compares BOM and placement counts and references. |

The report is selectable, scrollable text, with a separate header for each check. Every `[PASSED]` header has a green **✓** before it; every `[FAILED]` header has a red **✗**. The first line shows **✓ [PASSED] All tests passed** or **✗ [FAILED] Not all tests passed**, covering every check in that report, including production export errors and BOM/placement mismatches. Detail text keeps its normal colour. Configuration checks run away from the GUI thread. The BOM identifier check requires KiCad CLI and the cached component database; the other checks still run if either is unavailable. Save changes in KiCad before checking: only saved files are read.

### Schematic BOM identifiers via CLI

`CheckFilesConfiguration/SchematicBomIdentifiersCheck.cs` exports a temporary BOM from the main `.kicad_sch`, letting KiCad follow linked sheets. It requests `Reference`, `OEPS PN`, `OEPSPN`, `MPN`, and `OEPS Description` explicitly, regardless of saved column visibility or export labels. The check uses the default variant, no grouping or reference filter, includes DNP symbols, and excludes symbols marked Exclude from BOM. It preserves tabs and line breaks so invalid field characters can be reported.

Each BOM component must have an OEPS PN (either alias), MPN, and OEPS Description. Its PN/MPN pair must exist in the current cached database, and OEPS Description must match the database's `Description` for that pair. Comparisons ignore outer whitespace but preserve case, punctuation and internal spacing. Failures name each affected reference and explain missing fields, conflicting aliases, unknown OEPS PNs, incorrect MPN pairings, or mismatched descriptions (including the expected database description). A database row with no description also fails validation. The existing five-minute database refresh is unchanged. Missing CLI, export errors, invalid CSV, or an unavailable database fail this check.

Success shows only `[PASSED] Schematic BOM OEPS PN / MPN / Description database validation`. The temporary CSV is deleted after checking; checking does not generate or clear manufacturing files or modify the saved Symbol Fields Table.

**Configure files** offers an OEPS Description fix when this test fails. `ConfigureFiles/OepsDescriptionFix.cs` prepares a list of eligible references and database descriptions before a scrollable Yes/No confirmation (default No). On approval, it fills missing descriptions or replaces incorrect ones in the main schematic and linked sheets, provided the CLI BOM and saved symbol both have the same verified OEPS PN/MPN pair and the database supplies one valid description. Identifiers stay unchanged. Missing/incorrect pairs, ambiguous instances, and unavailable/conflicting descriptions are left unchanged and reported; these failures can remain after a partial fix. New description fields are hidden. Existing property formatting, symbol geometry and library definitions are preserved.

The fix follows linked sheets inside the project folder, supports repeated sheets and multiple symbol units, and does not scan unrelated schematic files. External sheets, filesystem links, cycles and unresolved sheet-filename variables require manual attention. Each changed schematic is backed up in `.oeps-backups` beside that file; all source snapshots are rechecked before saving, and the CLI test reruns after the fix. Declining the prompt leaves schematic files unchanged.

Configure files and Generate production files start disabled. Changing either the KiCad CLI or KiCad files path resets both buttons. Pressing Check files configuration enables Configure files; completing the checks without errors enables Generate production files. A new check clears the previous successful result until it finishes.

Generation options are stacked on the right between the buttons and report, with each label followed by its checkbox and all checkboxes sharing the same right edge. **Generate production files even with errors** starts unchecked. **Delete all files before generate production files**, **Generate Gerber files**, **Generate placement files**, **Generate drill files**, and **Generate IPC-D-356 netlist** start checked each time the app opens. The BOM is always generated first; clearing all four file-type checkboxes generates just the BOM. The override permits generation before a check or after a failed check; generation waits while a check, configuration fix or export is running. Options and inputs are locked during those operations. Database refreshes preserve the current check result.

### Symbol Fields Table

The check reads `schematic.bom_settings.fields_ordered` in the single `.kicad_pro` file directly inside the selected folder. Fields are identified by `name`; Included is stored as `show` and Group By as `group_by`.

| Field | Included | Group By |
| --- | --- | --- |
| Reference | Required | Optional |
| Value | Required | Required |
| Footprint | Required | Required |
| `${QUANTITY}` | Required | Optional |
| `${ITEM_NUMBER}` | Required | Optional |
| LCSC | Required if present | Required if present |
| MPN | Required | Required |
| OEPS Description | Required | Required |
| OEPS PN or OEPSPN | Required | Required |
| Temp. Co. or TempCo or Temp Co | Required | Required |
| Tolerance | Required | Required |
| Voltage | Required | Required |
| `${DNP}` | Required | Required |

KiCad saves Item Number as `${ITEM_NUMBER}` with an underscore. Aliases are matched exactly; when multiple accepted aliases are present, each present field is checked. LCSC may be absent from the saved field list. Column order and the global `group_symbols` option have their own checks below. Passing results show only the `[PASSED] Symbol Fields Table` header; failures list every correction needed.

### Edit Tab metadata

A separate check reads the following values from `schematic.bom_settings` and reports them under **Edit Tab metadata**:

| Edit tab option | Required saved setting |
| --- | --- |
| Group symbols selected | `group_symbols: true` |
| Include 'DNP' Symbols selected | `exclude_dnp: false` |
| Include 'Exclude from BOM' Symbols cleared | `include_excluded_from_bom: false` |
| Ascending sort | `sort_asc: true` |
| Sort field Reference | `sort_field: "Reference"` |

All five settings must be present with the correct JSON types and values. The check lists every required correction; it does not modify the project. Reports omit timestamps and project paths.

### Export configuration

The **Export configuration** check requires `schematic.bom_export_filename` to be exactly `manufacturing/bom/${PROJECTNAME}.csv`. The project variable stays literal in the saved configuration.

It also checks `schematic.bom_fmt_settings`:

| Setting | Required value |
| --- | --- |
| `field_delimiter` | Comma (`,`) |
| `string_delimiter` | One double quote (`"`) |
| `ref_delimiter` | Comma (`,`) |
| `ref_range_delimiter` | Empty string |
| `keep_tabs` | `false` |
| `keep_line_breaks` | `false` |

Missing, duplicated or incorrect settings are reported. Passing checks show only their `[PASSED]` header; failed checks list the corrections. This check reads the saved configuration and does not generate a BOM or modify the project.

### Field order

The **Field order** check requires this sequence of included columns in `schematic.bom_settings.fields_ordered`:

```text
# → Qty → Reference → Value → Tolerance → Footprint → TempCo → Voltage → DNP → OEPS PN → MPN → LCSC → OEPS Description
```

If LCSC is absent from the saved field list, its slot is omitted. Hidden extra fields are ignored. Additional included columns, missing or hidden required columns, and columns in the wrong position fail this check. Accepted temperature and OEPS PN aliases use the same slots. The check identifies `#`, `Qty` and `DNP` using their KiCad field names `${ITEM_NUMBER}`, `${QUANTITY}` and `${DNP}`; export labels are not renamed or validated.

Passing results show only `[PASSED] Field order`. Failures show the expected and actual sequences.

## Revision check

The additional **PCB silkscreen revision** check looks for the expected revision in visible saved text on `F.SilkS` or `B.SilkS` in the matching main `.kicad_pcb`. It accepts bare values and case-insensitive captions such as `Version1.2.`, `version 1.2`, `v1.2`, `Revision B`, `revisionB`, `revB` or `rB`, including within longer strings. Revision boundaries matter: `1.2.3` does not satisfy `1.2`, and `BB` does not satisfy `B`. Board text, text boxes, and visible footprint text/properties are checked; hidden text, other layers, title-block metadata and unresolved `${...}` text variables do not count.

This reports only `[PASSED] PCB silkscreen revision` on success. A failure explains that the board needs a manual correction in KiCad. There is **no automatic fix and no confirmation popup** for this check. The separate project/schematic Revision fix remains available and does not change PCB silkscreen text.

The **Revision** check compares the entry in the GUI with both `board.ipc2581.sch_revision` in the `.kicad_pro` file and `(rev "...")` in the root `(title_block ...)` of the main `.kicad_sch`. The main schematic must have the same filename stem as the project, for example `main.kicad_pro` and `main.kicad_sch`. Surrounding spaces, letter case, and an optional leading `rev` or `ver` (or `v` before a number) are ignored. `B`, `RevB`, and `revB` match; `ver1.2`, `v1.2`, and `1.2` match. Fixes write the normalized string: `Ver2` becomes `2`, `v1.3.3` becomes `1.3.3`, and `ver1.3.5` becomes `1.3.5`. Dotted numbers are preserved exactly, and already matching saved values are kept.

When both values match, the report shows only `[PASSED] Revision`. A missing or different saved revision, an invalid setting, or an empty Revision entry shows `[FAILED] Revision` with the reason and the affected file type. A bare `Rev` prefix is also invalid because it has no revision value. A missing main schematic is reported; other schematics and subsheets are not substituted for it.

Its source is `src/Oeps.KicadProductionFiles.Core/CheckFilesConfiguration/RevisionCheck.cs`.

## Gerber plot settings check

The **Gerber plot settings** check reads `setup/pcbplotparams` in the main `.kicad_pcb` with the same filename stem as the single `.kicad_pro` file. Its source is `CheckFilesConfiguration/GerberPlotSettingsCheck.cs`.

It requires the General and Gerber options shown in the supplied screenshot:

| Saved setting | Required value |
| --- | --- |
| `plotframeref` | `no` |
| `subtractmaskfromsilk` | `yes` |
| `hidednponfab` | `no` |
| `sketchdnponfab`, `crossoutdnponfab` | Both `yes` (Indicate DNP → Cross-out) |
| `sketchpadsonfab`, `plotpadnumbers` | Both `no` |
| `drillshape` | `0` (None) |
| `scaleselection` | `1` (1:1) |
| `useauxorigin` | `yes` |
| `mirror`, `psnegative` | Both `no` |
| `usegerberextensions`, `creategerberjobfile` | Both `no` |
| `gerberprecision` | `6`, or absent because 6 is KiCad's default |
| `usegerberattributes`, `usegerberadvancedattributes` | Both `yes` |
| `disableapertmacros` | `no` |
| `outputdirectory` | `"manufacturing/gerber/"` (quoted, with trailing slash) |

A passing result shows only `[PASSED] Gerber plot settings`. Failures list each missing or incorrect setting and its expected value. This includes the saved values corresponding to disabled screenshot controls and the required output directory `manufacturing/gerber/`. Layer selections, output format and other export-format settings are outside this check.

**Check zone fills before plotting** is a KiCad application preference, not a `pcbplotparams` entry. This PCB check/fix does not change that preference. Gerber generation requests zone checking explicitly with `--check-zones`.

## Configure files

After running Check files configuration, click **Configure files**. It reads the current saved project again and prepares a fix for each failed check. Each confirmation popup identifies the failed check, lists its problems, and describes the proposed change:

- **Yes** applies that fix and saves the project.
- **No** skips that fix and continues to the next one.
- Passing checks do not prompt. Checks are rerun between fixes and at the end, so an already resolved failure is skipped automatically.

Each fix has its own source file in `src/Oeps.KicadProductionFiles.Core/ConfigureFiles/`:

| Source file | Changes |
| --- | --- |
| `SymbolFieldsTableFix.cs` | Adds missing required fields and corrects Included/Group By flags. Preserves existing aliases and labels; does not add absent LCSC. |
| `EditTabMetadataFix.cs` | Corrects grouping, DNP/BOM inclusion and Reference sorting. |
| `ExportConfigurationFix.cs` | Corrects the BOM filename, delimiters and text options. |
| `FieldOrderFix.cs` | Reorders existing required fields and clears Included for extra columns while retaining their settings. |
| `RevisionFix.cs` | Corrects the project revision and main schematic title-block revision under one confirmation, using the entered revision without its optional prefix, in uppercase (for example `B`). Already matching values are retained. Requires a nonempty Revision entry. |
| `GerberPlotSettingsFix.cs` | Repairs the screenshot's saved PCB plot options and sets the output directory to `manufacturing/gerber/`. Adds missing settings/containers, accepts omitted default precision, and preserves other PCB text, including layers. |
| `OepsDescriptionFix.cs` | Fills or updates schematic OEPS Description for verified BOM/database PN/MPN pairs, after a reviewable confirmation; backs up changed sheets and reruns the CLI check. |

Field order requires the required fields to exist and be included. If the Symbol Fields Table fix is declined while those inputs are missing, the order fix reports that dependency; it does not apply the declined field changes. Ambiguous alias columns, duplicate JSON keys, malformed structures, and missing or ambiguous project files are reported for manual correction rather than guessed at.

Approved edits preserve unrelated JSON values and save each changed file in a uniquely named `.bak` file under the project's `.oeps-backups` folder. Each file replacement is atomic. Both revision files are checked for changes before either is saved; if either changed while the confirmation was open, the prepared fix is rejected. If a later replacement fails, the app attempts to restore earlier replacements and reports any recovery problem. Project JSON formatting may be normalized; UTF-8 BOM and newline style are retained. To restore a backup, close the project in KiCad and copy the chosen `.bak` file over its original `.kicad_pro` or `.kicad_sch` file.

The schematic fix replaces only the revision string, preserving the title, company, comments, symbols, wiring and surrounding text. It can add a missing `rev` or `title_block`. Duplicate or malformed title-block revision settings are reported for manual correction.

The final report keeps the check results and lists fixed, skipped, or unsuccessful actions. Save your work in KiCad before configuring files, and reopen the project in KiCad afterward to load the saved settings. Configure files edits `.kicad_pro` settings, the main schematic's title-block revision, and the main PCB's plot options. PCB fixes use the same confirmation, original-file backup and stale-file protection. Duplicate or structured plot values are reported for manual correction.

Component identifier validation runs after BOM generation, as described below. These checks have no automatic fixes.

## Generate production files

Click **Generate production files** to export the BOM first, followed by the selected file types. When Gerbers are selected, an OK/Cancel popup first asks you to open the board, make sure zones were filled, validate **Include Layers**, and save the board. Cancel stops the entire operation before cleanup or export. The reminder is skipped when Gerbers are unchecked.

The CLI path, BOM configuration, main schematic, and support for every selected export are checked before cleanup. If deletion is selected, the app clears the entire contents of the selected project's `manufacturing` folder, including subfolders, once before generation. With deletion unchecked, unrelated files remain and matching generated filenames are replaced. The main schematic and board must have the same filename stem as the single `.kicad_pro` file. Save schematic, project settings and PCB changes in KiCad first; exports read the saved files.

**BOM** goes to `manufacturing/bom/<project-name>.csv`. `BomFilesGenerator.cs` runs `sch export bom` on the main schematic, letting KiCad load its linked sheets. It passes the saved Symbol Fields Table's included fields, labels, order, grouping fields (including hidden grouping fields), filter and DNP selection from `.kicad_pro`. It uses the configured comma-separated, quoted CSV format, with comma-separated references and no reference ranges. Reference and `${QUANTITY}` must be included. Missing labels use the standard field labels. This uses KiCad's native Symbol Fields Table exporter.

Ascending sorting and the existing Export configuration rules must be satisfied before generation, even with the override selected. The command uses KiCad's default ascending sort because passing `--sort-asc true` crashes the installed KiCad 10.0.3. The deprecated option to include symbols excluded from BOM cannot be enabled on that CLI. Unsupported or ambiguous field names/labels are reported before cleanup.

The BOM **Component count** is the sum of Qty, so a grouped row containing five references counts as five components. The exported Reference list is checked against each row's quantity. When placement export is selected, `BomPlacementComparison.cs` compares the two newly generated reference lists, including duplicate occurrences. A count mismatch displays a warning popup with both counts. The report lists references present only in the BOM and references present only in placements, even when the totals happen to match. Matching files show only `[PASSED] BOM / placement comparison`. If placement export is unchecked, no comparison is made against old files. A BOM export failure stops the remaining exports.

Three **read-only checks** run immediately after every successful BOM export, including BOM-only generation. Each has its own source file in `src/Oeps.KicadProductionFiles.Core/CheckProductionFiles/`:

| Report header | Source | Validation |
| --- | --- | --- |
| BOM required fields: OEPS PN, MPN and OEPS Description | `BomIdentifiersCheck.cs` | Every BOM reference must have usable OEPS PN (or OEPSPN), MPN and OEPS Description values. Lists missing columns/values, invalid content, conflicting aliases and ambiguous BOM references. Does not need the database or PCB. |
| BOM vs database: OEPS PN, MPN and OEPS Description | `BomDatabaseIdentifiersCheck.cs` | The PN/MPN pair must exist in the database. OEPS Description must match database Description for that same pair. Reports unknown PNs, wrong pairings, missing database descriptions and mismatched descriptions with expected values. Incomplete BOM values refer to the required-fields check instead of repeating its missing-field list. |
| BOM vs layout: footprints, OEPS PN, MPN and OEPS Description | `BomLayoutIdentifiersCheck.cs` | Exactly one matching footprint must exist in the main PCB; it must contain OEPS PN, MPN and OEPS Description, and all three values must match the BOM. Reports missing/duplicate footprints, missing layout properties and differing values. This check compares the generated BOM with the PCB and does not consult the database. |

The schematic source is the **freshly generated BOM**, which includes components from the main schematic and all linked sheets according to its saved inclusion/filter settings. Grouped rows are expanded by reference; fields are identified by their KiCad names, so custom CSV labels do not affect validation. PCB properties are read even when hidden. Footprints outside the BOM are outside these identifier checks; the existing BOM/placement comparison reports additional placement references.

The checks accept `OEPS PN` and `OEPSPN`. If both have different nonempty values, the ambiguity is reported. Values are compared exactly after trimming outer whitespace, preserving case, leading zeros, punctuation and internal spacing. Missing values on both sides do not count as a match; duplicate properties, control characters and unresolved text variables are reported. The standard Description field is not a substitute for OEPS Description. If a BOM value is missing, layout presence and other available fields are still checked, but the layout result stays failed with a comparison-incomplete note referring to **BOM required fields: OEPS PN, MPN and OEPS Description**. It does not repeat the BOM's per-component missing-field messages or claim that uncomparable values differ. The database check uses one snapshot of the app's validated database/cache for the run, retaining the existing five-minute refresh interval. If no database is available, it fails with an instruction to update the database; layout comparison still runs independently.

Passing checks show only their **✓ [PASSED]** header. Failed checks show **✗ [FAILED]** and the affected references; the GUI status indicates that component checks failed. These failures do not stop the other selected exports. No repair is registered and no fix popup is shown for these checks. Correct the schematic/PCB fields manually, save in KiCad, and generate again to recheck. They do not use a previously saved BOM in the configuration-check or Configure files workflow.

**Gerber files** go to `manufacturing/gerber/`. `GerberFilesGenerator.cs` runs `pcb export gerbers --board-plot-params --check-zones --output <temporary-directory> <board>`, using KiCad's saved board plot settings and layer selection. The default variant is used. KiCad 10 CLI creates an auxiliary Gerber job file even when the board's `creategerberjobfile` is off; the app publishes it only when explicitly enabled in the board. CLI option behavior is documented in the [KiCad 10 CLI manual](https://docs.kicad.org/10.0/en/cli/cli.html).

**Drill files and maps** also go to `manufacturing/gerber/`. `DrillFilesGenerator.cs` uses explicit options: Excellon, inches, decimal coordinates, drill/place origin, separate PTH/NPTH files, routed oval holes (alternate mode off), and Gerber X2 maps. Mirror Y, minimal header and tenting switches are omitted. Its command is `pcb export drill --format excellon --drill-origin plot --excellon-zeros-format decimal --excellon-oval-format route --excellon-units in --excellon-separate-th --generate-map --map-format gerberx2 --output <temporary-directory> <board>`. KiCad's shared `pcbnew.json` preferences are not edited. The former app drill-profile check, fix and initialization have been removed; any old `drill-export-settings.json` is unused.

**IPC-D-356 netlist** goes directly to `manufacturing/<project-name>.d356`. `IpcD356FilesGenerator.cs` runs `pcb export ipcd356 --output <temporary-file> <board>`. It checks that the output exists and includes its header and end marker before publishing it. The new checkbox starts checked; IPC-only generation does not display the Gerber reminder.

The placement generator writes `manufacturing/assembly/<project-name>-pos.csv` with these settings:

| Setting | Value |
| --- | --- |
| Design variant | Default (no variant override) |
| Format / units | CSV / millimeters |
| Board sides | Front and back in one file |
| Include only SMD | False |
| Exclude footprints with through-hole pads | False |
| Exclude DNP | False |
| Exclude from BOM filter | False |
| Use drill/place origin | True |
| Negate bottom X | False |

The CLI invocation is `pcb export pos --format csv --units mm --side both --use-drill-file-origin --output <csv> <board>`. Exclusion and negative-X switches are omitted.

The report lists each generated file under its export's header, with component counts for the BOM and placements, or reports the CLI error. Each exporter writes to an empty temporary directory first, validates the output, then publishes it. Basic format checks detect missing, empty or truncated Gerbers, incorrect Excellon units/zero format, missing PTH/NPTH files/maps, invalid CSV headers/rows, and invalid BOM quantities. CSV parsing handles quoted commas and escaped quotes; embedded line breaks do not inflate placement counts. These checks do not validate manufacturing correctness or board geometry. Failed CLI exports cannot pass using stale files or replace existing output. Successfully generated earlier files and their comparison remain in the report if a later export fails. Errors report whether manufacturing was already cleared. Cleanup is confined to the selected project's `manufacturing` directory and refuses linked paths. Other project files and `.oeps-backups` are retained.

Each generation has its own source file under `src/Oeps.KicadProductionFiles.Core/GenerateProductionFiles/`: **`BomFilesGenerator.cs`**, **`GerberFilesGenerator.cs`**, **`PlacementFilesGenerator.cs`**, **`DrillFilesGenerator.cs`**, and **`IpcD356FilesGenerator.cs`**. `BomPlacementComparison.cs` compares component counts and references. `ProductionGenerationRunner.cs` selects generators and handles optional cleanup once before the sequence. `ExportOutputDirectory.cs` handles temporary outputs, validation and publication.

## Run from this checkout

Double-click **`Run.cmd`**. It builds and opens the app using .NET 10. It finds a local `.tools/dotnet` SDK, the existing SDK in the sibling `raw-material-sticker` folder, or an SDK on `PATH`.

For the already downloaded development cache:

```bat
Run.cmd --data-dir .local/data
```

For an offline UI demonstration with clearly labeled sample data:

```bat
Run.cmd --sample --data-dir .local/demo
```

## Shared component database

The app uses the same Google spreadsheet and `components list` tab as `raw-material-sticker`. It selects column A (description), B (OEPS PN), and D (MPN). Part numbers remain strings, including leading zeros. Multiple valid MPNs for one OEPS PN remain separate pairings.

- The CSV is refreshed every **five minutes** while the app is open; the button forces an immediate refresh.
- Startup loads the last valid cache and refreshes it if due.
- Invalid downloads, offline connections, or timeouts retain the last valid CSV and display the refresh failure.
- Downloads have a 40-second deadline and a 20 MiB limit. A validated CSV replaces the old file atomically.
- The footer shows sync status and the countdown to the next attempt; hover for cache details and errors.

Normal application data is stored separately from the sticker app:

```text
%LOCALAPPDATA%\OEPS\KicadProductionFiles\
  components.csv
  user-settings.json
  appsettings.json       (optional configuration overrides)
```

`--data-dir` changes the data directory for source runs or diagnostics. A verified live CSV is also saved in this checkout at `.local/data/components.csv` (ignored by Git). Live CSV data and user settings are excluded from release packages.

Packaged `appsettings.json` contains the spreadsheet URL, header aliases, and release repository. An `appsettings.json` in the data directory overrides individual values. See `config.example.json` for the available settings.

## Launcher, installer, and updates

The launcher and installer follow the sticker app's model, using this application's own name, shortcuts, data directory, and `oeps-tech/generate-kicad-production-files` release repository.

Build the MSI installer and application update ZIP with the .NET 10 x64 SDK and Desktop Runtime:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/package.ps1
```

The script builds the solution, runs the console verification suite, and writes these files under `artifacts/`. Existing versioned artifacts are protected from accidental replacement.

- `Oeps.KicadProductionFiles-0.1.1-setup-win-x64.msi` and `.msi.sha256`: Windows installer with a private .NET 10 runtime.
- `Oeps.KicadProductionFiles-0.1.1-win-x64.zip` and `.zip.sha256`: small application package used by the updater.

Double-click the MSI to install for the current Windows user. It registers in Windows Installed apps and creates desktop and Start-menu shortcuts that open `Oeps.KicadProductionFiles.Launcher.exe` directly. Installation and normal startup do not run PowerShell or download a runtime; no SDK is needed on the target PC. PowerShell is only used for source development and CI packaging. `scripts/Build-Msi.ps1` installs the pinned WiX 4.0.6 build tool into `.tools/wix` on first use and bundles only the .NET host, Core runtime and Desktop runtime from the build SDK, excluding SDKs and ASP.NET.

Install the MSI over the older ZIP-based setup to replace its PowerShell shortcuts. Existing preferences, component cache, installed app versions and recovery state remain in `%LOCALAPPDATA%\OEPS\KicadProductionFiles`. Previously pinned taskbar shortcuts may need to be replaced with the new shortcut.

Windows Installer manages the launcher, runtime and bundled update package in `%LOCALAPPDATA%\OEPS KiCad Production Files Installer`. Uninstall removes those files and the shortcuts, preserving the separate user data and app version history. New MSI versions upgrade the installer-managed files; publish a new MSI to service the private runtime. Ordinary app ZIP updates only update the app. This product has separate installer and component identities from Raw Material Sticker so both can coexist.

Packages are currently unsigned. The MSI removes the old PowerShell startup dependency; antivirus acceptance still needs verification on the affected computers.

The desktop shortcut starts the updater. Updates are checked against stable GitHub releases, validated using checksums and package metadata, and promoted only after the new app signals readiness. The installed/previous version remains available for fallback. The GUI also checks for an available update every 30 minutes and explains how to apply it via the shortcut. Source `Run.cmd` opens the development build directly.

The release repository is public, so the updater can discover published releases without GitHub credentials. Use the MSI for first-time installation and for updates to the bundled launcher/runtime; ordinary app updates use the smaller ZIP package. See [v0.1.0 release notes](docs/releases/v0.1.0.md) for the KiCad IPC-D-356 via-field issue and recommended KiCad version.

The included GitHub workflow builds and verifies pushes/PRs. Pushing a stable `vX.Y.Z` tag publishes the matching release artifacts. No release is published merely by building locally.

## Extending the workflows

```text
src/
  Oeps.KicadProductionFiles.App/       Windows Forms UI and action wiring
  Oeps.KicadProductionFiles.Core/
    Configuration/                   Paths, settings and configuration
    Data/                            Spreadsheet parsing and CSV cache
    Kicad/                           Project settings reader and CLI execution
    CheckFilesConfiguration/         One source file per configuration check
    ConfigureFiles/                  One source file per fix, confirmation workflow and project saving
    GenerateProductionFiles/         One source file per generator and shared manufacturing cleanup
    Checks/                          Shared reports and future production checks
    Updates/                         Release download and recovery
  Oeps.KicadProductionFiles.Launcher/ Startup and update orchestration
tests/Oeps.KicadProductionFiles.Tests/
```

Each file check implements `IFileCheck` and returns a named `CheckResult` with status and detail. The button's checks belong in `Core/CheckFilesConfiguration/`: `SymbolFieldsTableCheck.cs` checks the fields, `EditTabMetadataCheck.cs` checks the Edit tab options, `ExportConfigurationCheck.cs` checks the output filename and format, `FieldOrderCheck.cs` checks the included column sequence, and `RevisionCheck.cs` checks the saved revision against the GUI entry. `ConfigurationCheckRunner.cs` registers them. Add future configuration tests as separate source files and register them there.

The earlier setup/production check infrastructure remains available in `Core/Checks/` for later workflows:

- `KicadCliCheck.cs`
- `ProjectFilesCheck.cs`
- `ComponentDatabaseCheck.cs`
- `BomPositionCountCheck.cs` (pending comparison policy)
- `ComponentIdentifiersCheck.cs` (pending validation policy)

The configuration button reports a missing or ambiguous project when the selected folder contains zero or multiple `.kicad_pro` files. Component identifier validation runs with production generation; additional production generators can be added independently.

## Verification

With .NET 10 on `PATH` (or substitute the SDK found by `Run.cmd`):

```powershell
dotnet build Oeps.KicadProductionFiles.sln -c Release
dotnet run --project tests/Oeps.KicadProductionFiles.Tests -c Release
dotnet run --project tests/Oeps.KicadProductionFiles.Tests -c Release -- --verify-live .local/data
dotnet run --project tests/Oeps.KicadProductionFiles.Tests -c Release -- --verify-configuration "kicad to work on"
Run.cmd --sample --ui-smoke --data-dir .local/ui-smoke
```

The tests use a dependency-free console harness, so use `dotnet run` for verification. Live verification accesses the shared spreadsheet. UI smoke uses sample data, runs hidden, records its results and a screenshot, and exits. See `docs/verification-results.md` for the checks completed on this implementation.
