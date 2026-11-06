. "$PSScriptRoot/env.linux.ps1"
Push-Location "$repo/src/Demo.Ingress.Functions"
try { func start --port 7102 } finally { Pop-Location }
