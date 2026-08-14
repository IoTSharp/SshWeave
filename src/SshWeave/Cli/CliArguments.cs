using SshWeave.Configuration;

namespace SshWeave.Cli;

public sealed class CliArguments
{
    private Dictionary<string, string?> _options = new(StringComparer.Ordinal);

    private CliArguments()
    {
    }

    public IReadOnlyList<string> Positionals { get; private init; } = [];

    public IReadOnlyList<string> Tail { get; private init; } = [];

    public string? GetValue(string name) => _options.GetValueOrDefault(name);

    public bool HasFlag(string name) => _options.ContainsKey(name);

    public static CliArguments Parse(
        IReadOnlyList<string> arguments,
        IEnumerable<string> valueOptions,
        IEnumerable<string>? flags = null,
        bool allowTail = false)
    {
        HashSet<string> values = valueOptions.ToHashSet(StringComparer.Ordinal);
        HashSet<string> switches = (flags ?? []).ToHashSet(StringComparer.Ordinal);
        Dictionary<string, string?> options = new(StringComparer.Ordinal);
        List<string> positionals = [];
        List<string> tail = [];

        for (int index = 0; index < arguments.Count; index++)
        {
            string argument = arguments[index];
            if (argument == "--")
            {
                if (!allowTail)
                {
                    throw new ConfigurationException("此命令不接受 -- 后的子命令。");
                }

                tail.AddRange(arguments.Skip(index + 1));
                break;
            }

            if (!argument.StartsWith("--", StringComparison.Ordinal))
            {
                positionals.Add(argument);
                continue;
            }

            int separator = argument.IndexOf('=');
            string name = separator >= 0 ? argument[..separator] : argument;
            if (switches.Contains(name))
            {
                if (separator >= 0)
                {
                    throw new ConfigurationException($"开关 {name} 不接受值。");
                }

                AddOption(options, name, null);
                continue;
            }

            if (!values.Contains(name))
            {
                throw new ConfigurationException($"未知选项：{name}");
            }

            string value;
            if (separator >= 0)
            {
                value = argument[(separator + 1)..];
            }
            else
            {
                if (++index >= arguments.Count)
                {
                    throw new ConfigurationException($"选项 {name} 缺少值。");
                }

                value = arguments[index];
            }

            if (string.IsNullOrWhiteSpace(value))
            {
                throw new ConfigurationException($"选项 {name} 的值不能为空。");
            }

            AddOption(options, name, value);
        }

        return new CliArguments
        {
            _options = options,
            Positionals = positionals,
            Tail = tail,
        };
    }

    private static void AddOption(Dictionary<string, string?> options, string name, string? value)
    {
        if (!options.TryAdd(name, value))
        {
            throw new ConfigurationException($"选项不能重复：{name}");
        }
    }
}
