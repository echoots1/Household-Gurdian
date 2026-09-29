using Guardian.Contracts;

namespace Guardian.Service.Infrastructure;

/// <summary>Where everything lives. %ProgramData%\Guardian on Windows; GUARDIAN_DATA or ./guardian-data elsewhere (dev/test).</summary>
public sealed class Paths
{
    public string DataDir { get; }
    public string DbPath => Path.Combine(DataDir, Names.DbFileName);
    public string BackupDir => Path.Combine(DataDir, "backup");
    public string ListsDir => Path.Combine(DataDir, "lists");
    public string ExtDir => Path.Combine(DataDir, "ext");
    public string CertPath => Path.Combine(DataDir, "guardian.pfx");
    public string LogDir => Path.Combine(DataDir, "logs");
    public string InstallDir { get; }

    public Paths(string? dataDir = null)
    {
        DataDir = dataDir
            ?? Environment.GetEnvironmentVariable("GUARDIAN_DATA")
            ?? (OperatingSystem.IsWindows()
                ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), Names.DataFolderName)
                : Path.Combine(Directory.GetCurrentDirectory(), "guardian-data"));
        Directory.CreateDirectory(DataDir);
        Directory.CreateDirectory(BackupDir);
        Directory.CreateDirectory(ListsDir);
        Directory.CreateDirectory(LogDir);
        InstallDir = AppContext.BaseDirectory;
    }

    public string TrayExe => Path.Combine(InstallDir, "GuardianTray.exe");
}
