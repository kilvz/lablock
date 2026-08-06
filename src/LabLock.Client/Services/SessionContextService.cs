using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Principal;
using LabLock.Client.Helpers;
using Microsoft.Win32;

namespace LabLock.Client.Services;

public class SessionContextService
{
    private const string IdentityStoreLogonCache = @"SOFTWARE\Microsoft\IdentityStore\LogonCache";

    private readonly int _agentSessionId;
    private readonly string _runAsUser;
    private readonly string _osVersion;

    public SessionContextService()
    {
        _agentSessionId = Process.GetCurrentProcess().SessionId;
        _runAsUser = GetCurrentUserName();
        _osVersion = SystemInfoService.GetFriendlyOsName();
    }

    public int AgentSessionId => _agentSessionId;
    public string RunAsUser => _runAsUser;
    public string OsVersion => _osVersion;

    public int GetInteractiveSessionId()
    {
        try
        {
            return (int)NativeMethods.WTSGetActiveConsoleSessionId();
        }
        catch
        {
            return -1;
        }
    }

    public string GetInteractiveUser()
    {
        var sessionId = GetInteractiveSessionId();
        if (sessionId <= 0)
            return "";

        var name = TryGetUserFromToken((uint)sessionId, out var sid);
        if (!string.IsNullOrEmpty(name))
        {
            var email = GetIdentityStoreEmail(sid);
            return string.IsNullOrEmpty(email) ? name : $"{name} ({email})";
        }

        var user = QuerySessionInfo((uint)sessionId, NativeMethods.WTSUserName);
        var domain = QuerySessionInfo((uint)sessionId, NativeMethods.WTSDomainName);
        if (string.IsNullOrEmpty(user))
            return "";
        return string.IsNullOrEmpty(domain) ? user : $"{domain}\\{user}";
    }

    private static string TryGetUserFromToken(uint sessionId, out string sid)
    {
        sid = "";
        try
        {
            if (!NativeMethods.WTSQueryUserToken(sessionId, out var token) || token == IntPtr.Zero)
                return "";

            try
            {
                using var identity = new WindowsIdentity(token);
                sid = identity.User?.Value ?? "";
                return identity.Name;
            }
            finally
            {
                NativeMethods.CloseHandle(token);
            }
        }
        catch
        {
            return "";
        }
    }

    private static string GetIdentityStoreEmail(string sid)
    {
        try
        {
            using var logonCache = Registry.LocalMachine.OpenSubKey(IdentityStoreLogonCache);
            if (logonCache == null)
                return "";

            string fallback = "";
            foreach (var provider in logonCache.GetSubKeyNames())
            {
                using var providerKey = logonCache.OpenSubKey(provider);
                using var name2Sid = providerKey?.OpenSubKey("Name2Sid");
                if (name2Sid == null)
                    continue;

                foreach (var entryKeyName in name2Sid.GetSubKeyNames())
                {
                    using var entry = name2Sid.OpenSubKey(entryKeyName);
                    var entrySid = entry?.GetValue("Name2Sid")?.ToString();
                    var identityName = entry?.GetValue("IdentityName")?.ToString();

                    if (string.IsNullOrEmpty(identityName) || !identityName.Contains('@'))
                        continue;

                    if (!string.IsNullOrEmpty(sid) && string.Equals(entrySid, sid, StringComparison.OrdinalIgnoreCase))
                        return identityName;

                    if (string.IsNullOrEmpty(fallback))
                        fallback = identityName;
                }
            }

            return fallback;
        }
        catch
        {
            return "";
        }
    }

    private static string QuerySessionInfo(uint sessionId, int infoClass)
    {
        if (!NativeMethods.WTSQuerySessionInformation(IntPtr.Zero, sessionId, infoClass, out var buffer, out _))
            return "";
        try
        {
            if (buffer == IntPtr.Zero)
                return "";
            return Marshal.PtrToStringAnsi(buffer) ?? "";
        }
        finally
        {
            NativeMethods.WTSFreeMemory(buffer);
        }
    }

    private static string GetCurrentUserName()
    {
        try
        {
            return WindowsIdentity.GetCurrent().Name;
        }
        catch
        {
            return $"{Environment.UserDomainName}\\{Environment.UserName}";
        }
    }
}
