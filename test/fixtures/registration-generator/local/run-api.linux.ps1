# Starts Demo.Api on https://localhost:6011 (Swagger at /swagger).
. "$PSScriptRoot/env.linux.ps1"
dotnet run --project "$repo/src/Demo.Api" --launch-profile Demo.Api
