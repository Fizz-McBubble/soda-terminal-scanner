namespace ZZZScannerNext.Scanning;

// Preserve the final same-frame proof inputs without images, item hashes or
// absolute screen coordinates. Animation stability alone cannot explain why
// the original click failed the independent position proof.
internal sealed record NativeEdgePositionDiagnostics(
    RowAdvanceEvidence Evidence,
    bool BeforePositionFound,
    bool PositionFound,
    int BeforeThumbHeight,
    int AfterThumbHeight,
    int? ScrollbarDelta,
    double PixelsPerRow,
    bool SawMovement)
{
    public void AddTo(IDictionary<string, object?> details)
    {
        details["positionReason"] = Evidence.Reason ?? "unknown";
        details["positionDecision"] = Evidence.Decision.ToString();
        details["positionStrong"] = Evidence.Strong;
        details["beforePositionFound"] = BeforePositionFound;
        details["positionFound"] = PositionFound;
        details["sawMovement"] = SawMovement;
        details["scrollbarDirection"] = ScrollbarDelta switch
        {
            < 0 => "up", > 0 => "down", 0 => "stationary", _ => "unavailable"
        };
        if (BeforePositionFound) AddNumber("beforeThumbHeight", BeforeThumbHeight, 20000);
        if (PositionFound) AddNumber("afterThumbHeight", AfterThumbHeight, 20000);
        if (ScrollbarDelta is int delta) AddNumber("scrollbarDeltaPixels", Math.Abs((long)delta), 20000);
        if (double.IsFinite(PixelsPerRow) && PixelsPerRow >= 0 && PixelsPerRow <= 20000)
            AddNumber("scrollbarPixelsPerRowMilli", (long)Math.Round(PixelsPerRow * 1000), 20000000);
        // int.MaxValue means that this structural comparison was unavailable.
        // It must not be serialized as an observed score or replaced by zero.
        AddNumber("structureNoMoveScore", Evidence.NoMoveScore, 100000);
        AddNumber("structureOneRowScore", Evidence.OneRowScore, 100000);
        AddNumber("structureTwoRowScore", Evidence.TwoRowScore, 100000);
        AddNumber("positionMovementDistance", Evidence.MovedDistance, 100000);

        void AddNumber(string name, long value, long maximum)
        {
            if (value >= 0 && value <= maximum) details[name] = value;
        }
    }
}

internal static class NativeEdgeFailureDiagnostics
{
    public static Dictionary<string, object?> Create(
        NativeEdgeClickSettleResult edge, int visibleTopLogicalRow, int scannedRows, int totalRows)
    {
        var details = new Dictionary<string, object?>
        {
            ["phase"] = "edge_click", ["reason"] = edge.Reason,
            ["visibleTopLogicalRow"] = visibleTopLogicalRow,
            ["scannedRows"] = scannedRows, ["totalRows"] = totalRows,
            ["stableFrames"] = edge.StableFrames,
            ["requiredStableFrames"] = NativeEdgeClickSettleTracker.RequiredStableFrames,
            ["samples"] = edge.Samples, ["elapsedMs"] = (long)Math.Round(edge.ElapsedMilliseconds),
            ["timeoutMs"] = edge.TimeoutMilliseconds,
            ["movementDistance"] = edge.MovementDistance
        };
        edge.PositionDiagnostics?.AddTo(details);
        return details;
    }
}
