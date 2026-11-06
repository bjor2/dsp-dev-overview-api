# Windows variant: must be ignored when a .linux.ps1 exists.
. "$PSScriptRoot\env.ps1"
dotnet run --project "$repo\src\Demo.Api.Windows" --launch-profile Demo.Api
