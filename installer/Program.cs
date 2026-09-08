using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using WixSharp;
using WixSharp.CommonTasks;
using IOFile = System.IO.File;

internal static class Program
{
    private static int Main()
    {
        var repositoryRoot = FindRepositoryRoot();
        var publishDirectory = Path.Combine(repositoryRoot, "artifacts", "publish", "DiskMonitor");
        var outputDirectory = Path.Combine(repositoryRoot, "artifacts", "msi");
        var executable = Path.Combine(publishDirectory, "DiskMonitor.exe");

        if (!IOFile.Exists(executable))
        {
            throw new InvalidOperationException($"The published application was not found at '{executable}'. Run build.ps1 first.");
        }

        Directory.CreateDirectory(outputDirectory);

        var installDirectory = new Dir(
            @"%ProgramFiles%\Disk Monitor",
            new Files(Path.Combine(publishDirectory, "*.*")));

        var project = new Project(
            "Disk Monitor",
            installDirectory,
            new LaunchApplicationFromExitDialog(
                CreateStableFileId(executable),
                "Launch Disk Monitor"),
            new CustomActionRef(
                "Wix4CloseApplications_X64",
                When.After,
                Step.RemoveExistingProducts,
                Condition.Always),
            new CloseApplication("DiskMonitor.exe", true, false)
            {
                Description = "Disk Monitor must close before it can be upgraded or removed.",
                TerminateProcess = 0,
                Timeout = 5
            })
        {
            GUID = new Guid("1F1BAA9A-0B3A-486A-BD9B-1BBE3AA61BE8"),
            UpgradeCode = new Guid("5DE77042-1B99-4B3A-97BF-5E1050E79C5E"),
            Version = typeof(Program).Assembly.GetName().Version ?? throw new InvalidOperationException("The installer assembly has no version."),
            Platform = Platform.x64,
            Scope = InstallScope.perMachine,
            CustomIdAlgorithm = CreateStableEntityId,
            OutDir = outputDirectory,
            OutFileName = "DiskMonitor",
            LicenceFile = Path.Combine(repositoryRoot, "installer", "License.rtf"),
            RegValues = new[]
            {
                new RegValue(
                    RegistryHive.LocalMachine,
                    @"Software\Microsoft\Windows\CurrentVersion\Run",
                    "DiskMonitor",
                    "\"[INSTALLDIR]DiskMonitor.exe\"")
                {
                    Win64 = true
                }
            },
            UI = WUI.WixUI_InstallDir,
            MajorUpgrade = new MajorUpgrade
            {
                Schedule = UpgradeSchedule.afterInstallInitialize,
                DowngradeErrorMessage = "A newer version of Disk Monitor is already installed."
            }
        };
        project.ControlPanelInfo.Manufacturer = "Disk Monitor contributors";

        project.AddDir(new Dir(
            @"%ProgramMenu%\Disk Monitor",
            new ExeFileShortcut("Disk Monitor", "[INSTALLDIR]DiskMonitor.exe", "")));

        Compiler.BuildMsi(project);
        return 0;
    }

    private static string CreateStableEntityId(WixEntity entity)
    {
        if (!(entity is WixSharp.File file))
        {
            return null!;
        }

        return CreateStableFileId(file.Name);
    }

    private static string CreateStableFileId(string fileName)
    {
        fileName = Path.GetFileName(fileName).ToLowerInvariant();
        using (var sha256 = SHA256.Create())
        {
            var hash = sha256.ComputeHash(Encoding.UTF8.GetBytes(fileName));
            return "File_" + BitConverter.ToString(hash, 0, 16).Replace("-", string.Empty);
        }
    }

    private static string FindRepositoryRoot()
    {
        var current = new DirectoryInfo(Environment.CurrentDirectory);
        while (current is not null)
        {
            if (IOFile.Exists(Path.Combine(current.FullName, "DiskMonitor.sln")) &&
                Directory.Exists(Path.Combine(current.FullName, "src")))
            {
                return current.FullName;
            }

            current = current.Parent;
        }

        var fromOutput = new DirectoryInfo(AppContext.BaseDirectory);
        for (var i = 0; i < 4 && fromOutput.Parent is not null; i++)
        {
            fromOutput = fromOutput.Parent;
        }

        return fromOutput.FullName;
    }
}
