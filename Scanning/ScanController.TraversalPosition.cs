using System.Diagnostics;
using System.Drawing;

namespace ZZZScannerNext.Scanning;

public sealed partial class ScanController
{
    private sealed class TraversalPositionGuard(GameWindow window, ScanProfile profile, ScanLog log, bool enabled, CancellationToken token) : IDisposable
    {
        private ScrollbarThumbProbe _expected;
        private int _visibleTop;
        private bool _bound;
        private WarehouseMonitorSignature? _headerBaseline;

        public void BindVerifiedPosition(int visibleTop)
        {
            if (!enabled) return;
            _expected = CaptureScrollbarThumbProbe(window, profile, includeThumbEnds: true);
            if (!_expected.Found) throw NavigationFailure("无法绑定扫描行的滚动条位置。",
                new Dictionary<string, object?> { ["phase"] = "traversal_position", ["reason"] = "scrollbar_position_missing", ["visibleTopLogicalRow"] = visibleTop, ["positionFound"] = false });
            if (_headerBaseline is null)
            {
                using var header = window.Capture(window.ToScreenRectangle(profile.Rectangle("inventoryCount")));
                _headerBaseline = WarehousePreflightEvaluator.CreateMonitorSignature(header,
                    [new Rectangle(Point.Empty, header.Size)]);
            }
            _visibleTop = visibleTop;
            _bound = true;
            window.ConfigureTraversalPositionGuard(Verify);
            log.WriteEvent("TRAVERSAL_POSITION_BOUND", $"visibleTopLogicalRow={_visibleTop}, thumb={_expected.StartY}-{_expected.EndY}");
        }

        public void Verify()
        {
            if (!enabled || !_bound) return;
            token.ThrowIfCancellationRequested();
            var actual = CaptureScrollbarThumbProbe(window, profile, includeThumbEnds: true);
            token.ThrowIfCancellationRequested();
            var failure = TraversalPositionPolicy.Failure(_expected, actual);
            if (failure is null) return;
            if (!actual.Found)
            {
                log.WriteEvent("TRAVERSAL_POSITION_WAIT", $"visibleTopLogicalRow={_visibleTop}, expectedThumb={_expected.StartY}-{_expected.EndY}, timeoutMs={TraversalPositionPolicy.MissingPositionTimeoutMilliseconds}, reason=scrollbar_position_missing");
                var confirmation = TraversalPositionPolicy.ConfirmAfterMissing(_expected,
                    CaptureRecoveryPosition, token);
                actual = confirmation.Actual;
                failure = confirmation.Failure;
                if (failure is null)
                {
                    log.WriteEvent("TRAVERSAL_POSITION_RECOVERED", $"visibleTopLogicalRow={_visibleTop}, expectedThumb={_expected.StartY}-{_expected.EndY}, actualThumb={actual.StartY}-{actual.EndY}, samples={confirmation.Samples}");
                    return;
                }
            }
            log.WriteEvent("TRAVERSAL_POSITION_LOST", $"visibleTopLogicalRow={_visibleTop}, expectedThumb={_expected.StartY}-{_expected.EndY}, actualThumb={actual.StartY}-{actual.EndY}, found={actual.Found}, reason={failure}");
            throw NavigationFailure(failure == "scrollbar_position_missing"
                ? "暂时无法确认列表位置，等待画面恢复后仍未确认，已停止扫描。"
                : "扫描一行期间列表位置发生变化，已停止，避免漏扫或重复导入。",
                new Dictionary<string, object?>
                {
                    ["phase"] = "traversal_position", ["reason"] = failure,
                    ["visibleTopLogicalRow"] = _visibleTop, ["positionFound"] = actual.Found,
                    ["expectedThumbStart"] = _expected.StartY - window.ClientScreenRect.Top,
                    ["expectedThumbEnd"] = _expected.EndY - window.ClientScreenRect.Top,
                    ["actualThumbStart"] = actual.Found ? actual.StartY - window.ClientScreenRect.Top : null,
                    ["actualThumbEnd"] = actual.Found ? actual.EndY - window.ClientScreenRect.Top : null
                });
        }

        private ScrollbarThumbProbe CaptureRecoveryPosition()
        {
            if (!window.IsCaptureContextCurrent()) return default;
            var watch = Stopwatch.StartNew();
            var bounds = window.ClientScreenRect;
            using var frame = window.CaptureFrame(bounds);
            using var bitmap = frame.ToBitmap();
            var health = WarehousePreflightEvaluator.EvaluateCaptureHealth(bitmap);
            var header = window.ToScreenRectangle(profile.Rectangle("inventoryCount"));
            header.Offset(-bounds.Left, -bounds.Top);
            var headerScore = health.Passed && _headerBaseline is not null
                ? WarehousePreflightEvaluator.CompareMonitorSignature(_headerBaseline,
                    WarehousePreflightEvaluator.CreateMonitorSignature(bitmap, [header]))
                : 0;
            var minimumScore = Math.Clamp((profile.VisualProbes?.WarehousePreflight ?? new WarehousePreflightPolicy()).MonitorMinimumScore, 1, 100);
            var contextCurrent = window.IsCaptureContextCurrent();
            var thumb = ExtractScrollbarThumbProbe(frame, bounds, ScrollbarProbeBounds(window, profile, true),
                profile.Color("scrollBar"), Math.Max(0, profile.ColorTolerance));
            log.WriteEvent("TRAVERSAL_POSITION_CAPTURE", $"elapsedMs={watch.Elapsed.TotalMilliseconds:F1}, backend={frame.BackendName}, contextCurrent={contextCurrent}, healthPassed={health.Passed}, headerScore={headerScore}, requiredScore={minimumScore}, found={thumb.Found}, thumb={thumb.StartY}-{thumb.EndY}");
            // A detected displacement is never hidden by a failed context check.
            if (thumb.Found && TraversalPositionPolicy.Failure(_expected, thumb) is not null) return thumb;
            return contextCurrent && health.Passed && headerScore >= minimumScore ? thumb : default;
        }

        public void SuspendForAdvance()
        {
            Verify();
            _bound = false;
            window.ConfigureTraversalPositionGuard(null);
        }

        public void Dispose() => window.ConfigureTraversalPositionGuard(null);
    }
}
