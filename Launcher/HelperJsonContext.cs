using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace ZZZScannerHelper;

internal static partial class Program
{
    private static JsonTypeInfo ResolveJsonTypeInfo(Type type)
    {
        return HelperJsonContext.Default.GetTypeInfo(type)
            ?? throw new InvalidOperationException($"JSON type is not registered: {type.FullName}");
    }

    [JsonSerializable(typeof(HelperEnvelope))]
    [JsonSerializable(typeof(TokenRequest))]
    [JsonSerializable(typeof(TokenResponse))]
    [JsonSerializable(typeof(ScannerManifest))]
    [JsonSerializable(typeof(HelperInfoResponse))]
    [JsonSerializable(typeof(HelperHello))]
    [JsonSerializable(typeof(ScannerState))]
    [JsonSerializable(typeof(LauncherProgress))]
    [JsonSerializable(typeof(ScanStatusMessage))]
    [JsonSerializable(typeof(HelperErrorMessage))]
    [JsonSerializable(typeof(HelperErrorAction))]
    [JsonSerializable(typeof(HelperDiagnosticsResponse))]
    [JsonSerializable(typeof(HelperStorageSnapshot))]
    [JsonSerializable(typeof(HelperStorageCleanupResult))]
    [JsonSerializable(typeof(StorageInfoResponse))]
    [JsonSerializable(typeof(StorageCleanupResponse))]
    [JsonSerializable(typeof(HelperUpdateProgress))]
    [JsonSerializable(typeof(HelperUpdateResponse))]
    [JsonSerializable(typeof(HelperUpdateTransactionInfo))]
    [JsonSerializable(typeof(HelperUpdateCommitResponse))]
    [JsonSerializable(typeof(PongMessage))]
    [JsonSourceGenerationOptions(
        PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = false)]
    private sealed partial class HelperJsonContext : JsonSerializerContext
    {
    }
}
