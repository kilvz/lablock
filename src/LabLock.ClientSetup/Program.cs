namespace LabLock.ClientSetup;

static class Program
{
    [STAThread]
    static int Main(string[] args)
    {
        var silent = args.Contains("--silent");
        var uninstall = args.Contains("--uninstall") || args.Contains("-u");
        var extractTo = args.Where(a => a.StartsWith("--extract-to=", StringComparison.OrdinalIgnoreCase))
                            .Select(a => a.Substring("--extract-to=".Length).Trim('"'))
                            .FirstOrDefault();
        var overrideDir = args.Where(a => a.StartsWith("--to=", StringComparison.OrdinalIgnoreCase))
                              .Select(a => a.Substring("--to=".Length).Trim('"'))
                              .FirstOrDefault();

        var options = new InstallerCore.ClientOptions(
            ServerUrl: Flag(args, "--server="),
            ApiKey: Flag(args, "--apikey="),
            ClientId: Flag(args, "--clientid="));

        var installDir = overrideDir ?? InstallerCore.DefaultInstallDir;

        if (extractTo != null)
        {
            try
            {
                Directory.CreateDirectory(extractTo);
                foreach (var name in new[] { InstallerCore.BinaryName, "appsettings.json" })
                {
                    var dest = Path.Combine(extractTo, name);
                    using var s = System.Reflection.Assembly.GetExecutingAssembly()
                        .GetManifestResourceStream(name)
                        ?? throw new InvalidOperationException($"missing resource {name}");
                    using var f = File.Create(dest);
                    s.CopyTo(f);
                }
                return 0;
            }
            catch
            {
                return 1;
            }
        }

        if (silent || uninstall)
        {
            try
            {
                if (uninstall)
                    InstallerCore.Uninstall(installDir);
                else
                    InstallerCore.Install(installDir, options: options);
                return 0;
            }
            catch
            {
                return 1;
            }
        }

        ApplicationConfiguration.Initialize();
        Application.Run(new InstallerForm(installDir, options));
        return 0;
    }

    private static string? Flag(string[] args, string prefix) =>
        args.Where(a => a.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            .Select(a => a.Substring(prefix.Length).Trim('"'))
            .Where(v => v.Length > 0)
            .FirstOrDefault();
}
