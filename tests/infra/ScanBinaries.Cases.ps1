# Credential literals compiled into the fixture DLLs of ScanBinaries.Tests.ps1. Fragments keep this file clean when scanned.
$pw = 'Pass' + 'word'; $pwd = 'P' + 'wd'; $uid = 'User' + ' ID'; $v = 'Zq7x' + 'K2mP9'
@(
    # The three verifier counterexamples (v-03b-infra dll_one_pwd, dll_one_userid, dll_one_spaced).
    @{ Name = 'verifier-pwd';       Literal = "$pwd=$v" }
    @{ Name = 'verifier-userid-db'; Literal = "$uid=bloodbank_admin;Database=BloodBank" }
    @{ Name = 'verifier-spaced';    Literal = "$pw = $v; Database = BloodBank" }
    # Five more realistic variants, with different lengths so the length byte before each literal differs.
    @{ Name = 'azure-full';         Literal = "Server=tcp:lifestream-sql.database.windows.net,1433;Initial Catalog=BloodBank;$uid=app;$pw=$v" }
    @{ Name = 'lowercase-password'; Literal = "$($pw.ToLower())=$v;Server=localhost" }
    @{ Name = 'uppercase-pwd-uid';  Literal = "$($pwd.ToUpper())=$v;UID=sa" }
    @{ Name = 'userid-synonym';     Literal = "$($uid -replace ' ','')=bloodbank_admin;$pw=$v" }
    @{ Name = 'spaced-userid-only'; Literal = "$($uid.ToLower().Replace('id','Id')) = svc_lifestream ; $pw = $v ;" }
)
