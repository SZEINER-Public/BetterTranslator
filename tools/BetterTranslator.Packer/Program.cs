using System.Diagnostics;
using System.Text;

namespace BetterTranslator.Packer;

internal static class Program
{
    private const string SectionName = ".btpay";

    private static int Main(string[] args)
    {
        try
        {
            return Run(args);
        }
        catch (Exception failure)
        {
            Console.Error.WriteLine($"packer: {failure.Message}");
            return 1;
        }
    }

    private static int Run(string[] args)
    {
        if (args.Length == 0 || args[0] is "-h" or "--help")
        {
            WriteUsage();
            return args.Length == 0 ? 1 : 0;
        }

        if (args[0] != "pack")
        {
            Console.Error.WriteLine($"packer: unknown command '{args[0]}'");
            WriteUsage();
            return 1;
        }

        string? payloadRoot = null;
        string? bootstrapPath = null;
        string? outputPath = null;
        string? reportPath = null;
        uint algorithm = BtPayFormat.AlgorithmLzms;
        uint blockSize = BtPayFormat.DefaultBlockSize;
        int parallelism = Environment.ProcessorCount;
        var excludes = new List<string>();

        for (int index = 1; index < args.Length; index++)
        {
            switch (args[index])
            {
                case "--payload":
                    payloadRoot = Next(args, ref index);
                    break;
                case "--bootstrap":
                    bootstrapPath = Next(args, ref index);
                    break;
                case "--output":
                    outputPath = Next(args, ref index);
                    break;
                case "--report":
                    reportPath = Next(args, ref index);
                    break;
                case "--algorithm":
                    algorithm = WindowsCompressor.ParseAlgorithm(Next(args, ref index));
                    break;
                case "--block-size":
                    blockSize = uint.Parse(Next(args, ref index));
                    break;
                case "--parallelism":
                    parallelism = int.Parse(Next(args, ref index));
                    break;
                case "--exclude":
                    excludes.AddRange(Next(args, ref index)
                        .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
                    break;
                default:
                    throw new ArgumentException($"unknown option '{args[index]}'");
            }
        }

        if (payloadRoot is null || bootstrapPath is null || outputPath is null)
        {
            throw new ArgumentException("--payload, --bootstrap and --output are all required");
        }

        if (excludes.Count == 0)
        {
            excludes.Add("*.pdb");
        }

        var timer = Stopwatch.StartNew();
        IReadOnlyList<PayloadFile> files = PayloadInventory.Collect(payloadRoot, excludes);
        long inventoryMilliseconds = timer.ElapsedMilliseconds;

        timer.Restart();
        ContainerResult container = ContainerBuilder.Build(files, algorithm, blockSize, parallelism);
        long containerMilliseconds = timer.ElapsedMilliseconds;

        timer.Restart();
        byte[] bootstrap = File.ReadAllBytes(bootstrapPath);
        byte[] packed = PortableExecutableEditor.AppendPayloadSection(bootstrap, SectionName, container.Bytes);

        string? outputDirectory = Path.GetDirectoryName(Path.GetFullPath(outputPath));
        if (!string.IsNullOrEmpty(outputDirectory))
        {
            Directory.CreateDirectory(outputDirectory);
        }

        File.WriteAllBytes(outputPath, packed);
        long appendMilliseconds = timer.ElapsedMilliseconds;

        var report = new StringBuilder();
        report.AppendLine($"payload root      {Path.GetFullPath(payloadRoot)}");
        report.AppendLine($"payload files     {container.Statistics.EntryCount}");
        report.AppendLine($"payload bytes     {container.Statistics.UncompressedBytes}");
        report.AppendLine($"excluded          {string.Join(", ", excludes)}");
        report.AppendLine($"algorithm         {WindowsCompressor.DescribeAlgorithm(container.Statistics.Algorithm)}");
        report.AppendLine($"block size        {blockSize}");
        report.AppendLine($"blocks            {container.Statistics.BlockCount}");
        report.AppendLine($"container bytes   {container.Statistics.ContainerBytes}");
        report.AppendLine($"compression ratio {Ratio(container.Statistics)}");
        report.AppendLine($"bootstrap bytes   {bootstrap.Length}");
        report.AppendLine($"output bytes      {packed.Length}");
        report.AppendLine($"output            {Path.GetFullPath(outputPath)}");
        report.AppendLine($"payload sha256    {container.Statistics.HashHex}");
        report.AppendLine($"cache directory   {container.Statistics.HashHex[..(int)BtPayFormat.HashPrefixHexChars]}");
        report.AppendLine($"inventory ms      {inventoryMilliseconds}");
        report.AppendLine($"container ms      {containerMilliseconds}");
        report.AppendLine($"append ms         {appendMilliseconds}");

        Console.Out.Write(report.ToString());

        if (reportPath is not null)
        {
            string? reportDirectory = Path.GetDirectoryName(Path.GetFullPath(reportPath));
            if (!string.IsNullOrEmpty(reportDirectory))
            {
                Directory.CreateDirectory(reportDirectory);
            }

            File.WriteAllText(reportPath, report.ToString() + PayloadInventory.Describe(files));
        }

        return 0;
    }

    private static string Ratio(ContainerStatistics statistics) =>
        statistics.UncompressedBytes == 0
            ? "n/a"
            : ((double)statistics.ContainerBytes / statistics.UncompressedBytes).ToString("0.000");

    private static string Next(string[] args, ref int index)
    {
        if (index + 1 >= args.Length)
        {
            throw new ArgumentException($"option '{args[index]}' needs a value");
        }

        return args[++index];
    }

    private static void WriteUsage()
    {
        Console.Error.WriteLine("""
            usage: BetterTranslator.Packer pack --payload <dir> --bootstrap <exe> --output <exe>
                                                [--algorithm store|xpress-huff|lzms]
                                                [--block-size <bytes>]
                                                [--parallelism <count>]
                                                [--exclude <pattern>]
                                                [--report <path>]
            """);
    }
}
