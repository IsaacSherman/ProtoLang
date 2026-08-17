using System.Text;

namespace ProtoLang.Backend;

/// <summary>
/// Minimal indentation-aware text writer. Generated output must be byte-for-byte deterministic so
/// golden tests stay meaningful, so this always emits "\n" regardless of host platform.
/// </summary>
public sealed class SourceWriter
{
    private const string NewLine = "\n";

    private readonly StringBuilder _builder = new();
    private readonly string _indentUnit;
    private int _indent;
    private bool _atLineStart = true;

    public SourceWriter(string indentUnit = "    ") => _indentUnit = indentUnit;

    public void Indent() => _indent++;

    public void Unindent() => _indent = Math.Max(0, _indent - 1);

    public void Write(string text)
    {
        if (_atLineStart && text.Length > 0)
        {
            for (var i = 0; i < _indent; i++)
            {
                _builder.Append(_indentUnit);
            }

            _atLineStart = false;
        }

        _builder.Append(text);
    }

    public void WriteLine(string text = "")
    {
        if (text.Length > 0)
        {
            Write(text);
        }

        _builder.Append(NewLine);
        _atLineStart = true;
    }

    /// <summary>Opens a brace-delimited block and indents until disposed.</summary>
    /// <param name="header">Text written before the opening brace, on its own line.</param>
    /// <param name="closer">
    /// Replacement for the default closing <c>}</c>, for trailing comments such as
    /// <c>}  // namespace foo</c>.
    /// </param>
    public IDisposable Block(string header, string? closer = null)
    {
        WriteLine(header);
        WriteLine("{");
        Indent();
        return new BlockScope(this, closer ?? "}");
    }

    public override string ToString() => _builder.ToString();

    private sealed class BlockScope : IDisposable
    {
        private readonly SourceWriter _writer;
        private readonly string _closer;

        public BlockScope(SourceWriter writer, string closer)
        {
            _writer = writer;
            _closer = closer;
        }

        public void Dispose()
        {
            _writer.Unindent();
            _writer.WriteLine(_closer);
        }
    }
}
