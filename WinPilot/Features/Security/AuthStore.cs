using System.Security.Cryptography;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;
using System.Text.Json.Serialization;
using WinPilot.Core;

namespace WinPilot.Features.Security;

public sealed class AuthStore(PortableDataPaths paths)
{
    private const int Iterations = 600_000;
    private static readonly JsonSerializerOptions SerializationOptions = new()
    {
        WriteIndented = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };
    private readonly object gate = new();

    public bool IsConfigured => HasValidRecord(paths.AuthenticationFile);

    public static bool HasValidRecord(string path)
    {
        try
        {
            if (!File.Exists(path)) return false;
            var record = JsonSerializer.Deserialize<AuthRecord>(File.ReadAllText(path), SerializationOptions);
            return record is not null
                   && Convert.FromBase64String(record.Salt).Length == 32
                   && Convert.FromBase64String(record.Hash).Length == 32;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or FormatException)
        {
            return false;
        }
    }

    public void SetInitialPassword(string password)
    {
        lock (gate)
        {
            if (IsConfigured) throw new InvalidOperationException("管理员密码已设置，不能再次初始化");
            Save(password);
        }
    }

    public bool Verify(string password)
    {
        if (!IsValidPasswordInput(password)) return false;
        AuthRecord record;
        lock (gate) { record = Read(); }
        var salt = Convert.FromBase64String(record.Salt);
        var expected = Convert.FromBase64String(record.Hash);
        var actual = Rfc2898DeriveBytes.Pbkdf2(password, salt, Iterations, HashAlgorithmName.SHA512, expected.Length);
        return CryptographicOperations.FixedTimeEquals(actual, expected);
    }

    public void ChangePassword(string currentPassword, string newPassword)
    {
        lock (gate)
        {
            if (!Verify(currentPassword)) throw new InvalidOperationException("当前密码不正确");
            Save(newPassword);
        }
    }

    public static void ValidateNewPassword(string password, string confirmation)
    {
        if (!IsValidPasswordInput(password)) throw new InvalidOperationException("密码至少需要 6 位，且不能以空白字符开头或结尾");
        if (!string.Equals(password, confirmation, StringComparison.Ordinal)) throw new InvalidOperationException("两次输入的密码不一致");
    }

    private static bool IsValidPasswordInput(string? password) => password is { Length: >= 6 } && password == password.Trim() && !string.IsNullOrWhiteSpace(password);

    private AuthRecord Read()
    {
        if (!File.Exists(paths.AuthenticationFile)) throw new InvalidOperationException("管理员密码尚未设置");
        try
        {
            var record = JsonSerializer.Deserialize<AuthRecord>(File.ReadAllText(paths.AuthenticationFile), SerializationOptions) ?? throw new InvalidDataException();
            if (!IsValidRecord(record)) throw new InvalidDataException();
            return record;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or FormatException or InvalidDataException)
        {
            throw new InvalidOperationException("认证配置文件无效", exception);
        }
    }

    private void Save(string password)
    {
        AuthStore.ValidateNewPassword(password, password);
        var salt = RandomNumberGenerator.GetBytes(32);
        var hash = Rfc2898DeriveBytes.Pbkdf2(password, salt, Iterations, HashAlgorithmName.SHA512, 32);
        Directory.CreateDirectory(paths.DataDirectory);
        var temporary = paths.AuthenticationFile + ".tmp";
        var record = new AuthRecord(Convert.ToBase64String(salt), Convert.ToBase64String(hash));
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(record, SerializationOptions));
            try { ApplyAcl(temporary); }
            catch (Exception exception)
            {
                throw new InvalidOperationException("无法为认证配置文件设置安全访问权限", exception);
            }
            File.Move(temporary, paths.AuthenticationFile, true);
        }
        finally
        {
            try { File.Delete(temporary); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    private static void ApplyAcl(string path)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("当前运行环境不支持 Windows ACL");
        using var identity = WindowsIdentity.GetCurrent();
        var currentUser = identity.User ?? throw new InvalidOperationException("无法读取当前 Windows 用户");
        var security = new FileSecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.AddAccessRule(new FileSystemAccessRule(currentUser, FileSystemRights.FullControl, AccessControlType.Allow));
        security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null), FileSystemRights.FullControl, AccessControlType.Allow));
        security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), FileSystemRights.FullControl, AccessControlType.Allow));
        new FileInfo(path).SetAccessControl(security);
    }

    private static bool IsValidRecord(AuthRecord record) => Convert.FromBase64String(record.Salt).Length == 32
        && Convert.FromBase64String(record.Hash).Length == 32;

    private sealed record AuthRecord(string Salt, string Hash);
}
