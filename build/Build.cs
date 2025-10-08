using System;
using System.Buffers.Text;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Nuke.Common;
using Nuke.Common.CI;
using Nuke.Common.CI.AppVeyor;
using Nuke.Common.CI.AzurePipelines;
using Nuke.Common.Execution;
using Nuke.Common.IO;
using Nuke.Common.ProjectModel;
using Nuke.Common.Tooling;
using Nuke.Common.Tools.DotNet;
using Nuke.Common.Tools.MSBuild;
using Nuke.Common.Tools.PowerShell;
using Nuke.Common.Tools.Pwsh;
using Nuke.Common.Utilities.Collections;
using Nuke.Common.Tools.SignTool;
using static Nuke.Common.Tools.SignTool.SignToolTasks;
using static Nuke.Common.Tools.DotNet.DotNetTasks;
using static Nuke.Common.Tools.Pwsh.PwshTasks;
using static Nuke.Common.Tools.PowerShell.PowerShellTasks;

[UnsetVisualStudioEnvironmentVariables]
[MSBuildVerbosityMapping]
[AzurePipelines(
    AzurePipelinesImage.WindowsLatest,
    InvokedTargets = new[] { nameof(Test) },
    ExcludedTargets = new[] { nameof(Clean) },
    NonEntryTargets = new[] { nameof(Restore), nameof(Compile) })]
[AppVeyor(
    AppVeyorImage.VisualStudioLatest,
    InvokedTargets = new[] { nameof(Test) },
    AutoGenerate = false)]
class Build : NukeBuild
{
    /// Support plugins are available for:
    ///   - JetBrains ReSharper        https://nuke.build/resharper
    ///   - JetBrains Rider            https://nuke.build/rider
    ///   - Microsoft VisualStudio     https://nuke.build/visualstudio
    ///   - Microsoft VSCode           https://nuke.build/vscode
    public static int Main() => Execute<Build>(x => x.Compile);

    [Parameter("Configuration to build - Default is 'Debug' (local) or 'Release' (server)")]
    // ReSharper disable once ConditionIsAlwaysTrueOrFalse
    readonly Configuration Configuration = (IsLocalBuild && false) ? Configuration.Debug : Configuration.Release;

    [CI] readonly AzurePipelines AzurePipelines;
    [Solution] readonly Solution Solution;

    Target Clean => _ => _
        .Before(Restore)
        .Executes(() =>
        {
            var directories = RootDirectory
                .GlobDirectories(
                    "*/src/**/obj",
                    "*/src/**/bin",
                    "*/test/**/obj",
                    "*/test/**/bin");

            Console.WriteLine($"{RootDirectory} Count: {directories.Count}");
            if (directories.Any())
            {
                Console.WriteLine("v  ");
                Console.WriteLine(string.Join(Environment.NewLine, directories.Select(x => "|- " + x.ToString())));
            }

            directories
                .ForEach(path => path.DeleteDirectory());

            CompileDirectory.DeleteDirectory();
        });

    Target Restore => _ => _
        .Executes(() =>
        {
            DotNetRestore(_ => _
                .SetProjectFile(Solution));
        });

    static AbsolutePath CompileDirectory => RootDirectory / "output" / "lib";

    Target Compile => _ => _
        .DependsOn(Restore)
        .Executes(() =>
        {
            DotNetBuild(_ => _
                .SetProjectFile(Solution)
                .SetNoRestore(InvokedTargets.Contains(Restore))
                .SetConfiguration(Configuration)
                .SetOutputDirectory(CompileDirectory)
                .SetProperty("SourceLinkCreate", true));
        });

    Target Publish => _ => _
        .DependsOn(SignDlls)
        .Executes(() =>
        {
            // Your publish logic here
        });

    Target SignDlls => s => s
        // .DependsOn(Compile)
        .Executes(() =>
        {
            // var packageExecutable = NuGetToolPathResolver.GetPackageExecutable(packageId: "Nivot.StrongNaming",
            //     packageExecutable: "Nivot.PowerShell.StrongNaming.dll");

            var packageExecutablePath = Path.GetDirectoryName(@"H:\$Resources\strongnaming\StrongNaming\bin\Debug\Scripts\StrongNaming.psd1");

            CompileDirectory
                .GlobFiles("**/*.bak")
                .ForEach(path => path.DeleteFile());

            var files = CompileDirectory.GlobFiles("**/Abp*.dll");
            Console.WriteLine($"{CompileDirectory} Count: {files.Count}");


            if (files.Any())
            {
                AbsolutePath outputDirectory = RootDirectory / "output" / "lib-signed";
                outputDirectory.CreateOrCleanDirectory();

                var f = string.Join($", `{Environment.NewLine}", files.Select(x => "  " + x.ToString("d")));

                var powershellscript = $$"""
                                         
                                         import-module (join-path '{{packageExecutablePath}}' StrongNaming.psd1)

                                         # $pass = ConvertTo-SecureString 'brain24h' -asplaintext -force
                                         # $key = Import-StrongNameKeyPair -KeyFile 'H:\braincrm\trunk\Contact Center\Fontes\Brain.CRM.Signing\braincrm.net.pfx' -Password $pass

                                         $key = Import-StrongNameKeyPair -KeyFile 'H:\braincrm\trunk\Contact Center\Fontes\Brain.CRM.Signing\braincrm_net_2022.snk'

                                         $stringArray1 = @('{{CompileDirectory.ToString("nq")}}')

                                         $stringArray2 = @( `
                                         {{f}} `
                                         )

                                         # foreach ($item in $stringArray1) {
                                         #    Write-Host "FOREACH ITEM 1: $item"
                                         #    dir -rec $item | `
                                         #      where { -not (Test-StrongName $_ -ErrorAction SilentlyContinue) } | `
                                         #      Set-StrongName -KeyPair $key -BaseDir '{{CompileDirectory.ToString()}}' -OutputDir '{{outputDirectory.ToString()}}' -Force
                                         #    Write-Host "=================================================================="
                                         # }

                                         foreach ($item in $stringArray1) {
                                            Write-Host "FOREACH ITEM 1: $item"
                                            $files = Get-ChildItem -Path $item -Recurse -Include '*.dll'
                                            foreach ($f in $files) {
                                                if ($f.FullName -notmatch 'runtimes') {
                                                if (-not (Test-StrongName -AssemblyFile $f.FullName -ErrorAction SilentlyContinue)) {
                                                    Write-Host "Processing: $($f.FullName)"
                                                    Set-StrongName -AssemblyFile $f.FullName -KeyPair $key -BaseDir '{{CompileDirectory.ToString()}}' -OutputDir '{{outputDirectory.ToString()}}' -Force
                                                    Write-Host "=================================================================="
                                                }
                                                }
                                            }
                                         }

                                         foreach ($item in $stringArray2) {
                                            Write-Host "FOREACH ITEM 2: $item"
                                            if (-not (Test-StrongName -AssemblyFile $item -ErrorAction SilentlyContinue)) {
                                                Set-StrongName -AssemblyFile $item -KeyPair $key -BaseDir '{{CompileDirectory.ToString()}}' -OutputDir '{{outputDirectory.ToString()}}' -Force
                                            }
                                            Write-Host "=================================================================="
                                         }
                                         """;
                PowerShell(s => s
                    .SetCommand("Invoke-Command")
                    .SetProcessAdditionalArguments("-ScriptBlock { " + powershellscript.Replace("\"", "\\\"") + "}")
                );

                // Change working directory and environment variables
                // var arg =
                //     $"-R \"{x.ToString()}\" \"H:\\braincrm\\trunk\\Contact Center\\Fontes\\Brain.CRM.Signing\\braincrm_net_2022.snk\"";
                // sn(
                //     arguments: arg,
                //     workingDirectory: CompileDirectory
                //     // environmentVariables: EnvironmentInfo.Variables
                //     //     .ToDictionary(x => x.Key, x => x.Value)
                //     //     .SetKeyValue("key", "value").AsReadOnly()
                //     );

                // Signing: Use sn -R [assembly.dll] [keyfile.snk] to sign an existing assembly.
                // Verification: Use sn -v [assembly.dll] to verify the signature of an assembly.

                // SignTool(s => s
                //     .SetFile(@"H:\braincrm\trunk\Contact Center\Fontes\Brain.CRM.Signing\braincrm.net.pfx") // Replace with your DLL path
                //     .SetPassword("brain24h") // Replace with your password
                //     // .SetAutomaticSelection(true)
                //     // .SetKeyContainer(@"H:\braincrm\trunk\Contact Center\Fontes\Brain.CRM.Signing\braincrm_net_2022.snk") // Replace with your DLL path
                //     // .SetCsp(@"H:\braincrm\trunk\Contact Center\Fontes\Brain.CRM.Signing\braincrm_net_2022.snk")
                //     .SetTimestampServerUrl("http://timestamp.digicert.com") // .SetTimestampServerDigestAlgorithm("SHA256")
                //     // .SetFileDigestAlgorithm("SHA256")
                //     .SetFileDigestAlgorithm("SHA1")
                //     .SetVerbose(true)
                //     .SetFiles(CompileDirectory
                //         .GlobFiles($"*/Abp*.dll")
                //         .Select(x => x.ToString("dn"))
                //         .ToArray()
                //     ));
            }
        });

    static AbsolutePath PackagesDirectory => RootDirectory / "output" / "packages";

    Target Pack => _ => _
        .DependsOn(Compile)
        .Produces(PackagesDirectory / "*.nupkg")
        .Executes(() =>
        {
            DotNetPack(_ => _
                .SetConfiguration(Configuration)
                .SetOutputDirectory(PackagesDirectory)
                .SetNoBuild(InvokedTargets.Contains(Compile))
                .SetProperty("SourceLinkCreate", true)
                .CombineWith(
                    Solution.AllProjects.Where(x => x.SolutionFolder?.Name == "src"), (_, v) => _
                        .SetProject(v)));
        });


    AbsolutePath TestResultDirectory => RootDirectory / "output" / "test-results";

    Target Test => _ => _
        .DependsOn(Compile)
        .Partition(4)
        .Executes(() =>
        {
            var allTestConfigurations =
                from project in Solution.GetAllProjects("*Tests")
                from targetFramework in project.GetTargetFrameworks()
                select (project, targetFramework);
            var relevantTestConfigurations = Partition.GetCurrent(allTestConfigurations);

            try
            {
                DotNetTest(_ => _
                        .SetConfiguration(Configuration.Release)
                        .SetNoBuild(InvokedTargets.Contains(Compile))
                        .SetResultsDirectory(TestResultDirectory)
                        .CombineWith(relevantTestConfigurations, (_, v) => _
                            .SetProjectFile(v.project)
                            .SetFramework(v.targetFramework)
                            .SetLoggers($"trx;LogFileName={v.project.Name}.trx")
                        ),
                    completeOnFailure: true);
            }
            finally
            {
                TestResultDirectory.GlobFiles("*.trx").ForEach(x =>
                    AzurePipelines?.PublishTestResults(
                        type: AzurePipelinesTestResultsType.VSTest,
                        title: $"{Path.GetFileNameWithoutExtension(x)} ({AzurePipelines.StageDisplayName})",
                        files: new string[] { x }));
            }
        });
}