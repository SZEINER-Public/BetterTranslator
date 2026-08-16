using System.IO.Enumeration;
using System.Text;

namespace BetterTranslator.Packer;

internal static class PayloadInventory
{
    private static readonly uint AttributeMask = BtPayFormat.AttributeMask;

    public static IReadOnlyList<PayloadFile> Collect(string root, IReadOnlyList<string> excludePatterns)
    {
        string full = Path.GetFullPath(root);
        if (!Directory.Exists(full))
        {
            throw new DirectoryNotFoundException($"the payload directory {full} does not exist");
        }

        var collected = new List<PayloadFile>();

        foreach (string path in Directory.EnumerateFiles(full, "*", SearchOption.AllDirectories))
        {
            string relative = Path.GetRelativePath(full, path).Replace('\\', '/');

            if (excludePatterns.Any(pattern => Matches(relative, pattern)))
            {
                continue;
            }

            var info = new FileInfo(path);
            collected.Add(new PayloadFile(relative, path, info.Length, (uint)info.Attributes & AttributeMask));
        }

        if (collected.Count == 0)
        {
            throw new InvalidOperationException($"the payload directory {full} holds no files after exclusions");
        }

        return collected;
    }

    public static string Describe(IReadOnlyList<PayloadFile> files)
    {
        var report = new StringBuilder();
        long bytes = files.Sum(file => file.Length);

        report.AppendLine($"files {files.Count}");
        report.AppendLine($"bytes {bytes}");

        foreach (PayloadFile file in files.OrderByDescending(file => file.Length).Take(10))
        {
            report.AppendLine($"  {file.Length,12}  {file.PortablePath}");
        }

        return report.ToString();
    }

    private static bool Matches(string relativePath, string pattern)
    {
        string name = pattern.Contains('/') ? relativePath : Path.GetFileName(relativePath);
        return FileSystemName.MatchesSimpleExpression(pattern, name, ignoreCase: true);
    }
}
