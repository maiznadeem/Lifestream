<#
.SYNOPSIS
  Creates the two contained database users of Lifestream (managed identities, no passwords) and gives them only what they need.

.DESCRIPTION
  Run once by the orchestrator after infra/main.bicep has created the identities, and again whenever the identities change
  (it is idempotent). Connects to the database as the signed-in Microsoft Entra administrator of the SQL server, with an
  access token from `az account get-access-token`; no password is used or written anywhere.

    lifestream-api       the API container app (system-assigned identity)   db_datareader, db_datawriter, EXECUTE. No DDL.
    lifestream-migrator  the migrate job (user-assigned identity)           db_ddladmin, db_datareader, db_datawriter.

  The Entra administrator itself is set by main.bicep (resource Microsoft.Sql/servers/administrators), not here.

  The database may be paused (serverless free offer): the first connection wakes it, which fails with error 40613 for up to
  about a minute, so the connection is retried until -WakeTimeoutSeconds.

.PARAMETER ApiPrincipalId        Output apiPrincipalId of main.bicep (object id of the API's system-assigned identity).
.PARAMETER MigratorPrincipalId   Output migratorPrincipalId of main.bicep.
.PARAMETER PrintOnly             Prints the T-SQL that would run and exits; no connection, no Azure call. Used by the tests.
#>
[CmdletBinding()]
param(
    [string]$ServerName = 'lifestream-server',
    [string]$Database = 'BloodBank',
    [Parameter(Mandatory)][ValidatePattern('^[0-9a-fA-F]{8}-([0-9a-fA-F]{4}-){3}[0-9a-fA-F]{12}$')][string]$ApiPrincipalId,
    [ValidatePattern('^[A-Za-z0-9-]{1,64}$')][string]$ApiName = 'lifestream-api',
    [Parameter(Mandatory)][ValidatePattern('^[0-9a-fA-F]{8}-([0-9a-fA-F]{4}-){3}[0-9a-fA-F]{12}$')][string]$MigratorPrincipalId,
    [ValidatePattern('^[A-Za-z0-9-]{1,64}$')][string]$MigratorName = 'lifestream-migrator',
    [int]$WakeTimeoutSeconds = 240,
    [switch]$PrintOnly
)

$ErrorActionPreference = 'Stop'

function New-UserSql {
    param([string]$Name, [string]$ObjectId, [string[]]$Roles, [string[]]$Grants)
    $lines = @(
        "IF NOT EXISTS (SELECT 1 FROM sys.database_principals WHERE name = N'$Name')"
        "    CREATE USER [$Name] FROM EXTERNAL PROVIDER WITH OBJECT_ID = '$ObjectId';"
    )
    foreach ($role in $Roles) { $lines += "ALTER ROLE [$role] ADD MEMBER [$Name];" }
    foreach ($grant in $Grants) { $lines += "GRANT $grant TO [$Name];" }
    $lines -join "`n"
}

$statements = @(
    (New-UserSql -Name $ApiName -ObjectId $ApiPrincipalId -Roles 'db_datareader', 'db_datawriter' -Grants 'EXECUTE')
    (New-UserSql -Name $MigratorName -ObjectId $MigratorPrincipalId -Roles 'db_ddladmin', 'db_datareader', 'db_datawriter' -Grants @())
)

# Proof that the API user holds nothing beyond read, write and execute, and that the migrator is the only DDL identity.
# Returns one row per violation; an empty result means the permissions are exactly as intended.
$verifySql = @"
SELECT problem FROM (
    SELECT CONCAT(N'$ApiName is a member of ', r.name) COLLATE DATABASE_DEFAULT AS problem
    FROM sys.database_role_members m
    JOIN sys.database_principals u ON u.principal_id = m.member_principal_id AND u.name = N'$ApiName'
    JOIN sys.database_principals r ON r.principal_id = m.role_principal_id
    WHERE r.name NOT IN (N'db_datareader', N'db_datawriter')
    UNION ALL
    SELECT CONCAT(N'$ApiName holds ', p.state_desc, N' ', p.permission_name, N' on ', p.class_desc) COLLATE DATABASE_DEFAULT
    FROM sys.database_permissions p
    JOIN sys.database_principals u ON u.principal_id = p.grantee_principal_id AND u.name = N'$ApiName'
    WHERE p.permission_name NOT IN (N'EXECUTE', N'CONNECT') OR p.state_desc NOT IN (N'GRANT')
    UNION ALL
    SELECT N'$MigratorName is missing db_ddladmin' COLLATE DATABASE_DEFAULT
    WHERE ISNULL(IS_ROLEMEMBER(N'db_ddladmin', N'$MigratorName'), 0) <> 1
    UNION ALL
    SELECT N'$ApiName is a member of db_ddladmin' COLLATE DATABASE_DEFAULT
    WHERE IS_ROLEMEMBER(N'db_ddladmin', N'$ApiName') = 1
) v;
"@

if ($PrintOnly) {
    "-- Run against [$Database] on $ServerName as the Entra administrator."
    $statements
    "-- Verification (must return no rows):"
    $verifySql
    return
}

# SqlConnection.AccessToken needs System.Data.SqlClient, which ships with Windows PowerShell 5.1 but not with PowerShell 7.
if ($PSVersionTable.PSEdition -eq 'Core') {
    $forward = @()
    foreach ($key in $PSBoundParameters.Keys) {
        $v = $PSBoundParameters[$key]
        if ($v -is [switch]) { if ($v.IsPresent) { $forward += "-$key" } } else { $forward += "-$key"; $forward += "$v" }
    }
    & powershell.exe -NoProfile -ExecutionPolicy Bypass -File $PSCommandPath @forward
    exit $LASTEXITCODE
}

Add-Type -AssemblyName System.Data
$token = (& az account get-access-token --resource https://database.windows.net/ --query accessToken -o tsv)
if ($LASTEXITCODE -ne 0 -or -not $token) { throw "az account get-access-token failed. Run 'az login' as the Entra administrator of $ServerName." }

$connectionString = "Server=tcp:$ServerName.database.windows.net,1433;Database=$Database;Encrypt=True;TrustServerCertificate=False;Connection Timeout=60"
$deadline = (Get-Date).AddSeconds($WakeTimeoutSeconds)
$connection = $null
while ($true) {
    $connection = New-Object System.Data.SqlClient.SqlConnection $connectionString
    $connection.AccessToken = $token
    try { $connection.Open(); break }
    catch [System.Data.SqlClient.SqlException] {
        $connection.Dispose()
        $waking = $_.Exception.Number -in 40613, 40197, 40501, 49918, 4060
        if ($_.Exception.Number -eq 40615) { throw "The SQL firewall does not admit this machine (error 40615). Add a temporary client IP rule, see infra/README.md step 3." }
        if (-not $waking -or (Get-Date) -gt $deadline) { throw }
        Write-Host "Database is waking (error $($_.Exception.Number)); retrying in 10 s"
        Start-Sleep -Seconds 10
    }
}

try {
    foreach ($sql in $statements) {
        $command = $connection.CreateCommand(); $command.CommandText = $sql; $command.CommandTimeout = 120
        [void]$command.ExecuteNonQuery()
    }
    $command = $connection.CreateCommand(); $command.CommandText = $verifySql
    $reader = $command.ExecuteReader()
    $problems = @()
    while ($reader.Read()) { $problems += $reader.GetString(0) }
    $reader.Dispose()
    if ($problems.Count -gt 0) { $problems | ForEach-Object { Write-Host "PROBLEM: $_" }; exit 1 }
    Write-Host "OK: $ApiName holds db_datareader, db_datawriter and EXECUTE only; $MigratorName holds db_ddladmin, db_datareader, db_datawriter."
} finally {
    $connection.Dispose()
}
