# Credential literals compiled into the fixture DLLs of ScanBinaries.Tests.ps1. Fragments keep this file clean when scanned.
# Kind: us = a C# string literal (UTF-16 in the #US heap), attr = an [assembly: AssemblyMetadata] value (UTF-8 SerString in a
# custom-attribute blob, preceded by its 1-byte length), resx = a .resx string resource (UTF-8 in the .resources stream,
# preceded by its 7-bit length).
$pw = 'Pass' + 'word'; $pwd = 'P' + 'wd'; $uid = 'User' + ' ID'; $v = 'Zq7x' + 'K2mP9'
# Pads a literal to exactly $n UTF-8 bytes with x...; so its length byte is the ASCII character the verifier chose.
function script:Pad-Literal([string]$s, [int]$n) { if ($s.Length -ge $n) { $s } else { $s + ('x' * ($n - $s.Length - 1)) + ';' } }
@(
    # The three v-03b verifier counterexamples (run/agents/v-03b-infra-probes dll_one_pwd, dll_one_userid, dll_one_spaced).
    @{ Name = 'verifier-pwd';       Kind = 'us'; Literal = "$pwd=$v" }
    @{ Name = 'verifier-userid-db'; Kind = 'us'; Literal = "$uid=bloodbank_admin;Database=BloodBank" }
    @{ Name = 'verifier-spaced';    Kind = 'us'; Literal = "$pw = $v; Database = BloodBank" }
    # Five more realistic variants, with different lengths so the length byte before each literal differs.
    @{ Name = 'azure-full';         Kind = 'us'; Literal = "Server=tcp:lifestream-sql.database.windows.net,1433;Initial Catalog=BloodBank;$uid=app;$pw=$v" }
    @{ Name = 'lowercase-password'; Kind = 'us'; Literal = "$($pw.ToLower())=$v;Server=localhost" }
    @{ Name = 'uppercase-pwd-uid';  Kind = 'us'; Literal = "$($pwd.ToUpper())=$v;UI$('D')=sa" }
    @{ Name = 'userid-synonym';     Kind = 'us'; Literal = "$($uid -replace ' ','')=bloodbank_admin;$pw=$v" }
    @{ Name = 'spaced-userid-only'; Kind = 'us'; Literal = "$($uid.ToLower().Replace('id','Id')) = svc_lifestream ; $pw = $v ;" }
    # The v-03c verifier counterexamples (run/agents/v-03c-infra-probes/make.py), same literals and the same UTF-8 lengths, so
    # the length byte in front of each is the character named in the case ('A' = 65, '.' = 46, '@' = 64, 'u' = 117, 'P' = 80).
    @{ Name = 'v03c-attr-pwd-len65-A';        Kind = 'attr'; Literal = (Pad-Literal "$pwd=$v;Server=tcp:x.example;Database=BloodBank;" 65) }
    @{ Name = 'v03c-attr-password-len46-dot'; Kind = 'attr'; Literal = (Pad-Literal "$pw=$v;Server=tcp:x.example;" 46) }
    @{ Name = 'v03c-attr-password-len64-at';  Kind = 'attr'; Literal = (Pad-Literal "$pw=$v;Server=tcp:x.example;Database=B;" 64) }
    @{ Name = 'v03c-attr-userid-len117-u';    Kind = 'attr'; Literal = (Pad-Literal "$uid=svc_admin;Database=BloodBank;Server=tcp:x.example;" 117) }
    @{ Name = 'v03c-attr-spaced-len80-P';     Kind = 'attr'; Literal = (Pad-Literal "$pw = $v; Server=tcp:x.example; Database=BloodBank;" 80) }
    @{ Name = 'v03c-resx-pwd-len65-A';        Kind = 'resx'; Literal = (Pad-Literal "$pwd=$v;Server=tcp:x.example;Database=BloodBank;" 65) }
    @{ Name = 'v03c-resx-spaced-len80-P';     Kind = 'resx'; Literal = (Pad-Literal "$pw = $v; Server=tcp:x.example; Database=BloodBank;" 80) }
    @{ Name = 'v03c-us-cyrillic-value';       Kind = 'us'; Literal = "Server=x;$uid=sa_admin_x;$pw=$([char]0x43F)$([char]0x430)$([char]0x440)$([char]0x43E)$([char]0x43B)$([char]0x44C)$v" }
    @{ Name = 'v03c-us-cyrillic-only-pwd';    Kind = 'us'; Literal = "Server=x;Database=BloodBank;$pw=$([char]0x43F)$([char]0x430)$([char]0x440)$([char]0x43E)$([char]0x43B)$([char]0x44C)$v" }
    @{ Name = 'v03c-us-euro-first';           Kind = 'us'; Literal = "Server=x;Database=BloodBank;$pw=$([char]0x20AC)$v" }
    @{ Name = 'v03c-us-brace-then-bang';      Kind = 'us'; Literal = "Server=x;Database=BloodBank;$pwd={abc}!$v" }
    @{ Name = 'v03c-us-odbc-brace';           Kind = 'us'; Literal = "Driver={ODBC Driver 18 for SQL Server};Server=x;UI$('D')=sa;$pwd={Summer-2024};" }
    @{ Name = 'v03c-us-percent-then';         Kind = 'us'; Literal = "Server=x;Database=BloodBank;$pw=%abc%-$v" }
    # The same value shapes as UTF-8 (attribute blob) so the shapes are not only covered in UTF-16.
    @{ Name = 'attr-cyrillic-first';          Kind = 'attr'; Literal = "$pw=$([char]0x43F)$([char]0x430)$([char]0x440)$([char]0x43E)$([char]0x43B)$([char]0x44C)$v" }
    @{ Name = 'attr-euro-first';              Kind = 'attr'; Literal = "$pw=$([char]0x20AC)$v" }
    @{ Name = 'attr-brace-then-bang';         Kind = 'attr'; Literal = "$pwd={abc}!$v" }
    @{ Name = 'attr-odbc-brace';              Kind = 'attr'; Literal = "UI$('D')=sa;$pwd={Summer-2024};" }
    @{ Name = 'resx-percent-then';            Kind = 'resx'; Literal = "$pw=%abc%-$v" }
)
