using System.Text.Json;
using System.Text.Json.Nodes;

namespace Soda.Scanner.Core;

public class PointerInfo
{
    public int SchemaVersion { get; set; } = 1;
    public string Version { get; set; } = string.Empty;
    public string Path { get; set; } = string.Empty;
    public string VerifiedAt { get; set; } = string.Empty;
    public bool AccountWriteEnabled { get; set; } = false;
    public bool ImportAccess { get; set; } = false;
}

public static class PointerStore
{
    public static PointerInfo? ReadValidated(string pointerFilePath, string installRoot)
    {
        FileSystemSafety.AssertSafePath(installRoot, pointerFilePath);
        if (!File.Exists(pointerFilePath) || new FileInfo(pointerFilePath).Length > 65536) return null;
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(pointerFilePath));
            var json = document.RootElement;
            if (json.GetProperty("schemaVersion").GetInt32() != 1 ||
                json.GetProperty("accountWriteEnabled").GetBoolean() || json.GetProperty("importAccess").GetBoolean()) return null;
            var version = json.GetProperty("version").GetString();
            var path = json.GetProperty("path").GetString();
            if (string.IsNullOrEmpty(version) || !System.Text.RegularExpressions.Regex.IsMatch(version, "^soda-scanner-[a-zA-Z0-9._-]+$") ||
                string.IsNullOrEmpty(path)) return null;
            var expected = FileSystemSafety.SafePath(installRoot, "versions\\" + version);
            if (!Path.GetFullPath(path).Equals(expected, StringComparison.OrdinalIgnoreCase)) return null;
            return ReadPointer(pointerFilePath);
        }
        catch (JsonException) { return null; }
        catch (KeyNotFoundException) { return null; }
        catch (InvalidOperationException) { return null; }
        catch (ArgumentException) { return null; }
    }

    public static PointerInfo? ReadPointer(string pointerFilePath)
    {
        if (!File.Exists(pointerFilePath))
        {
            return null;
        }

        try
        {
            var json = File.ReadAllText(pointerFilePath);
            return JsonSerializer.Deserialize<PointerInfo>(json, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            });
        }
        catch
        {
            return null;
        }
    }

    public static void WritePointerAtomic(string pointerFilePath, PointerInfo pointer)
    {
        var destFullPath = Path.GetFullPath(pointerFilePath);
        var dir = Path.GetDirectoryName(destFullPath) ?? throw new InvalidOperationException("Invalid pointer directory");
        Directory.CreateDirectory(dir);

        var fileName = Path.GetFileName(destFullPath);
        var nonce = Guid.NewGuid().ToString("N");
        var tempFile = Path.Combine(dir, $".{fileName}.{nonce}.tmp");
        var bakFile = Path.Combine(dir, $".{fileName}.{nonce}.bak");

        var payload = new JsonObject
        {
            ["schemaVersion"] = pointer.SchemaVersion,
            ["version"] = pointer.Version,
            ["path"] = pointer.Path,
            ["verifiedAt"] = pointer.VerifiedAt,
            ["accountWriteEnabled"] = pointer.AccountWriteEnabled,
            ["importAccess"] = pointer.ImportAccess
        };

        var json = payload.ToJsonString(new JsonSerializerOptions { WriteIndented = false });

        try
        {
            File.WriteAllText(tempFile, json);
            if (File.Exists(destFullPath))
            {
                File.Replace(tempFile, destFullPath, bakFile);
            }
            else
            {
                File.Move(tempFile, destFullPath);
            }
        }
        finally
        {
            if (File.Exists(tempFile)) File.Delete(tempFile);
            if (File.Exists(bakFile)) File.Delete(bakFile);
        }
    }
}
