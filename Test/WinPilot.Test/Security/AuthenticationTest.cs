using WinPilot.Core;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;
using WinPilot.Features.Security;

namespace WinPilot.Test;

public sealed class AuthenticationTest
{
    public static IEnumerable<object[]> InvalidAuthenticationRecords()
    {
        var valid = Convert.ToBase64String(new byte[32]);
        yield return ["null"];
        yield return ["{}"];
        foreach (var field in new[] { "Salt", "Hash" })
        {
            var other = field == "Salt" ? "Hash" : "Salt";
            yield return [JsonSerializer.Serialize(new Dictionary<string, string> { [other] = valid })];
            foreach (var value in new[] { null, "", " ", "not-base64", Convert.ToBase64String(new byte[31]), Convert.ToBase64String(new byte[33]) })
                yield return [JsonSerializer.Serialize(new Dictionary<string, string?> { [field] = value, [other] = valid })];
        }
    }

    [Theory]
    [MemberData(nameof(InvalidAuthenticationRecords))]
    public void InvalidAuthenticationRecordIsUnconfiguredAndReadReportsInvalidConfiguration(string content)
    {
        var root = Path.Combine(Path.GetTempPath(), "WinPilot.Auth.Test", Guid.NewGuid().ToString("N"));
        try
        {
            var paths = PortableDataPaths.Create(root);
            Directory.CreateDirectory(paths.DataDirectory);
            File.WriteAllText(paths.AuthenticationFile, content);
            var store = new AuthStore(paths);
            Assert.False(AuthStore.HasValidRecord(paths.AuthenticationFile));
            Assert.False(store.IsConfigured);
            var error = Assert.Throws<InvalidOperationException>(() => store.Verify("a secure passphrase"));
            Assert.Equal("认证配置文件无效", error.Message);
            Assert.IsType<InvalidDataException>(error.InnerException);
            Assert.Equal(content, File.ReadAllText(paths.AuthenticationFile));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ValidAuthenticationRecordAcceptsBase64WhitespaceWithoutChangingVerification(bool whitespace)
    {
        var root = Path.Combine(Path.GetTempPath(), "WinPilot.Auth.Test", Guid.NewGuid().ToString("N"));
        try
        {
            var paths = PortableDataPaths.Create(root);
            var store = new AuthStore(paths);
            store.SetInitialPassword("a secure passphrase");
            if (whitespace)
            {
                using var record = JsonDocument.Parse(File.ReadAllText(paths.AuthenticationFile));
                File.WriteAllText(paths.AuthenticationFile, JsonSerializer.Serialize(new
                {
                    Salt = " \n" + record.RootElement.GetProperty("Salt").GetString() + "\r\n ",
                    Hash = " \n" + record.RootElement.GetProperty("Hash").GetString() + "\r\n "
                }));
            }
            Assert.True(store.IsConfigured);
            Assert.True(store.Verify("a secure passphrase"));
            Assert.False(store.Verify("another secure passphrase"));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void AclFailurePreservesOriginalAuthenticationCleansTemporaryAndDoesNotWrapCancellation(bool existing, bool cancelled)
    {
        var root = Path.Combine(Path.GetTempPath(), "WinPilot.Auth.Test", Guid.NewGuid().ToString("N"));
        try
        {
            var paths = PortableDataPaths.Create(root);
            if (existing) new AuthStore(paths).SetInitialPassword("a secure passphrase");
            var original = existing ? File.ReadAllBytes(paths.AuthenticationFile) : null;
            Exception cause = cancelled ? new OperationCanceledException("ACL 测试取消") : new UnauthorizedAccessException("ACL 测试拒绝");
            var store = new AuthStore(paths, temporary =>
            {
                Assert.Equal(paths.AuthenticationFile + ".tmp", temporary);
                Assert.True(File.Exists(temporary));
                throw cause;
            });
            var error = Record.Exception(() =>
            {
                if (existing) store.ChangePassword("a secure passphrase", "another secure passphrase");
                else store.SetInitialPassword("another secure passphrase");
            });
            if (cancelled) Assert.Same(cause, error);
            else Assert.Same(cause, Assert.IsType<IOException>(error).InnerException);
            Assert.False(File.Exists(paths.AuthenticationFile + ".tmp"));
            if (existing)
            {
                Assert.Equal(original, File.ReadAllBytes(paths.AuthenticationFile));
                Assert.True(new AuthStore(paths).Verify("a secure passphrase"));
            }
            else Assert.False(File.Exists(paths.AuthenticationFile));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public void AuthStore_StoresPbkdf2RecordAndAllowsOnlyCorrectPassword()
    {
        var root = Path.Combine(Path.GetTempPath(), "WinPilot.Auth.Test", Guid.NewGuid().ToString("N"));
        try
        {
            var paths = PortableDataPaths.Create(root);
            var store = new AuthStore(paths);
            store.SetInitialPassword("a secure passphrase");

            using var document = JsonDocument.Parse(File.ReadAllText(paths.AuthenticationFile));
            Assert.Equal(32, Convert.FromBase64String(document.RootElement.GetProperty("Salt").GetString()!).Length);
            Assert.Equal(32, Convert.FromBase64String(document.RootElement.GetProperty("Hash").GetString()!).Length);
            Assert.Equal(2, document.RootElement.EnumerateObject().Count());
            Assert.DoesNotContain("a secure passphrase", File.ReadAllText(paths.AuthenticationFile));
            Assert.True(store.Verify("a secure passphrase"));
            Assert.False(store.Verify("wrong password"));
            Assert.Throws<InvalidOperationException>(() => store.SetInitialPassword("another secure passphrase"));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public void AuthStore_ProtectsAuthFileWithExpectedWindowsAcl()
    {
        var root = Path.Combine(Path.GetTempPath(), "WinPilot.Auth.Test", Guid.NewGuid().ToString("N"));
        try
        {
            var paths = PortableDataPaths.Create(root);
            new AuthStore(paths).SetInitialPassword("a secure passphrase");
            var rules = new FileInfo(paths.AuthenticationFile).GetAccessControl().GetAccessRules(true, true, typeof(SecurityIdentifier)).Cast<FileSystemAccessRule>().ToArray();
            Assert.Contains(rules, rule => rule.IdentityReference.Value == new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null).Value && rule.AccessControlType == AccessControlType.Allow);
            Assert.Contains(rules, rule => rule.IdentityReference.Value == new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null).Value && rule.AccessControlType == AccessControlType.Allow);
            Assert.DoesNotContain(rules, rule => rule.IsInherited);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public void FailedPasswordReplacementPreservesExistingAuthenticationAndRemovesTemporaryFile()
    {
        var root = Path.Combine(Path.GetTempPath(), "WinPilot.Auth.Test", Guid.NewGuid().ToString("N"));
        var paths = PortableDataPaths.Create(root);
        try
        {
            var store = new AuthStore(paths);
            store.SetInitialPassword("a secure passphrase");
            var original = File.ReadAllText(paths.AuthenticationFile);
            Assert.True(store.Verify("a secure passphrase", out var version));
            File.SetAttributes(paths.AuthenticationFile, FileAttributes.ReadOnly);
            var error = Record.Exception(() => store.ChangePassword("a secure passphrase", "another secure passphrase"));
            Assert.True(error is IOException or UnauthorizedAccessException, error?.ToString() ?? "文件替换意外成功");
            Assert.Equal(original, File.ReadAllText(paths.AuthenticationFile));
            Assert.True(store.Verify("a secure passphrase"));
            Assert.False(store.Verify("another secure passphrase"));
            Assert.False(File.Exists(paths.AuthenticationFile + ".tmp"));
            Assert.True(store.IsCurrentVersion(version));
            using var sessions = new SessionManager();
            var session = sessions.Create(store, version);
            Assert.NotNull(session);
            Assert.True(sessions.Validate(session));
        }
        finally
        {
            if (File.Exists(paths.AuthenticationFile)) File.SetAttributes(paths.AuthenticationFile, FileAttributes.Normal);
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    [Fact]
    public void PasswordChangeAndSessionRevocation_InvalidateExistingSessions()
    {
        var root = Path.Combine(Path.GetTempPath(), "WinPilot.Auth.Test", Guid.NewGuid().ToString("N"));
        try
        {
            var paths = PortableDataPaths.Create(root);
            var store = new AuthStore(paths);
            var sessions = new SessionManager();
            store.SetInitialPassword("a secure passphrase");
            var session = sessions.Create();
            store.ChangePassword("a secure passphrase", "another secure passphrase");
            sessions.RevokeAll();

            Assert.False(store.Verify("a secure passphrase"));
            Assert.True(store.Verify("another secure passphrase"));
            Assert.False(sessions.Validate(session));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public void AuthenticationVersionRejectsOldVerificationAndAcceptsCurrentPassword()
    {
        var root = Path.Combine(Path.GetTempPath(), "WinPilot.Auth.Test", Guid.NewGuid().ToString("N"));
        try
        {
            var store = new AuthStore(PortableDataPaths.Create(root));
            using var sessions = new SessionManager();
            store.SetInitialPassword("old-password");
            Assert.True(store.Verify("old-password", out var oldVersion));
            var previous = sessions.Create(store, oldVersion);
            Assert.NotNull(previous);
            store.ChangePassword("old-password", "new-password");
            sessions.RevokeAll();
            Assert.False(store.IsCurrentVersion(oldVersion));
            Assert.Null(sessions.Create(store, oldVersion));
            Assert.False(sessions.Validate(previous));
            Assert.True(store.Verify("new-password", out var currentVersion));
            Assert.NotEqual(oldVersion, currentVersion);
            var current = sessions.Create(store, currentVersion);
            Assert.NotNull(current);
            Assert.True(sessions.Validate(current));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public void SessionRevocation_CancelsAuthenticatedLongRunningRequests()
    {
        var sessions = new SessionManager();
        var session = sessions.Create();

        Assert.True(sessions.TryValidate(session, out var invalidated));
        Assert.False(invalidated.IsCancellationRequested);

        sessions.RevokeAll();

        Assert.True(invalidated.IsCancellationRequested);
        Assert.False(sessions.Validate(session));
    }

    [Fact]
    public void SessionCreationPrunesExpiredEntriesAndDisposalCancelsRemainingSessions()
    {
        using var sessions = new SessionManager();
        var expired = sessions.Create();
        Assert.True(sessions.TryValidate(expired, out var previousToken));
        var field = typeof(SessionManager).GetField("sessions", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        var states = (System.Collections.IDictionary)field.GetValue(sessions)!;
        var state = states[expired]!;
        state.GetType().GetField("expiresAt", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
            .SetValue(state, DateTimeOffset.UtcNow.AddMinutes(-1));
        var active = sessions.Create();
        Assert.False(states.Contains(expired));
        Assert.True(previousToken.IsCancellationRequested);
        Assert.True(sessions.TryValidate(active, out var activeToken));
        sessions.Dispose();
        sessions.Dispose();
        Assert.True(activeToken.IsCancellationRequested);
        var callbackRan = false;
        using var registration = activeToken.Register(() => callbackRan = true);
        Assert.True(callbackRan);
        Assert.Empty(states.Keys.Cast<object>());
        Assert.Throws<ObjectDisposedException>(() => sessions.Create());
    }

    [Fact]
    public void LoginRateLimiter_LocksOnlyTheFailingSource()
    {
        var limiter = new LoginRateLimiter();
        for (var index = 0; index < 5; index++) limiter.Failure("127.0.0.1");
        Assert.True(limiter.IsLocked("127.0.0.1"));
        Assert.False(limiter.IsLocked("192.168.1.10"));
    }

    [Fact]
    public void ConcurrentLoginFailuresReportOneLockTransitionAndSuccessResetsTheSource()
    {
        var limiter = new LoginRateLimiter();
        var transitions = 0;
        Parallel.For(0, 100, _ =>
        {
            if (limiter.Failure("192.168.1.10")) Interlocked.Increment(ref transitions);
        });
        Assert.Equal(1, transitions);
        Assert.True(limiter.IsLocked("192.168.1.10"));
        limiter.Success("192.168.1.10");
        Assert.False(limiter.IsLocked("192.168.1.10"));
        for (var index = 0; index < 4; index++) Assert.False(limiter.Failure("192.168.1.10"));
        Assert.True(limiter.Failure("192.168.1.10"));
    }

    [Fact]
    public void AuthStore_RejectsLegacyOrUnexpectedFields()
    {
        var root = Path.Combine(Path.GetTempPath(), "WinPilot.Auth.Test", Guid.NewGuid().ToString("N"));
        try
        {
            var paths = PortableDataPaths.Create(root);
            var store = new AuthStore(paths);
            store.SetInitialPassword("a secure passphrase");
            var legacy = File.ReadAllText(paths.AuthenticationFile).Replace("{", "{\"Algorithm\":\"PBKDF2-SHA512\",");
            File.WriteAllText(paths.AuthenticationFile, legacy);
            Assert.False(store.IsConfigured);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
}
