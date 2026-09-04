namespace BetterTranslator.Core.Verification.Checks.Structure;

public sealed class JsonParityCheck : StructureCheck
{
    public const string MalformedAttribute = "malformed";

    public override string CheckId => Checks.CheckId.Structure.JsonParity;

    protected override void Find(CheckContext context, List<CheckFinding> findings)
    {
        var sourceNodes = context.Source.Nodes.Where(IsJson).ToList();

        if (sourceNodes.Count == 0)
        {
            return;
        }

        var targetRoot = context.Target.Nodes.FirstOrDefault(n => n.Kind == DocumentNodeKind.Document);

        if (targetRoot is not null && string.Equals(targetRoot.Attribute(MalformedAttribute), "true", StringComparison.Ordinal))
        {
            findings.Add(Finding(
                context.Target.Whole,
                context.Source.Whole,
                CheckGranularity.Document,
                CheckSeverity.Defect,
                100,
                CheckCause.Restore,
                "target is not well-formed JSON"));
            return;
        }

        var targetNodes = context.Target.Nodes.Where(IsJson).ToDictionary(n => n.Path, n => n, StringComparer.Ordinal);
        var sourceByPath = sourceNodes.ToDictionary(n => n.Path, n => n, StringComparer.Ordinal);

        foreach (var node in sourceNodes)
        {
            if (!targetNodes.TryGetValue(node.Path, out var twin))
            {
                var parent = context.Target.Nodes.FirstOrDefault(n => string.Equals(n.Path, node.ParentPath, StringComparison.Ordinal));
                var where = parent?.Range ?? context.Target.Whole;

                findings.Add(Finding(
                    where,
                    node.Range,
                    CheckGranularity.Block,
                    CheckSeverity.Defect,
                    100,
                    CauseAt(context, where, node.Range),
                    Describe(node) + " missing from target"));
                continue;
            }

            if (twin.Kind != node.Kind)
            {
                findings.Add(Finding(
                    twin.Range,
                    node.Range,
                    CheckGranularity.Block,
                    CheckSeverity.Defect,
                    100,
                    CauseAt(context, twin.Range, node.Range),
                    $"{Describe(node)} is {node.Kind} in source, {twin.Kind} in target"));
                continue;
            }

            if (twin.Depth != node.Depth)
            {
                findings.Add(Finding(
                    twin.Range,
                    node.Range,
                    CheckGranularity.Block,
                    CheckSeverity.Defect,
                    100,
                    CauseAt(context, twin.Range, node.Range),
                    $"{Describe(node)} nesting depth {node.Depth} in source, {twin.Depth} in target"));
            }

            Compare(context, node, twin, "length", "array length", findings);
            Compare(context, node, twin, "type", "value type", findings);
        }

        foreach (var node in context.Target.Nodes.Where(IsJson))
        {
            if (!sourceByPath.ContainsKey(node.Path))
            {
                findings.Add(Finding(
                    node.Range,
                    null,
                    CheckGranularity.Block,
                    CheckSeverity.Defect,
                    100,
                    CauseAt(context, node.Range, null),
                    Describe(node) + " added in target"));
            }
        }
    }

    private void Compare(CheckContext context, DocumentNode node, DocumentNode twin, string attribute, string label, List<CheckFinding> findings)
    {
        var expected = node.Attribute(attribute);
        var actual = twin.Attribute(attribute);

        if (string.Equals(expected, actual, StringComparison.Ordinal))
        {
            return;
        }

        findings.Add(Finding(
            twin.Range,
            node.Range,
            CheckGranularity.Block,
            CheckSeverity.Defect,
            100,
            CauseAt(context, twin.Range, node.Range),
            $"{Describe(node)} {label} '{expected}' in source, '{actual}' in target"));
    }

    private static string Describe(DocumentNode node) => node.Kind switch
    {
        DocumentNodeKind.JsonProperty => $"key '{node.Attribute("key")}' at {node.ParentPath}",
        DocumentNodeKind.JsonArray => $"array at {node.Path}",
        DocumentNodeKind.JsonObject => $"object at {node.Path}",
        _ => $"value at {node.Path}",
    };

    private static bool IsJson(DocumentNode node) =>
        node.Kind is DocumentNodeKind.JsonObject or DocumentNodeKind.JsonArray or DocumentNodeKind.JsonProperty or DocumentNodeKind.JsonValue;
}
