using System.Text.Json;

namespace Goggles.Cli;

/// <summary>
/// Prints the shape of an unknown JSON document — keys, types, array sizes, and a sample of each
/// leaf value. This is the whole point of the manifest spike: see the format before modelling it.
/// </summary>
internal static class JsonOutline
{
    public static void Print(JsonElement element, int maxDepth = 4, TextWriter? writer = null) =>
        Write(writer ?? Console.Out, element, name: "$", depth: 0, maxDepth);

    private static void Write(TextWriter output, JsonElement element, string name, int depth, int maxDepth)
    {
        var indent = new string(' ', depth * 2);

        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                var properties = element.EnumerateObject().ToList();
                output.WriteLine($"{indent}{name}  {{{properties.Count} keys}}");

                if (depth >= maxDepth)
                {
                    output.WriteLine($"{indent}  …");
                    return;
                }

                foreach (var property in properties)
                {
                    Write(output, property.Value, property.Name, depth + 1, maxDepth);
                }

                break;

            case JsonValueKind.Array:
                var items = element.EnumerateArray().ToList();
                output.WriteLine($"{indent}{name}  [{items.Count}]");

                if (items.Count == 0 || depth >= maxDepth)
                {
                    return;
                }

                // One representative element is enough to learn the shape.
                Write(output, items[0], "[0]", depth + 1, maxDepth);

                if (items.Count > 1)
                {
                    output.WriteLine($"{indent}  … {items.Count - 1} more");
                }

                break;

            default:
                output.WriteLine($"{indent}{name}  {Describe(element)}");
                break;
        }
    }

    private static string Describe(JsonElement element)
    {
        var raw = element.ToString();
        var sample = raw.Length > 60 ? string.Concat(raw.AsSpan(0, 59), "…") : raw;

        return element.ValueKind switch
        {
            JsonValueKind.String => $"string  \"{sample}\"",
            JsonValueKind.Number => $"number  {sample}",
            JsonValueKind.True or JsonValueKind.False => $"bool    {sample}",
            JsonValueKind.Null => "null",
            _ => sample,
        };
    }
}
