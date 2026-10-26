namespace BackupPlatform.Tools;

/// <summary>Minimal <c>--name value</c> parser shared by the offline tools (compiled into each tool; they intentionally have no common assembly).</summary>
internal sealed class CliArguments
{
    private readonly Dictionary<string, List<string>> values = [];

    /// <summary>Parses <paramref name="args"/> after the optional leading command; every option takes exactly one value and may repeat.</summary>
    /// <returns>The parsed arguments, or <c>null</c> with <paramref name="error"/> set when an option is unknown, unvalued, or a stray token appears.</returns>
    public static CliArguments? Parse(IReadOnlyList<string> args, IReadOnlySet<string> options, out string? error)
    {
        var parsed = new CliArguments();
        error = null;
        for (var i = 0; i < args.Count; i++)
        {
            if (!options.Contains(args[i]) || i + 1 >= args.Count)
            {
                error = $"unexpected or incomplete argument '{args[i]}'";
                return null;
            }

            if (!parsed.values.TryGetValue(args[i], out var list))
            {
                parsed.values[args[i]] = list = [];
            }

            list.Add(args[++i]);
        }

        return parsed;
    }

    public IReadOnlyList<string> All(string option) => values.TryGetValue(option, out var list) ? list : [];

    public string? Single(string option) => All(option) is [var only] ? only : null;

    public bool Has(string option) => values.ContainsKey(option);
}
