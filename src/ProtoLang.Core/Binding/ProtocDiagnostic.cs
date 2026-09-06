namespace ProtoLang.Binding;

/// <summary>
/// One line of protoc's error output, kept in the shape it was written in: the file it blames, where
/// in that file, and what it said.
/// </summary>
/// <remarks>
/// <para>
/// protoc reports against the <c>.proto</c>, and this compiler used to flatten the whole of that into
/// the prose of a single exception message. Everything a reader could act on -- which schema, which
/// line -- survived only as text inside a sentence, so publishing a protoc error on the line that
/// caused it meant re-parsing that sentence somewhere downstream. Splitting it here means the
/// structure is recovered once, at the only place that knows it came from protoc.
/// </para>
/// <para>
/// <see cref="Raw"/> is kept whatever happens. protoc's output is not a specified format, and a line
/// this parser does not recognize -- a runtime log line, an indented continuation of the message
/// above it -- must still reach a reader intact rather than being dropped for failing to match. That
/// is also why severity is not modelled: protoc spells a warning as a <c>warning:</c> prefix inside
/// the message text, and inventing a severity enum here would be this compiler guessing at a
/// convention it does not own. #42 maps these onto LSP diagnostics and owns that decision.
/// </para>
/// </remarks>
/// <param name="File">
/// The schema protoc blamed, as protoc spelled it -- relative to a proto root, which is exactly the
/// spelling the compiler handed it. Null when the line named no file.
/// </param>
/// <param name="Line">The 1-based line, or 0 when protoc gave no position.</param>
/// <param name="Column">The 1-based column, or 0 when protoc gave no position.</param>
/// <param name="Text">The message with the file and position stripped off, or the whole line.</param>
/// <param name="Raw">The line exactly as protoc wrote it.</param>
public sealed record ProtocDiagnostic(string? File, int Line, int Column, string Text, string Raw)
{
    /// <summary>Whether protoc said where in the file the problem is.</summary>
    /// <remarks>
    /// Zero rather than null for a missing position, matching
    /// <see cref="Diagnostics.SourceSpan.None"/>, which is line 0 for the same reason: one
    /// out-of-band value that every consumer tests for in one way, rather than a nullable pair whose
    /// two halves can be inconsistent with each other.
    /// </remarks>
    public bool HasPosition => Line > 0;

    /// <summary>Splits protoc's standard error into one entry per line.</summary>
    /// <remarks>
    /// Blank lines are dropped and nothing else is. Lines are read independently, so a message protoc
    /// wrapped across two lines arrives as two entries with the second carrying no position -- which
    /// is worse than understanding the wrapping and better than discarding half of it.
    /// </remarks>
    public static IReadOnlyList<ProtocDiagnostic> Parse(string stderr)
    {
        ArgumentNullException.ThrowIfNull(stderr);

        return
        [
            .. stderr
                .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
                .Where(line => !string.IsNullOrWhiteSpace(line))
                .Select(ParseLine)
        ];
    }

    private static ProtocDiagnostic ParseLine(string line)
    {
        if (TryReadPosition(line, out var located))
        {
            return located;
        }

        if (TryReadFileOnly(line, out var blamed))
        {
            return blamed;
        }

        return new ProtocDiagnostic(null, 0, 0, line, line);
    }

    /// <summary>Reads the <c>file:line:column: message</c> form.</summary>
    /// <remarks>
    /// The separator is looked for from the left and each candidate is tested for digits on both
    /// sides, rather than splitting on the first colon or the last. A Windows absolute path carries a
    /// colon of its own -- <c>C:\schemas\invoice.proto:12:5:</c> -- so splitting on the first colon
    /// yields the drive letter as the file name, and splitting on the last yields the column. Only
    /// "the first colon that is followed by two numbers and a colon" identifies the right one.
    /// </remarks>
    private static bool TryReadPosition(string line, out ProtocDiagnostic diagnostic)
    {
        for (var separator = line.IndexOf(':'); separator >= 0; separator = line.IndexOf(':', separator + 1))
        {
            var rest = line.AsSpan(separator + 1);

            var lineDigits = CountLeadingDigits(rest);
            if (lineDigits == 0 || rest.Length <= lineDigits || rest[lineDigits] != ':')
            {
                continue;
            }

            var afterLine = rest[(lineDigits + 1)..];
            var columnDigits = CountLeadingDigits(afterLine);
            if (columnDigits == 0 || afterLine.Length <= columnDigits || afterLine[columnDigits] != ':')
            {
                continue;
            }

            var file = line[..separator];
            if (file.Length == 0)
            {
                continue;
            }

            // Parsed rather than assumed to fit. Nothing bounds how many digits a line of text can
            // hold, and this text arrives from another process -- so a run of twenty digits is a
            // number this parser declines to read, not an exception thrown out of a compiler that
            // may not throw on input. Declining leaves the line to be kept whole, which is the right
            // answer for something that was never a position to begin with.
            if (!int.TryParse(rest[..lineDigits], out var lineNumber)
                || !int.TryParse(afterLine[..columnDigits], out var column))
            {
                continue;
            }

            diagnostic = new ProtocDiagnostic(
                file,
                lineNumber,
                column,
                afterLine[(columnDigits + 1)..].Trim().ToString(),
                line);
            return true;
        }

        diagnostic = null!;
        return false;
    }

    /// <summary>Reads the <c>file: message</c> form, which is what an unresolvable import gets.</summary>
    /// <remarks>
    /// A file is only claimed when the text before the separator ends in <c>.proto</c>. protoc's own
    /// runtime log lines start with a bracketed level and contain colons of their own, and calling
    /// the first of those a file name would put a diagnostic on a schema that has nothing to do with
    /// the problem -- which is worse than leaving the line unattributed.
    /// </remarks>
    private static bool TryReadFileOnly(string line, out ProtocDiagnostic diagnostic)
    {
        var separator = line.IndexOf(": ", StringComparison.Ordinal);
        var file = separator > 0 ? line[..separator] : string.Empty;

        if (file.EndsWith(".proto", StringComparison.OrdinalIgnoreCase))
        {
            diagnostic = new ProtocDiagnostic(file, 0, 0, line[(separator + 2)..].Trim(), line);
            return true;
        }

        diagnostic = null!;
        return false;
    }

    private static int CountLeadingDigits(ReadOnlySpan<char> text)
    {
        var count = 0;
        while (count < text.Length && char.IsAsciiDigit(text[count]))
        {
            count++;
        }

        return count;
    }
}
