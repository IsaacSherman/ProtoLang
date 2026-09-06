using System.Globalization;
using ProtoLang.Binding;
using ProtoLang.LanguageServer.Protocol.Lsp;
using ProtoLang.LanguageServer.Workspace;
using Range = ProtoLang.LanguageServer.Protocol.Lsp.Range;

namespace ProtoLang.LanguageServer.Hosting;

/// <summary>
/// Everything one compilation has to say, turned into diagnostics filed under the files they are
/// about.
/// </summary>
/// <remarks>
/// <para>
/// <b>Where a protoc failure appears.</b> Both in the <c>.proto</c> protoc named and on the
/// <c>import proto</c> line that pulled it in. #42 originally accepted the import line alone as a beta
/// floor, on the grounds that publishing precisely needed protoc's standard error parsed into
/// locations -- which #48 has since done, so the reason for settling has gone. The import line stays
/// because a user looking at a ProtoLang buffer whose schema is broken must not see an empty Problems
/// list, and because a transitively imported schema is reached through an import that names a
/// different file, which the wording says out loud rather than leaving the reader to work out.
/// </para>
/// <para>
/// <b>PL0003 is replaced, not supplemented, when protoc said anything structured.</b> Its message is
/// the whole of standard error, so publishing it beside the per-line diagnostics parsed from that same
/// text would say everything twice. When protoc could not be found at all there is no structured
/// output, and PL0003 -- which then carries <c>ProtocLocator</c>'s account of everywhere it looked --
/// is published exactly as the command line prints it.
/// </para>
/// </remarks>
public static class CompilationDiagnostics
{
    /// <summary>The code the compiler reports a schema-load failure under.</summary>
    private const string SchemaLoadFailed = "PL0003";

    /// <summary>What protoc's own messages are attributed to.</summary>
    /// <remarks>
    /// Not a <c>PL</c> code. protoc's errors have no code in this compiler's numbering, and giving
    /// them one would be ProtoLang inventing a taxonomy for another tool's output -- the same
    /// reasoning <see cref="ProtocDiagnostic"/> already applies to severity.
    /// </remarks>
    public const string ProtocSource = "protoc";

    /// <param name="resolvePaths">
    /// The roots protoc's file names are resolved against: the compilation's own search paths plus
    /// the loader's implicit ones, in that order. It must be the list the compilation used, or a
    /// well-known schema resolves here to a different file than the one protoc read.
    /// </param>
    public static DiagnosticContribution Build(
        CompilationResult result,
        DocumentUri owner,
        IReadOnlyList<string> resolvePaths,
        DiagnosticMapper mapper)
    {
        ArgumentNullException.ThrowIfNull(result);
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentNullException.ThrowIfNull(mapper);

        var contribution = new DiagnosticContribution();

        // Claimed even when nothing is wrong, so that a document whose last error was just fixed is
        // published with an empty list rather than keeping its squiggles.
        contribution.Claim(owner);

        var protoc = result.SchemaFailure is { Output.Count: > 0 } failure ? failure.Output : null;

        foreach (var diagnostic in result.Diagnostics)
        {
            if (protoc is not null && string.Equals(diagnostic.Code, SchemaLoadFailed, StringComparison.Ordinal))
            {
                continue;
            }

            contribution.Add(owner, mapper.Map(diagnostic, owner.Text));
        }

        if (protoc is not null)
        {
            Route(protoc, result, owner, resolvePaths, contribution);
        }

        return contribution;
    }

    /// <summary>Files each line protoc wrote against the schema it blamed and against the import.</summary>
    private static void Route(
        IReadOnlyList<ProtocDiagnostic> output,
        CompilationResult result,
        DocumentUri owner,
        IReadOnlyList<string> resolvePaths,
        DiagnosticContribution contribution)
    {
        var texts = new Dictionary<string, string?>(PathIdentity.Comparer);

        foreach (var entry in output)
        {
            var schema = Locate(entry.File, resolvePaths);
            var import = ImportFor(result, schema);

            Location? location = null;

            if (schema is not null && entry.HasPosition && DocumentUri.TryParse(schema, out var schemaUri))
            {
                var range = RangeIn(schema, entry, texts);
                location = new Location(schemaUri.Text, range);

                contribution.Add(
                    schemaUri,
                    new Diagnostic
                    {
                        Range = range,
                        Severity = DiagnosticSeverity.Error,
                        Source = ProtocSource,
                        Message = entry.Text,
                    });
            }

            contribution.Add(owner, OnTheImportLine(entry, import, schema, location));
        }
    }

    /// <summary>
    /// The one diagnostic the ProtoLang buffer always gets, whatever else was published elsewhere.
    /// </summary>
    /// <remarks>
    /// Attached to the import that reached the failing schema when one can be identified, and to the
    /// first import otherwise -- there always is one, because a compilation with no imports never
    /// reaches protoc. When the blamed file is not the imported file the message says so, because a
    /// squiggle on <c>import proto "invoice.proto"</c> reporting an error in <c>money.proto</c> is
    /// otherwise simply confusing.
    /// </remarks>
    private static Diagnostic OnTheImportLine(
        ProtocDiagnostic entry,
        ImportResolution? import,
        string? schema,
        Location? location)
    {
        var blamed = entry.File ?? schema;
        var reachedDirectly = blamed is not null
            && import?.ResolvedPath is { } resolved
            && PathIdentity.AreSame(resolved, schema);

        var at = entry.HasPosition
            ? string.Create(CultureInfo.InvariantCulture, $" at line {entry.Line}, column {entry.Column}")
            : string.Empty;

        var message = blamed is null
            ? $"protoc rejected the schemas for this file: {entry.Text}"
            : reachedDirectly
                ? $"protoc rejected '{entry.File}'{at}: {entry.Text}"
                : $"protoc rejected '{entry.File}'{at}, which this import reaches: {entry.Text}"
                    + "\nThe problem is in that file rather than on this line; this import is what pulled it in.";

        return new Diagnostic
        {
            Range = import is null ? DiagnosticMapper.WholeDocumentStart : DiagnosticMapper.RangeOf(import.Span),
            Severity = DiagnosticSeverity.Error,
            Source = ProtocSource,
            Message = message,
            RelatedInformation = location is null ? null : [new DiagnosticRelatedInformation(location, entry.Text)],
        };
    }

    /// <summary>The file protoc's name refers to, or null when it names none that exists.</summary>
    /// <remarks>
    /// Through <see cref="SchemaLookup"/>, which is the rule protoc itself follows -- first root wins.
    /// Predicting protoc's own answer is the point: resolving by some other reasonable rule would put
    /// the diagnostic in a file protoc did not read.
    /// </remarks>
    private static string? Locate(string? file, IReadOnlyList<string> resolvePaths)
        => string.IsNullOrWhiteSpace(file) ? null : SchemaLookup.Find(file, resolvePaths);

    /// <summary>Which import reached this schema, or the first one when that cannot be told.</summary>
    private static ImportResolution? ImportFor(CompilationResult result, string? schema)
    {
        if (result.Imports.Count == 0)
        {
            return null;
        }

        if (schema is not null)
        {
            foreach (var import in result.Imports)
            {
                if (import.ResolvedPath is { } resolved && PathIdentity.AreSame(resolved, schema))
                {
                    return import;
                }
            }
        }

        return result.Imports[0];
    }

    /// <summary>
    /// Where in the schema to draw the squiggle: from protoc's column to the end of that line.
    /// </summary>
    /// <remarks>
    /// protoc gives a point, and a point renders as a marker narrow enough to miss. The rest of the
    /// line is the smallest honest widening of it -- protoc's own command-line rendering underlines
    /// from the column onward for the same reason. A schema that cannot be read falls back to the
    /// point itself rather than failing the diagnostic; the text is what matters and it is already
    /// carried.
    /// </remarks>
    private static Range RangeIn(string schema, ProtocDiagnostic entry, Dictionary<string, string?> texts)
    {
        var start = new Position(Math.Max(entry.Line - 1, 0), Math.Max(entry.Column - 1, 0));

        if (!texts.TryGetValue(schema, out var text))
        {
            text = ReadOrNull(schema);
            texts[schema] = text;
        }

        if (text is null)
        {
            return new Range(start, start);
        }

        var lines = new ProtoLang.Diagnostics.LineMap(text);
        var offset = lines.OffsetOf(entry.Line, entry.Column);
        var end = lines.PositionOf(EndOfLine(text, offset));

        return new Range(start, new Position(Math.Max(end.Line - 1, 0), Math.Max(end.Column - 1, 0)));
    }

    private static int EndOfLine(string text, int offset)
    {
        var newline = text.IndexOf('\n', offset);
        var end = newline < 0 ? text.Length : newline;

        return end > offset && text[end - 1] == '\r' ? end - 1 : end;
    }

    private static string? ReadOrNull(string path)
    {
        try
        {
            return File.ReadAllText(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return null;
        }
    }
}
