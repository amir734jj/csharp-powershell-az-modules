using System.IO.Compression;
using System.Management.Automation;
using System.Management.Automation.Runspaces;
using Microsoft.PowerShell;

namespace App;

public static class HttpClientExtensions
{
    public static async Task DownloadFileTaskAsync(this HttpClient client, Uri uri, string fileName)
    {
        await using var s = await client.GetStreamAsync(uri);
        await using var fs = new FileStream(fileName, FileMode.CreateNew);
        await s.CopyToAsync(fs);
    }
}

/// <summary>
/// This utility class uses Microsoft.PowerShell.Sdk to simplify running PowerShell scripts
/// modules that install modules from NuGet.
/// </summary>
public static class PowerShellSdkUtility
{
    private const string PowerShellGet = "PowerShellGet";

    private const string Nupkg  = "nupkg";

    private const string PackageGetUrlTemplate = "https://psg-prod-eastus.azureedge.net/packages/{0}.{1}.{2}";
    
    private static readonly string[] AzureModules = {
        "Az",
        "Az.Accounts",
        "Az.RecoveryServices",
        "Az.Storage",
    };
    
    public readonly record struct PowerShellResponse(
        List<string> Result,
        List<string> ErrorMessages,
        bool Failed);

    private readonly record struct PowerShellPackage(
        string Name,
        string ImportVersion,
        string FullVersion);

    /// <summary>
    /// Runs a PowerShell script string
    /// </summary>
    /// <param name="script">PowerShell script to run</param>
    /// <param name="environments">Environment variables accessible to PowerShell</param>
    /// <returns>PowerShell invocation result</returns>
    public static async Task<PowerShellResponse> RunPsScript(string script, Dictionary<string, string>? environments = null)
    {
        return await RunPsSession(powershell =>
        {
            powershell.AddScript(script);
        }, environments);
    }
    
    /// <summary>
    /// Runs a PowerShell file given a path
    /// </summary>
    /// <param name="path">PowerShell script path to run</param>
    /// <param name="environments">Environment variables accessible to PowerShell</param>
    /// <returns>PowerShell invocation result</returns>
    public static async Task<PowerShellResponse> RunPsFile(string path, Dictionary<string, string>? environments = null)
    {
        var workingDirectory = Path.GetDirectoryName(path)!;
        return await RunPsSession(powershell =>
        {
            powershell.AddScript($"Set-Location -Path {workingDirectory};");
            powershell.AddScript(File.ReadAllText(path));
        }, environments);
    }

    /// <summary>
    /// Given an array of packages:
    /// 1) it downloads the package nupkg from PowerShell gallery
    /// 2) unzip nupkg file to access modules folder
    /// 3) Import-Module package
    /// </summary>
    /// <param name="folderPrefix">Folder prefix to put the packages into</param>
    /// <param name="powerShell">PowerShell instance</param>
    /// <param name="packages">List of NuGet packages</param>
    private static async Task DownloadNugetPackages(string folderPrefix, PowerShell powerShell, params PowerShellPackage[] packages)
    {
        using var client = new HttpClient();
        foreach (var package in packages)
        {
            var nupkgPackagePath = Path.Combine(folderPrefix, $"{package.Name}.{Nupkg}");
            var nupkgPackageGetModulePath = Path.Combine(folderPrefix, package.Name);

            await client.DownloadFileTaskAsync(
                new Uri(string.Format(PackageGetUrlTemplate, package.Name, package.FullVersion, Nupkg).ToLower()),
                nupkgPackagePath);

            // UnZip Nupkg file
            ZipFile.ExtractToDirectory(
                nupkgPackagePath,
                nupkgPackageGetModulePath);

            powerShell.AddScript($"Import-Module {nupkgPackageGetModulePath} -RequiredVersion {package.ImportVersion} -Force;");
        }
    }

    /// <summary>
    /// This function runs a PowerShell script inside C#.
    /// </summary>
    /// <param name="environments">Environment variables accessible to PowerShell</param>
    /// <param name="action">Action to run against PowerShell instance</param>
    /// <returns></returns>
    private static async Task<PowerShellResponse> RunPsSession(Action<PowerShell> action, Dictionary<string, string>? environments)
    {
        var sessionId = Guid.NewGuid().ToString();
        var folderPrefix = Path.Combine(Directory.GetCurrentDirectory(), sessionId);
        var packages = new[]
        {
            new PowerShellPackage(PowerShellGet, "3.0.17", "3.0.17-beta17")
        };

        var errorMessages = new List<string>();

        // Setting up the PowerShell runspace
        var initialSessionState = InitialSessionState.CreateDefault();
        initialSessionState.ExecutionPolicy = ExecutionPolicy.Unrestricted;
        initialSessionState.LanguageMode = PSLanguageMode.FullLanguage;

        environments ??= new Dictionary<string, string>();

        // This environment variable instructs PowerShellGet to download modules into current session folder
        // to avoid polluting the namespace and file system.
        environments.Add("PSModulePath", folderPrefix);

        // Add environment variables
        foreach (var (key, value) in environments)
        {
            initialSessionState.EnvironmentVariables.Add(
                new SessionStateVariableEntry(
                    key,
                    value,
                    $"{folderPrefix}/{key}={value}"));
        }

        PowerShell? powershell = null;

        try
        {
            // Create session folder
            Directory.CreateDirectory(folderPrefix);

            powershell = PowerShell.Create(initialSessionState);

            powershell.AddScript("Set-ExecutionPolicy -Scope CurrentUser -ExecutionPolicy Unrestricted;");

            // Manually download and import the packages. This is needed because PowerShell that comes with C# is
            // bare bone and does not have a way to interact with NuGet.
            await DownloadNugetPackages(folderPrefix, powershell, packages);

            // Install Az using the new Install-PSResource that comes with PowerShellGet v3+.
            // It combines the legacy functions of the Install-Module and Install-Script cmdlets from PowerShellGet v2.
            // Source: https://github.com/PowerShell/PowerShellGet/blob/master/help/Install-PSResource.md
            foreach (var azureModule in AzureModules)
            {
                powershell.AddScript($"Install-PSResource -Name {azureModule} -Repository PSGallery -TrustRepository -Scope CurrentUser -Quiet;");
            }

            powershell.AddScript("Write-Output $env:PSModulePath");
            
            action(powershell);

            var result = powershell.Invoke();

            if (powershell.HadErrors)
            {
                errorMessages.AddRange(powershell.Streams.Error.Select(x => x.ToString()));
            }

            return new PowerShellResponse(result.Select(x => x.ToString()).ToList(), errorMessages, powershell.HadErrors);
        }
        catch (Exception e)
        {
            if (powershell != null)
            {
                var info = powershell.Streams.Information.Select(x => x.ToString()).ToList();
                var verbose = powershell.Streams.Verbose.Select(x => x.ToString()).ToList();
                var warnings = powershell.Streams.Warning.Select(x => x.ToString()).ToList();
                var progress = powershell.Streams.Progress.Select(x => x.ToString()).ToList();
                var debugs = powershell.Streams.Debug.Select(x => x.ToString()).ToList();
            }
            
            return new PowerShellResponse(
                new List<string>(),
                new List<string>
                {
                    $"Error occurred while running command: {e.Message}"
                }.Concat(powershell?.Streams.Error.Select(x => x.ToString()) ?? Array.Empty<string>()).ToList(),
                true);
        }
        finally
        {
            powershell?.Dispose();
            DeleteDirectorySafely(folderPrefix);
        }
    }

    private static void DeleteDirectorySafely(string path)
    {
        try
        {
            Directory.Delete(path, true);
        }
        catch (UnauthorizedAccessException e)
        {
            Console.Error.WriteLine($"UnauthorizedAccessException when deleting the directory {path}: {e.Message}");
        }
        catch (Exception e)
        {
            Console.Error.WriteLine($"Exception when deleting the directory {path}: {e.Message}");
        }
    }
}

class Program
{
    public static async Task Main(string[] args)
    {
        //var (results, errors, _) = await PowerShellSdkUtility.RunPsFile(@"C:\Users\shesamian\RiderProjects\AzureStack-Fiji-Workloads\src\ASR-EdgeZone\E2EEZtoAZ-remote-cache.ps1",
        var (results, errors, _) = await PowerShellSdkUtility.RunPsScript(@"
# Import Azure module
Import-Module 'Az'
Import-Module 'Az.Accounts'
Import-Module 'Az.RecoveryServices'
Import-Module 'Az.Storage'

New-AzStorageAccount",
            new Dictionary<string, string>());
        
        foreach (var errorMsg in errors)
        {
            await Console.Error.WriteLineAsync($"error: {errorMsg}");
        }
        
        foreach (var result in results)
        {
            Console.WriteLine($"result: {result}");
        }
    }
}