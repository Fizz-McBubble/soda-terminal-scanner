using System.Text.Json;
using ZZZScannerNext.Core;

namespace ZZZScannerNext.Scanning;

// Local traversal proof. This file is never part of browser staging or telemetry.
internal sealed class ScanTraversalEvidence
{
    internal sealed record Cell(int? LogicalRow, int Column, string Rarity, bool Selected);
    private readonly List<Cell> cells = [];
    internal IReadOnlyList<Cell> Cells => cells;

    internal void Visit(int? logicalRow, int column, string rarity, bool selected)
        => cells.Add(new Cell(logicalRow, column, rarity, selected));

    internal int Validate(int warehouseTotal, int columns, IReadOnlySet<string> selectedRarities,
        int visited, int queued, int completed, int failed, int exported, bool partial)
    {
        if (partial || warehouseTotal < 0 || columns <= 0 || visited != warehouseTotal || cells.Count != visited)
            throw new InvalidDataException("r4_traversal_incomplete");
        for (var index = 0; index < cells.Count; index++)
        {
            var cell = cells[index];
            if (cell.LogicalRow != index / columns + 1 || cell.Column != index % columns + 1)
                throw new InvalidDataException("r4_traversal_sequence_mismatch");
            if (cell.Rarity is not ("S" or "A" or "B") || cell.Selected != selectedRarities.Contains(cell.Rarity))
                throw new InvalidDataException("r4_traversal_filter_unverified");
        }
        var selected = cells.Count(cell => cell.Selected);
        if (failed != 0 || selected != queued || selected != completed || selected != exported)
            throw new InvalidDataException("r4_selected_total_mismatch");
        return selected;
    }

    internal static void EnsureImportable(int selected)
    {
        if (selected == 0)
            throw new ScannerFailureException("scan_no_importable_s_discs", "没有可导入的 S 级驱动盘",
                "本次完整遍历未发现选中的 S 级驱动盘。", "可以检查仓库品质筛选后重新扫描。");
    }

    internal async Task WriteAsync(string outputDirectory, int? warehouseTotal, int columns,
        IReadOnlySet<string> selectedRarities, int visited, int queued, int completed, int failed,
        int exported, bool partial, string? validationError)
    {
        var proof = new
        {
            schemaVersion = 1, warehouseTotal, columns,
            selectedRarities = selectedRarities.OrderBy(value => value).ToArray(),
            visited, queued, completed, failed, exported, partial,
            verifiedComplete = validationError is null && !partial,
            validationError,
            skippedByRarity = cells.Where(cell => !cell.Selected).GroupBy(cell => cell.Rarity)
                .ToDictionary(group => group.Key, group => group.Count()),
            cells
        };
        await File.WriteAllTextAsync(Path.Combine(outputDirectory, "scan-traversal.json"),
            JsonSerializer.Serialize(proof, JsonDefaults.Write));
    }
}
