param([string]$ConfigDir, [string]$Operation)
$ErrorActionPreference = 'Stop'
# CNG's named key is scoped to the current OS user. No machine-key flag is set.
$provider = [System.Security.Cryptography.CngProvider]::new('Microsoft Platform Crypto Provider')
$name = 'AgentStudio.OperatorDevice.P256.v1'
if ([System.Security.Cryptography.CngKey]::Exists($name, $provider)) {
    $key = [System.Security.Cryptography.CngKey]::Open($name, $provider)
} else {
    $parameters = [System.Security.Cryptography.CngKeyCreationParameters]::new()
    $parameters.Provider = $provider
    $parameters.ExportPolicy = [System.Security.Cryptography.CngExportPolicies]::None
    $parameters.KeyUsage = [System.Security.Cryptography.CngKeyUsages]::Signing
    $key = [System.Security.Cryptography.CngKey]::Create([System.Security.Cryptography.CngAlgorithm]::ECDsaP256, $name, $parameters)
}
try {
    if ($key.ExportPolicy -ne [System.Security.Cryptography.CngExportPolicies]::None) { throw 'Export policy is not non-exportable.' }
    $exported = $false
    try { $null = $key.Export([System.Security.Cryptography.CngKeyBlobFormat]::EccPrivateBlob); $exported = $true } catch [System.Security.Cryptography.CryptographicException] { }
    if ($exported) { throw 'Private key export unexpectedly succeeded.' }
    if ($Operation -eq 'public') {
        $blob = $key.Export([System.Security.Cryptography.CngKeyBlobFormat]::EccPublicBlob)
        function Url64([byte[]]$bytes) { [Convert]::ToBase64String($bytes).TrimEnd('=').Replace('+','-').Replace('/','_') }
        @{ publicJwk = @{ kty = 'EC'; crv = 'P-256'; x = (Url64 $blob[8..39]); y = (Url64 $blob[40..71]) }; nonExportable = $true } | ConvertTo-Json -Compress
    } elseif ($Operation -eq 'sign') {
        $signer = [System.Security.Cryptography.ECDsaCng]::new($key)
        try {
            $signer.HashAlgorithm = [System.Security.Cryptography.CngAlgorithm]::Sha256
            $signature = $signer.SignData([Convert]::FromBase64String([Console]::In.ReadToEnd()))
            @{ signature = [Convert]::ToBase64String($signature) } | ConvertTo-Json -Compress
        } finally { $signer.Dispose() }
    } else { throw 'Unknown operation.' }
} finally { $key.Dispose() }
