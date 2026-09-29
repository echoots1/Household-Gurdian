using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;

namespace Guardian.Service.Infrastructure;

/// <summary>%ProgramData%\Guardian: SYSTEM and Administrators full control, nothing for Users. The child cannot read the database, the key, or the SMTP secret.</summary>
public static class DataFolderAcl
{
    [SupportedOSPlatform("windows")]
    public static void Apply(string dir, ILogger log)
    {
        try
        {
            var di = new DirectoryInfo(dir);
            var acl = new DirectorySecurity();
            acl.SetAccessRuleProtection(true, false);
            var flags = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;
            acl.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), FileSystemRights.FullControl, flags, PropagationFlags.None, AccessControlType.Allow));
            acl.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null), FileSystemRights.FullControl, flags, PropagationFlags.None, AccessControlType.Allow));
            acl.SetOwner(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null));
            di.SetAccessControl(acl);
        }
        catch (Exception ex) { log.LogWarning(ex, "Could not set the data folder ACL"); }
    }
}
