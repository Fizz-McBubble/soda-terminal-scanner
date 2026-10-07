using System.Collections.Concurrent;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Globalization;
using System.Runtime.ExceptionServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using ZZZScannerNext.Cleaning;
using ZZZScannerNext.Core;
using ZZZScannerNext.Ocr;
using CvRect = System.Drawing.Rectangle;
using OcrBatchInput = ZZZScannerNext.Ocr.PaddleOcrRecognizer.OcrBatchInput;

namespace ZZZScannerNext.Scanning;

public sealed partial class ScanController
{
    private sealed class PanelCapture : IDisposable
    {
        private bool _imageTaken;

        public PanelCapture(
            Bitmap image,
            int visibleRoiCount,
            double waitMilliseconds,
            bool usedFallback,
            ImageSignature[] probeSignatures,
            int frameCount,
            double? changeMilliseconds,
            double? selectionChangeMilliseconds,
            double? fullRoiMilliseconds,
            double? stableMilliseconds,
            double? panelTextStableMilliseconds,
            string panelStableSource,
            string panelStabilityReason,
            double captureMilliseconds,
            double signatureMilliseconds,
            double visibleRoiMilliseconds,
            double frameLoopMilliseconds,
            double frameToBitmapMilliseconds,
            int bitmapCreatedCount,
            int minimumAcceptMilliseconds,
            int requiredStableFrames,
            int adaptiveSampleCount,
            string adaptiveReason,
            string acceptReason,
            bool quickAccept,
            string quickRejectReason,
            PanelAcceptMode panelAcceptMode,
            PostScrollPanelAcceptMode postScrollPanelAcceptMode,
            PanelFloorMode panelFloorMode,
            int panelMinAcceptFloorMs,
            int sameRowPanelFloorMs,
            int postScrollPanelFloorMs,
            string panelFloorReason,
            double floorWaitLimitedMilliseconds,
            double panelAcceptElapsedVsFloorMilliseconds,
            int roiCompleteFrames,
            int selectedStableFrames,
            int targetSelectionStableFrames,
            TargetVerificationKind targetVerificationKind,
            string acceptGateReason)
        {
            Image = image;
            VisibleRoiCount = visibleRoiCount;
            WaitMilliseconds = waitMilliseconds;
            UsedFallback = usedFallback;
            ProbeSignatures = probeSignatures;
            FrameCount = frameCount;
            ChangeMilliseconds = changeMilliseconds;
            SelectionChangeMilliseconds = selectionChangeMilliseconds;
            FullRoiMilliseconds = fullRoiMilliseconds;
            StableMilliseconds = stableMilliseconds;
            PanelTextStableMilliseconds = panelTextStableMilliseconds;
            PanelStableSource = panelStableSource;
            PanelStabilityReason = panelStabilityReason;
            CaptureMilliseconds = captureMilliseconds;
            SignatureMilliseconds = signatureMilliseconds;
            VisibleRoiMilliseconds = visibleRoiMilliseconds;
            FrameLoopMilliseconds = frameLoopMilliseconds;
            FrameToBitmapMilliseconds = frameToBitmapMilliseconds;
            BitmapCreatedCount = bitmapCreatedCount;
            MinimumAcceptMilliseconds = minimumAcceptMilliseconds;
            RequiredStableFrames = requiredStableFrames;
            AdaptiveSampleCount = adaptiveSampleCount;
            AdaptiveReason = adaptiveReason;
            AcceptReason = acceptReason;
            QuickAccept = quickAccept;
            QuickRejectReason = quickRejectReason;
            PanelAcceptMode = panelAcceptMode;
            PostScrollPanelAcceptMode = postScrollPanelAcceptMode;
            PanelFloorMode = panelFloorMode;
            PanelMinAcceptFloorMs = panelMinAcceptFloorMs;
            SameRowPanelFloorMs = sameRowPanelFloorMs;
            PostScrollPanelFloorMs = postScrollPanelFloorMs;
            PanelFloorReason = panelFloorReason;
            FloorWaitLimitedMilliseconds = floorWaitLimitedMilliseconds;
            PanelAcceptElapsedVsFloorMilliseconds = panelAcceptElapsedVsFloorMilliseconds;
            RoiCompleteFrames = roiCompleteFrames;
            SelectedStableFrames = selectedStableFrames;
            TargetSelectionStableFrames = targetSelectionStableFrames;
            TargetVerificationKind = targetVerificationKind;
            AcceptGateReason = acceptGateReason;
        }

        public Bitmap Image { get; }
        public int VisibleRoiCount { get; }
        public double WaitMilliseconds { get; }
        public bool UsedFallback { get; }
        public ImageSignature[] ProbeSignatures { get; }
        public int FrameCount { get; }
        public double? ChangeMilliseconds { get; }
        public double? SelectionChangeMilliseconds { get; }
        public double? FullRoiMilliseconds { get; }
        public double? StableMilliseconds { get; }
        public double? PanelTextStableMilliseconds { get; }
        public string PanelStableSource { get; }
        public string PanelStabilityReason { get; }
        public double CaptureMilliseconds { get; }
        public double SignatureMilliseconds { get; }
        public double VisibleRoiMilliseconds { get; }
        public double FrameLoopMilliseconds { get; }
        public double FrameToBitmapMilliseconds { get; }
        public int BitmapCreatedCount { get; }
        public int MinimumAcceptMilliseconds { get; }
        public int RequiredStableFrames { get; }
        public int AdaptiveSampleCount { get; }
        public string AdaptiveReason { get; }
        public string AcceptReason { get; }
        public bool QuickAccept { get; }
        public string QuickRejectReason { get; }
        public PanelAcceptMode PanelAcceptMode { get; }
        public PostScrollPanelAcceptMode PostScrollPanelAcceptMode { get; }
        public PanelFloorMode PanelFloorMode { get; }
        public int PanelMinAcceptFloorMs { get; }
        public int SameRowPanelFloorMs { get; }
        public int PostScrollPanelFloorMs { get; }
        public string PanelFloorReason { get; }
        public double FloorWaitLimitedMilliseconds { get; }
        public double PanelAcceptElapsedVsFloorMilliseconds { get; }
        public int RoiCompleteFrames { get; }
        public int SelectedStableFrames { get; }
        public int TargetSelectionStableFrames { get; }
        public TargetVerificationKind TargetVerificationKind { get; }
        public string AcceptGateReason { get; }

        public Bitmap TakeImage()
        {
            _imageTaken = true;
            return Image;
        }

        public void Dispose()
        {
            if (!_imageTaken)
            {
                Image.Dispose();
            }
        }
    }

}
