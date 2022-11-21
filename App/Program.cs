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

public static class PowerShellUtility
{
    /// <summary>
    /// See this: https://www.powershellgallery.com/packages/PowerShellGet/3.0.17-beta17
    /// </summary>
    private const string PowerShellGetVersion = "3.0.17-beta17";

    private const string PowerShellGet = "PowerShellGet";

    private const string PowerShellGetNupkg = $"{PowerShellGet}.nupkg";

    private const string PowerShellGetUrl = "https://psg-prod-eastus.azureedge.net/packages/powershellget.{0}.nupkg";

    public readonly record struct PowerShellResponse(
        List<string> Result,
        List<string> ErrorMessages,
        bool Failed);

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
            powershell.AddScript($"{path};");
        }, environments);
    }

    /// <summary>
    /// This function runs a PowerShell script inside C#.
    /// 1) downloads PowerShelGet nupkg from PowerShell gallery
    /// 2) unzip nupkg file to access modules folder
    /// 3) Import-Module PowerShellGet so Install-Module Cmdlet becomes available
    /// 4) Runs the PowerShell script
    /// </summary>
    /// <param name="environments">Environment variables accessible to PowerShell</param>
    /// <param name="action">Action to run against PowerShell instance</param>
    /// <returns></returns>
    private static async Task<PowerShellResponse> RunPsSession(Action<PowerShell> action, Dictionary<string, string>? environments)
    {
        var sessionId = Guid.NewGuid().ToString();
        var folderPrefix = Path.Combine(Directory.GetCurrentDirectory(), sessionId);
        var powerShellGetNupkgPath = Path.Combine(folderPrefix, PowerShellGetNupkg);
        var powerShellGetModulePath = Path.Combine(folderPrefix, PowerShellGet);
        
        try
        {
            Directory.CreateDirectory(folderPrefix);

            // Manually download PowerShellGet module
            using var client = new HttpClient();
            await client.DownloadFileTaskAsync(
                new Uri(string.Format(PowerShellGetUrl, PowerShellGetVersion)),
                powerShellGetNupkgPath);

            // UnZip Nupkg file
            ZipFile.ExtractToDirectory(
                powerShellGetNupkgPath,
                powerShellGetModulePath);

            var errorMessages = new List<string>();
            using var powershell = PowerShell.Create();

            // Setting up the PowerShell runspace
            var sessionState = InitialSessionState.CreateDefault2();
            sessionState.ExecutionPolicy = ExecutionPolicy.Unrestricted;
            sessionState.LanguageMode = PSLanguageMode.FullLanguage;

            // Add environment variables
            foreach (var (key, value) in environments ?? new Dictionary<string, string>())
            {
                sessionState.EnvironmentVariables.Add(
                    new SessionStateVariableEntry(
                        key,
                        value,
                        $"{folderPrefix}/{key}={value}"));
            }

            // Using PSGet v3 in order to save the Az modules and its dependencies
            powershell.AddScript(
                $"Import-Module {powerShellGetModulePath} -Force");

            powershell.AddScript(@"
if ((Get-Module -Name PSPackageProject -ListAvailable).Count -eq 0) {
    Install-Module -Name PSPackageProject -Repository PSGallery
}
            ");

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
            return new PowerShellResponse(
                new List<string>(),
                new List<string> { $"Error occured while running command: {e.Message}" },
                true);
        }
        finally
        {
            Directory.Delete(folderPrefix, true);
        }
    }
}

class Program
{
    public static async Task Main(string[] args)
    {
        /*var (results, errors, _) = await PowerShellUtility.RunPsFile(
            Path.Combine(Directory.GetCurrentDirectory(), "PS", "Test.ps1"));*/

        var (results, errors, _) = await PowerShellUtility.RunPsScript("echo 'hello world!'");
        
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