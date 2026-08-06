namespace LabLock.Installer;

static class Program
{
    [STAThread]
    static int Main(string[] args)
    {
        var silent = args.Contains("--silent");
        var overrideDir = args.Where(a => a.StartsWith("--to=", StringComparison.OrdinalIgnoreCase))
                              .Select(a => a.Substring("--to=".Length).Trim('"'))
                              .FirstOrDefault();

        if (silent)
        {
            try
            {
                InstallerCore.Install(overrideDir ?? InstallerCore.DefaultInstallDir);
                return 0;
            }
            catch
            {
                return 1;
            }
        }

        ApplicationConfiguration.Initialize();
        Application.Run(new InstallerForm(overrideDir));
        return 0;
    }
}
