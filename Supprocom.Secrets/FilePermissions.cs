using System.Security.AccessControl;
using System.Security.Principal;

namespace Supprocom.Secrets;

#pragma warning disable CA1416
internal sealed class FilePermissionSnapshot
{
    private readonly FileSystemAccessRule[]? _windowsRules;
    private readonly bool _windowsRulesProtected;
    private readonly UnixFileMode? _unixMode;

    private FilePermissionSnapshot(FileSystemAccessRule[] rules, bool rulesProtected)
    {
        _windowsRules = rules;
        _windowsRulesProtected = rulesProtected;
    }

    private FilePermissionSnapshot(UnixFileMode unixMode)
    {
        _unixMode = unixMode;
    }

    public static FilePermissionSnapshot? Capture(string path)
    {
        if (!File.Exists(path))
            return null;

        if (OperatingSystem.IsWindows())
        {
            FileSecurity security = new FileInfo(path).GetAccessControl(AccessControlSections.Access);
            var rules = security
                .GetAccessRules(includeExplicit: true, includeInherited: false, typeof(SecurityIdentifier))
                .Cast<FileSystemAccessRule>()
                .ToArray();
            return new FilePermissionSnapshot(rules, security.AreAccessRulesProtected);
        }

        return new FilePermissionSnapshot(File.GetUnixFileMode(path));
    }

    public void Apply(string path)
    {
        if (_windowsRules is not null)
        {
            var security = new FileSecurity();
            security.SetAccessRuleProtection(_windowsRulesProtected, preserveInheritance: !_windowsRulesProtected);
            foreach (FileSystemAccessRule rule in _windowsRules)
                security.AddAccessRule(rule);
            new FileInfo(path).SetAccessControl(security);
            return;
        }

        if (_unixMode.HasValue)
            File.SetUnixFileMode(path, _unixMode.Value);
    }
}
#pragma warning restore CA1416
