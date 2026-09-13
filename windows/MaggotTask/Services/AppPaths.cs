namespace MaggotTask.Services;

/// <summary>应用的磁盘位置。</summary>
public static class AppPaths
{
    /// <summary>%APPDATA%\MaggotTask</summary>
    public static string DataDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "MaggotTask");

    /// <summary>%APPDATA%\MaggotTask\state.json</summary>
    public static string StateFile { get; } = Path.Combine(DataDirectory, "state.json");

    public static void EnsureDataDirectory()
    {
        try
        {
            Directory.CreateDirectory(DataDirectory);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 目录建不出来就退化为「本次不落盘」，不影响运行。
            System.Diagnostics.Debug.WriteLine($"[MaggotTask] 无法创建数据目录: {ex.Message}");
        }
    }
}
