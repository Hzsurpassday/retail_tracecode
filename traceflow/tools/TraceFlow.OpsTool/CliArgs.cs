namespace TraceFlow.OpsTool;

/// <summary>
/// 极简命令行解析：<c>--name value</c> / <c>--flag</c> / <c>--name=value</c>，其余为位置参数。
/// 连接串含 '='/'&amp;'，只按首个 '=' 切分；连接串作为独立参数传入（脚本侧负责引号）。
/// </summary>
internal sealed class CliArgs
{
    private readonly Dictionary<string, string?> _options = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _flags = new(StringComparer.OrdinalIgnoreCase);

    public List<string> Positionals { get; } = new();

    public static CliArgs Parse(IReadOnlyList<string> args, params string[] flags)
    {
        var result = new CliArgs();
        var flagSet = new HashSet<string>(
            flags.Select(f => f.StartsWith("--", StringComparison.Ordinal) ? f[2..] : f),
            StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < args.Count; i++)
        {
            var token = args[i];
            if (!token.StartsWith("--", StringComparison.Ordinal))
            {
                result.Positionals.Add(token);
                continue;
            }

            var body = token[2..];
            var eq = body.IndexOf('=');
            if (eq >= 0)
            {
                result._options[body[..eq]] = body[(eq + 1)..];
                continue;
            }
            if (flagSet.Contains(body))
            {
                result._flags.Add(body);
                result._options[body] = null;
                continue;
            }
            if (i + 1 >= args.Count)
                throw new CommandException($"选项 --{body} 缺少取值", 2);
            result._options[body] = args[++i];
        }
        return result;
    }

    public bool Has(string name) => _options.ContainsKey(name);

    public string? Get(string name) => _options.TryGetValue(name, out var value) ? value : null;

    public bool Flag(string name) => _flags.Contains(name);

    public int GetInt(string name, int fallback, int min = 1)
    {
        var raw = Get(name);
        if (string.IsNullOrWhiteSpace(raw)) return fallback;
        if (!int.TryParse(raw, out var value) || value < min)
            throw new CommandException($"选项 --{name} 须为 ≥{min} 的整数（实际 {raw}）", 2);
        return value;
    }
}