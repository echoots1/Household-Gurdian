namespace Guardian.Contracts;

/// <summary>The three fixed activity categories. Rules map apps and domains onto these.</summary>
public static class Categories
{
    public const string School = "School";
    public const string Gaming = "Gaming";
    public const string Other = "Other";

    public static readonly string[] All = { School, Gaming, Other };

    public static bool IsValid(string? c) => c is School or Gaming or Other;
}

public static class Ports
{
    /// <summary>Loopback-only HTTP: extension reports, tray reports, extension update source.</summary>
    public const int Local = 47130;
    /// <summary>LAN HTTPS: parent dashboard, and the child's /me view when reached over loopback.</summary>
    public const int Web = 47131;
}

public static class Names
{
    public const string Product = "Guardian";
    public const string ServiceName = "GuardianSvc";
    public const string PipeName = "GuardianTray";
    public const string DataFolderName = "Guardian";
    public const string DbFileName = "guardian.db";
}
