. "$PSScriptRoot/env.linux.ps1"
Push-Location "$repo/src/Demo.Functions"
try { func start --port 7101 } finally { Pop-Location }
