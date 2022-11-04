using System.Diagnostics;
using System.Management.Automation;
using System.Management.Automation.Runspaces;

var script = @$"
[Net.ServicePointManager]::SecurityProtocol = [Net.ServicePointManager]::SecurityProtocol -bor [Net.SecurityProtocolType]::Tls12

Install-PackageProvider -Name NuGet -Force
Install-Module PowerShellGet -AllowClobber -Force
Set-PSRepository -Name PSGallery -InstallationPolicy Trusted

Install-PackageProvider -Name NuGet

Install-Module -Name Nuget -Scope CurrentUser -Repository PSGallery -AllowClobber
Install-Module -Name PowerShellGet -Scope CurrentUser -Repository PSGallery -AllowClobber

Install-Module -Name 'Az' -Scope CurrentUser -Repository PSGallery -Force

Import-Module 'Az'
Import-Module 'Az.Accounts'
Import-Module 'Az.RecoveryServices'

New-AzResourceGroup -Name 'TestRg123' -Location 'eastus2euap'
";


var initialState = InitialSessionState.CreateDefault2();
initialState.LanguageMode = PSLanguageMode.ConstrainedLanguage;

using var powershell = PowerShell.Create(initialState);
var results = powershell
    .AddScript(script).Invoke();

foreach (var outputItem in results)
{
    Debug.WriteLine(outputItem);
}
