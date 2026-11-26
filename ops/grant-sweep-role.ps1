# After every provision: give the sweep job's managed identity the Shipments.Read.App app role on the Public API.
# azd creates a NEW identity at every azd up, and Bicep can't assign Entra app roles, so this runs as an azd hook.
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $true   # a failing az command stops the script

$jobPrincipalId = $env:SWEEP_IDENTITY_PRINCIPALID          # an output of main.bicep
$apiClientId    = $env:AZURE_ENTRA_PUBLIC_API_CLIENT_ID    # the azd parameter you entered
if (-not $jobPrincipalId -or -not $apiClientId) {
    throw "SWEEP_IDENTITY_PRINCIPALID or AZURE_ENTRA_PUBLIC_API_CLIENT_ID is missing from the azd environment."
}

# The Public API's identity in this tenant (its service principal), and the role's ID, looked up by its value.
$apiSpId = az ad sp show --id $apiClientId --query id -o tsv
$roleId  = az ad sp show --id $apiClientId --query "appRoles[?value=='Shipments.Read.App'].id | [0]" -o tsv
if (-not $roleId) { throw "The Public API has no app role called Shipments.Read.App." }

$assignments = "https://graph.microsoft.com/v1.0/servicePrincipals/$jobPrincipalId/appRoleAssignments"

# Idempotent: running it twice doesn't assign twice.
# No parentheses in the query: on Windows, az is a batch file, and cmd misreads them.
$existing = az rest --method GET --url $assignments --query "value[?appRoleId=='$roleId'].id" -o tsv
if ($existing) {
    Write-Host "The sweep job's identity already has Shipments.Read.App."
    return
}

# A brand-new identity can take a few seconds to appear in Entra, so try a few times.
$body = New-TemporaryFile
@{ principalId = $jobPrincipalId; resourceId = $apiSpId; appRoleId = $roleId } | ConvertTo-Json -Compress |
    Set-Content -Path $body -Encoding ascii
try {
    foreach ($attempt in 1..6) {
        try {
            az rest --method POST --url $assignments --headers "Content-Type=application/json" --body "@$body" | Out-Null
            Write-Host "Gave the sweep job's identity Shipments.Read.App (attempt $attempt)."
            return
        }
        catch {
            if ($attempt -eq 6) { throw }
            Write-Host "Entra doesn't know the new identity yet; trying again in 10 s."
            Start-Sleep -Seconds 10
        }
    }
}
finally {
    Remove-Item $body -ErrorAction SilentlyContinue
}