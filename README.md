# csharp-powershell-az-modules
Repo showing how to run Azure PowerShell modules inside C# PowerShell session

The problem is PowerShell in C# doesn't have `Install-Module`, `Uninstall-Module`, `Update-Module` Cmdlets.
To be able to install latest `Az` modules or any other PowerShell library, you need to `Import-Module` latest PowerShellGet.

```csharp
// Run PowerShell from the file (note that working directory needs to be correctly set for it function properly)
var (results, errors, failed) = await PowerShellUtility.RunPsFile(
  Path.Combine(Directory.GetCurrentDirectory(), "PS", "Test.ps1"));

// OR run script from string
var (results, errors, failed) = await PowerShellUtility.RunPsScript("echo 'hello world!'");
```
