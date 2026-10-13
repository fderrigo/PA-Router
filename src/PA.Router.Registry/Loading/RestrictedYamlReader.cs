using System.Globalization;
using System.Text.Json.Nodes;
using PA.Router.Abstractions.Contracts;
using YamlDotNet.Core;
using YamlDotNet.Core.Events;

namespace PA.Router.Registry.Loading;

/// <summary>
/// Documento letto: albero JSON piu' la riga di ogni proprieta', per poter indicare dove si trova
/// un errore senza stampare il valore.
/// </summary>
/// <param name="Root">Radice del documento.</param>
/// <param name="Lines">Riga per ogni percorso JSON Pointer.</param>
public sealed record ParsedDocument(JsonNode Root, IReadOnlyDictionary<string, long> Lines)
{
    /// <summary>Riga associata a un percorso, se nota.</summary>
    public long? LineOf(string pointer) => Lines.TryGetValue(pointer, out long line) ? line : null;
}

/// <summary>
/// Lettore YAML/JSON ristretto. Accetta il sottoinsieme che i contratti usano davvero e rifiuta
/// tutto il resto invece di interpretarlo: niente ancore e alias, niente tag espliciti, niente
/// chiavi di merge, niente documenti multipli, niente chiavi non stringa.
///
/// Le chiavi duplicate sono respinte qui, prima della deserializzazione: uno schema JSON non le
/// vedrebbe, perche' l'ultima sovrascriverebbe silenziosamente la precedente.
/// </summary>
public static class RestrictedYamlReader
{
    /// <summary>Profondita' massima ammessa, per fermare documenti costruiti per esaurire lo stack.</summary>
    public const int MaxDepth = 32;

    /// <summary>Numero massimo di nodi ammessi in un documento.</summary>
    public const int MaxNodes = 20_000;

    /// <summary>Legge un documento. Restituisce false e riempie <paramref name="issues"/> se non e' accettabile.</summary>
    public static bool TryRead(
        string source,
        string text,
        List<ValidationIssue> issues,
        out ParsedDocument? document)
    {
        ArgumentNullException.ThrowIfNull(issues);

        document = null;

        try
        {
            Reader reader = new(source, issues);
            return reader.TryRead(text, out document);
        }
        catch (YamlException ex)
        {
            issues.Add(new ValidationIssue(
                source,
                "",
                ex.Start.Line,
                ValidationCodes.Malformed,
                "The document is not readable as restricted YAML or JSON."));

            return false;
        }
    }

    private sealed class Reader(string source, List<ValidationIssue> issues)
    {
        private readonly Dictionary<string, long> lines = new(StringComparer.Ordinal);
        private int nodes;
        private bool failed;

        internal bool TryRead(string text, out ParsedDocument? document)
        {
            document = null;

            IParser parser = new Parser(new StringReader(text));

            Expect<StreamStart>(parser);

            if (!parser.Accept<DocumentStart>(out _))
            {
                Fail("", null, ValidationCodes.Malformed, "The document is empty.");
                return false;
            }

            Expect<DocumentStart>(parser);
            JsonNode? root = ReadNode(parser, "", depth: 0);
            Expect<DocumentEnd>(parser);

            if (parser.Accept<DocumentStart>(out DocumentStart? second))
            {
                Fail(
                    "",
                    second.Start.Line,
                    ValidationCodes.UnsupportedConstruct,
                    "A configuration file must contain exactly one document.");
            }

            if (failed || root is null)
            {
                return false;
            }

            document = new ParsedDocument(root, lines);
            return true;
        }

        private JsonNode? ReadNode(IParser parser, string pointer, int depth)
        {
            if (depth > MaxDepth)
            {
                Fail(pointer, parser.Current?.Start.Line, ValidationCodes.UnsupportedConstruct, "The document is nested too deeply.");
                Skip(parser);
                return null;
            }

            if (++nodes > MaxNodes)
            {
                Fail(pointer, parser.Current?.Start.Line, ValidationCodes.UnsupportedConstruct, "The document has too many nodes.");
                Skip(parser);
                return null;
            }

            if (parser.Accept<AnchorAlias>(out AnchorAlias? alias))
            {
                Fail(pointer, alias.Start.Line, ValidationCodes.UnsupportedConstruct, "YAML aliases are not allowed.");
                parser.MoveNext();
                return null;
            }

            if (parser.Accept<Scalar>(out Scalar? scalar))
            {
                parser.MoveNext();
                Record(pointer, scalar.Start.Line);
                RejectAnchorOrTag(pointer, scalar.Anchor, scalar.Tag, scalar.Start.Line);
                return ScalarToNode(scalar);
            }

            if (parser.Accept<SequenceStart>(out SequenceStart? sequenceStart))
            {
                parser.MoveNext();
                Record(pointer, sequenceStart.Start.Line);
                RejectAnchorOrTag(pointer, sequenceStart.Anchor, sequenceStart.Tag, sequenceStart.Start.Line);

                JsonArray array = [];
                int index = 0;

                while (!parser.Accept<SequenceEnd>(out _))
                {
                    array.Add(ReadNode(parser, pointer + "/" + index.ToString(CultureInfo.InvariantCulture), depth + 1));
                    index++;
                }

                parser.MoveNext();
                return array;
            }

            if (parser.Accept<MappingStart>(out MappingStart? mappingStart))
            {
                parser.MoveNext();
                Record(pointer, mappingStart.Start.Line);
                RejectAnchorOrTag(pointer, mappingStart.Anchor, mappingStart.Tag, mappingStart.Start.Line);

                JsonObject mapping = [];

                while (!parser.Accept<MappingEnd>(out _))
                {
                    if (!parser.Accept<Scalar>(out Scalar? key))
                    {
                        Fail(pointer, parser.Current?.Start.Line, ValidationCodes.UnsupportedConstruct, "Only string keys are allowed.");
                        Skip(parser);
                        Skip(parser);
                        continue;
                    }

                    parser.MoveNext();

                    if (string.Equals(key.Value, "<<", StringComparison.Ordinal))
                    {
                        Fail(pointer, key.Start.Line, ValidationCodes.UnsupportedConstruct, "YAML merge keys are not allowed.");
                        Skip(parser);
                        continue;
                    }

                    string childPointer = pointer + "/" + Escape(key.Value);

                    if (mapping.ContainsKey(key.Value))
                    {
                        Fail(childPointer, key.Start.Line, ValidationCodes.DuplicateKey, "The key appears more than once in the same object.");
                        Skip(parser);
                        continue;
                    }

                    mapping[key.Value] = ReadNode(parser, childPointer, depth + 1);
                }

                parser.MoveNext();
                return mapping;
            }

            Fail(pointer, parser.Current?.Start.Line, ValidationCodes.Malformed, "Unexpected document structure.");
            Skip(parser);
            return null;
        }

        private static JsonNode? ScalarToNode(Scalar scalar)
        {
            // Solo gli scalari non quotati sono soggetti alla risoluzione implicita: un valore
            // fra virgolette resta una stringa anche se sembra un numero.
            if (scalar.Style is not ScalarStyle.Plain)
            {
                return JsonValue.Create(scalar.Value);
            }

            string value = scalar.Value;

            if (value.Length == 0 || string.Equals(value, "null", StringComparison.Ordinal) || string.Equals(value, "~", StringComparison.Ordinal))
            {
                return null;
            }

            if (string.Equals(value, "true", StringComparison.Ordinal))
            {
                return JsonValue.Create(true);
            }

            if (string.Equals(value, "false", StringComparison.Ordinal))
            {
                return JsonValue.Create(false);
            }

            if (long.TryParse(value, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out long integer))
            {
                return JsonValue.Create(integer);
            }

            if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out double number))
            {
                return JsonValue.Create(number);
            }

            return JsonValue.Create(value);
        }

        private void RejectAnchorOrTag(string pointer, AnchorName anchor, TagName tag, long line)
        {
            if (!anchor.IsEmpty)
            {
                Fail(pointer, line, ValidationCodes.UnsupportedConstruct, "YAML anchors are not allowed.");
            }

            if (!tag.IsEmpty && !tag.IsNonSpecific)
            {
                Fail(pointer, line, ValidationCodes.UnsupportedConstruct, "Explicit YAML tags are not allowed.");
            }
        }

        private static void Skip(IParser parser)
        {
            if (parser.Current is null)
            {
                return;
            }

            parser.SkipThisAndNestedEvents();
        }

        private static void Expect<T>(IParser parser)
            where T : ParsingEvent
        {
            parser.Consume<T>();
        }

        private static string Escape(string key) =>
            key.Replace("~", "~0", StringComparison.Ordinal).Replace("/", "~1", StringComparison.Ordinal);

        private void Record(string pointer, long line)
        {
            if (pointer.Length > 0)
            {
                lines.TryAdd(pointer, line);
            }
        }

        private void Fail(string pointer, long? line, string code, string message)
        {
            failed = true;
            issues.Add(new ValidationIssue(source, pointer, line, code, message));
        }
    }
}
