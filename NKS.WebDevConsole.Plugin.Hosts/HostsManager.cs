namespace NKS.WebDevConsole.Plugin.Hosts;

/// <summary>
/// Manages entries within a delimited block in the system hosts file.
/// </summary>
public class HostsManager
{
    private const string BeginMarker = "# BEGIN NKS WebDev Console";
    private const string EndMarker = "# END NKS WebDev Console";
    private readonly string _hostsPath;

    public HostsManager(string? hostsPath = null)
    {
        _hostsPath = hostsPath ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.System),
            "drivers", "etc", "hosts");
    }

    public string HostsPath => _hostsPath;

    /// <summary>
    /// Parses the hosts file and returns entries within the managed block.
    /// </summary>
    public List<(string Ip, string Domain)> GetManagedEntries()
    {
        if (!File.Exists(_hostsPath))
            return [];

        var content = File.ReadAllText(_hostsPath);
        var (_, managedBlock, _) = ParseHostsFile(content);
        return ParseManagedBlock(managedBlock);
    }

    /// <summary>
    /// Generates the managed block content for the given domains.
    /// </summary>
    public string GenerateHostsBlock(IEnumerable<string> domains, string ip = "127.0.0.1")
    {
        var lines = new List<string> { BeginMarker };

        foreach (var domain in domains)
        {
            lines.Add($"{ip}\t{domain}");
        }

        lines.Add(EndMarker);
        return string.Join(Environment.NewLine, lines);
    }

    /// <summary>
    /// Splits the hosts file content into three parts: before markers, managed block, and after markers.
    /// </summary>
    public (string Before, string ManagedBlock, string After) ParseHostsFile(string content)
    {
        var beginIdx = content.IndexOf(BeginMarker, StringComparison.Ordinal);
        var endIdx = content.IndexOf(EndMarker, StringComparison.Ordinal);

        if (beginIdx < 0 || endIdx < 0 || endIdx < beginIdx)
        {
            return (content, string.Empty, string.Empty);
        }

        var endOfEndMarker = endIdx + EndMarker.Length;

        // Skip trailing newline after end marker if present
        if (endOfEndMarker < content.Length && content[endOfEndMarker] == '\r')
            endOfEndMarker++;
        if (endOfEndMarker < content.Length && content[endOfEndMarker] == '\n')
            endOfEndMarker++;

        var before = content[..beginIdx];
        var managed = content[beginIdx..endIdx].TrimEnd();
        var after = content[endOfEndMarker..];

        return (before, managed, after);
    }

    /// <summary>
    /// Replaces (or appends) the managed block in the hosts file content.
    /// </summary>
    public string BuildUpdatedContent(string currentContent, IEnumerable<string> domains, string ip = "127.0.0.1")
    {
        var domainList = domains.ToList();
        var (before, _, after) = ParseHostsFile(currentContent);

        if (domainList.Count == 0)
        {
            // Remove managed block entirely
            return (before.TrimEnd() + Environment.NewLine + after.TrimStart()).Trim()
                   + Environment.NewLine;
        }

        var block = GenerateHostsBlock(domainList, ip);

        // Ensure separation
        var beforePart = before.TrimEnd();
        if (beforePart.Length > 0)
            beforePart += Environment.NewLine + Environment.NewLine;

        var afterPart = after.TrimStart();
        if (afterPart.Length > 0)
            afterPart = Environment.NewLine + Environment.NewLine + afterPart.TrimEnd()
                        + Environment.NewLine;
        else
            afterPart = Environment.NewLine;

        return beforePart + block + afterPart;
    }

    private static List<(string Ip, string Domain)> ParseManagedBlock(string block)
    {
        if (string.IsNullOrWhiteSpace(block))
            return [];

        var entries = new List<(string Ip, string Domain)>();
        var lines = block.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);

        foreach (var line in lines)
        {
            var trimmed = line.Trim();
            if (trimmed.StartsWith('#') || trimmed.Length == 0)
                continue;

            var parts = trimmed.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length >= 2)
            {
                entries.Add((parts[0], parts[1]));
            }
        }

        return entries;
    }
}
