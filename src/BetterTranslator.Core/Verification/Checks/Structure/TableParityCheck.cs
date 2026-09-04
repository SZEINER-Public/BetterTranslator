namespace BetterTranslator.Core.Verification.Checks.Structure;

public sealed class TableParityCheck : StructureCheck
{
    public override string CheckId => Checks.CheckId.Structure.TableParity;

    protected override void Find(CheckContext context, List<CheckFinding> findings)
    {
        var source = context.Source.OfKind(DocumentNodeKind.Table).ToList();
        var target = context.Target.OfKind(DocumentNodeKind.Table).ToList();

        if (source.Count == 0 && target.Count == 0)
        {
            return;
        }

        if (source.Count != target.Count)
        {
            findings.Add(Finding(
                context.Target.Whole,
                context.Source.Whole,
                CheckGranularity.Document,
                CheckSeverity.Defect,
                100,
                CauseAt(context, context.Target.Whole, context.Source.Whole),
                $"table count {source.Count} in source, {target.Count} in target"));
        }

        var shared = Math.Min(source.Count, target.Count);

        for (var i = 0; i < shared; i++)
        {
            Compare(context, source[i], target[i], "rows", "table rows", findings);
            Compare(context, source[i], target[i], "columns", "table columns", findings);
            Compare(context, source[i], target[i], "delimiter", "table delimiter row", findings);

            var sourceRows = context.Source.ChildrenOf(source[i]).Where(n => n.Kind == DocumentNodeKind.TableRow).ToList();
            var targetRows = context.Target.ChildrenOf(target[i]).Where(n => n.Kind == DocumentNodeKind.TableRow).ToList();

            for (var r = 0; r < Math.Min(sourceRows.Count, targetRows.Count); r++)
            {
                Compare(context, sourceRows[r], targetRows[r], "cells", $"table row {r + 1} cells", findings);
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
            $"{label} '{expected}' in source, '{actual}' in target"));
    }
}
