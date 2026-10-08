using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace SideBySide.Core.Settings;

public interface ISettingsStore
{
    AppSettings Load();

    void Save(AppSettings settings);
}

/// <summary>
/// JSON 파일 저장소. 손상된 파일은 .bak으로 옮기고 기본값으로 시작한다.
/// 저장은 임시 파일 → 교체 방식이라 쓰는 도중 종료되어도 기존 설정이 깨지지 않는다.
/// </summary>
public sealed class JsonSettingsStore(string path) : ISettingsStore
{
    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
        NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals,
    };

    public string FilePath { get; } = path;

    public static string DefaultPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SideBySide", "settings.json");

    public AppSettings Load()
    {
        if (!File.Exists(FilePath)) return new AppSettings();

        try
        {
            var node = JsonNode.Parse(File.ReadAllText(FilePath)) as JsonObject ?? throw new JsonException("root is not an object");
            Migrate(node);
            return node.Deserialize<AppSettings>(JsonOptions) ?? new AppSettings();
        }
        catch (Exception ex) when (ex is JsonException or IOException or NotSupportedException or InvalidOperationException)
        {
            TryBackupCorrupted();
            return new AppSettings();
        }
    }

    public void Save(AppSettings settings)
    {
        settings.Version = AppSettings.CurrentVersion;
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        var temp = FilePath + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(settings, JsonOptions));
        File.Move(temp, FilePath, overwrite: true);
    }

    /// <summary>
    /// 버전별 단계적 마이그레이션. 새 버전 추가 예:
    /// <code>if (version &lt; 2) { node["NewName"] = node["OldName"]?.DeepClone(); node.Remove("OldName"); }</code>
    /// </summary>
    internal static void Migrate(JsonObject node)
    {
        var version = node["Version"]?.GetValue<int>() ?? 0;

        if (version < 1)
        {
            // v0(버전 필드 없음) → v1: 형식 동일, 버전만 기록
        }

        node["Version"] = AppSettings.CurrentVersion;
    }

    private void TryBackupCorrupted()
    {
        try { File.Copy(FilePath, FilePath + ".bak", overwrite: true); }
        catch (IOException) { /* 백업 실패는 무시 */ }
    }
}
