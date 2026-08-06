using System.Runtime.InteropServices;

namespace LabLock.Client.Helpers;

public static class ProcessGuard
{
    private const uint DACL_SECURITY_INFORMATION = 0x00000004;
    private const uint OWNER_SECURITY_INFORMATION = 0x00000001;
    private const uint GROUP_SECURITY_INFORMATION = 0x00000002;

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentProcess();

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode)]
    private static extern bool ConvertStringSecurityDescriptorToSecurityDescriptor(
        string stringSecurityDescriptor,
        uint stringSDRevision,
        out IntPtr securityDescriptor,
        out ulong securityDescriptorSize);

    [DllImport("advapi32.dll")]
    private static extern bool SetKernelObjectSecurity(
        IntPtr handle,
        uint securityInformation,
        IntPtr securityDescriptor);

    [DllImport("kernel32.dll")]
    private static extern bool LocalFree(IntPtr hMem);

    /// <summary>
    /// Locks down the current process so only SYSTEM can terminate it.
    /// Everyone else loses PROCESS_TERMINATE (taskkill /f fails); only the
    /// Service Control Manager (SYSTEM) can stop it. Call once at startup.
    /// </summary>
    public static void Harden()
    {
        const string sddl =
            "D:" +
            "(A;;GA;;;SY)" +                 // SYSTEM: full control (can kill/stop)
            "(A;;GRGXSY;;;WD)" +             // Everyone: read, execute, synchronize (no terminate)
            "(A;;RC;;;BU)";                  // Users: read-control only

        if (!ConvertStringSecurityDescriptorToSecurityDescriptor(
            sddl, 1, out var pSd, out _))
            return;

        try
        {
            SetKernelObjectSecurity(
                GetCurrentProcess(),
                DACL_SECURITY_INFORMATION,
                pSd);
        }
        finally
        {
            LocalFree(pSd);
        }
    }
}
