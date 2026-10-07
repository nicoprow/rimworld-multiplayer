using System.Globalization;

namespace NetworkConditioner;

public record ConditionerOptions(
    int ListenPort,
    string TargetHost,
    int TargetPort,
    LinkProfile Upstream,
    LinkProfile Downstream,
    int Seed,
    TimeSpan SessionTimeout,
    TimeSpan StatsInterval)
{
    private static readonly string[] SharedLinkOptionNames =
        ["delay", "jitter", "loss", "burst-interval", "burst-length"];

    private static readonly string[] ValueOptionNames = BuildValueOptionNames();

    public static string UsageText =>
        string.Join(Environment.NewLine,
            "Usage: NetworkConditioner --listen <port> --target <host:port> [options]",
            "",
            "Options:",
            "  --delay <ms>              One-way base delay (default 0)",
            "  --jitter <ms>             Uniform jitter around the delay (default 0)",
            "  --loss <percent>          Random loss, 0-100 (default 0)",
            "  --burst-interval <s>      Mean time between loss bursts (default 0, off)",
            "  --burst-length <ms>       Mean loss burst duration (default 0)",
            "  --reorder                 Allow packets to overtake each other",
            "  --up-<option>             Override delay, jitter, loss, burst-interval or burst-length",
            "                            for client to server only (e.g. --up-delay 100)",
            "  --down-<option>           Same, for server to client only",
            "  --seed <int>              Random seed (default random, always printed)",
            "  --session-timeout <s>     Idle time before a client session is closed (default 30)",
            "  --stats-interval <s>      Seconds between statistics lines (default 5)",
            "  --help                    Show this text");

    public static ConditionerOptions Parse(string[] args)
    {
        var values = new Dictionary<string, string>();
        var allowReorder = false;

        for (var i = 0; i < args.Length; i++)
        {
            var argument = args[i];
            if (!argument.StartsWith("--"))
                throw new ArgumentException($"Unexpected argument '{argument}'");

            var name = argument[2..];
            if (name == "reorder")
            {
                allowReorder = true;
                continue;
            }

            if (!ValueOptionNames.Contains(name))
                throw new ArgumentException($"Unknown option '{argument}'");

            if (i + 1 >= args.Length)
                throw new ArgumentException($"Option '{argument}' needs a value");

            values[name] = args[++i];
        }

        if (!values.TryGetValue("listen", out var listenText))
            throw new ArgumentException("Missing required option --listen");
        if (!values.TryGetValue("target", out var targetText))
            throw new ArgumentException("Missing required option --target");

        var listenPort = ParsePort(listenText, "--listen");
        var (targetHost, targetPort) = ParseTarget(targetText);

        var seed = values.TryGetValue("seed", out var seedText)
            ? ParseInt(seedText, "--seed")
            : Random.Shared.Next();

        var sessionTimeout = ParsePositiveSeconds(values, "session-timeout", 30);
        var statsInterval = ParsePositiveSeconds(values, "stats-interval", 5);

        return new ConditionerOptions(
            listenPort,
            targetHost,
            targetPort,
            ParseLinkProfile(values, "up-", allowReorder),
            ParseLinkProfile(values, "down-", allowReorder),
            seed,
            sessionTimeout,
            statsInterval);
    }

    private static string[] BuildValueOptionNames()
    {
        var singleValueNames = new[] { "listen", "target", "seed", "session-timeout", "stats-interval" };
        var upNames = SharedLinkOptionNames.Select(name => "up-" + name);
        var downNames = SharedLinkOptionNames.Select(name => "down-" + name);
        return singleValueNames.Concat(SharedLinkOptionNames).Concat(upNames).Concat(downNames).ToArray();
    }

    private static LinkProfile ParseLinkProfile(Dictionary<string, string> values, string directionPrefix, bool allowReorder)
    {
        var profile = new LinkProfile(
            ResolveLinkValue(values, directionPrefix, "delay"),
            ResolveLinkValue(values, directionPrefix, "jitter"),
            ResolveLinkValue(values, directionPrefix, "loss"),
            ResolveLinkValue(values, directionPrefix, "burst-interval"),
            ResolveLinkValue(values, directionPrefix, "burst-length"),
            allowReorder);

        if (profile.LossPercent > 100)
            throw new ArgumentException($"Loss must be at most 100 ({directionPrefix}direction)");
        if (profile.BurstIntervalSeconds > 0 && profile.BurstLengthMs <= 0)
            throw new ArgumentException($"Burst length must be greater than 0 when a burst interval is set ({directionPrefix}direction)");

        return profile;
    }

    private static double ResolveLinkValue(Dictionary<string, string> values, string directionPrefix, string name)
    {
        var key = values.ContainsKey(directionPrefix + name) ? directionPrefix + name : name;
        return values.TryGetValue(key, out var text) ? ParseNonNegative(text, "--" + key) : 0;
    }

    private static TimeSpan ParsePositiveSeconds(Dictionary<string, string> values, string name, double defaultSeconds)
    {
        if (!values.TryGetValue(name, out var text))
            return TimeSpan.FromSeconds(defaultSeconds);

        var seconds = ParseNonNegative(text, "--" + name);
        if (seconds <= 0)
            throw new ArgumentException($"--{name} must be greater than 0");
        return TimeSpan.FromSeconds(seconds);
    }

    private static double ParseNonNegative(string text, string optionName)
    {
        var parsed = double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var number);
        if (!parsed || !double.IsFinite(number))
            throw new ArgumentException($"{optionName} needs a number, got '{text}'");
        if (number < 0)
            throw new ArgumentException($"{optionName} must not be negative");
        return number;
    }

    private static int ParseInt(string text, string optionName)
    {
        if (!int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number))
            throw new ArgumentException($"{optionName} needs an integer, got '{text}'");
        return number;
    }

    private static int ParsePort(string text, string optionName)
    {
        var port = ParseInt(text, optionName);
        if (port is < 1 or > 65535)
            throw new ArgumentException($"{optionName} must be a port between 1 and 65535");
        return port;
    }

    private static (string Host, int Port) ParseTarget(string text)
    {
        var separatorIndex = text.LastIndexOf(':');
        if (separatorIndex <= 0)
            throw new ArgumentException($"--target must look like host:port, got '{text}'");

        var host = text[..separatorIndex];
        var port = ParsePort(text[(separatorIndex + 1)..], "--target");
        return (host, port);
    }
}
