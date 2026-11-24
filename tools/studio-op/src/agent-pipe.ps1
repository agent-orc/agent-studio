# User-only named-pipe transport. The Node resident keeps the key and signs.
param([string]$PipeName)
$ErrorActionPreference = 'Stop'
$identity = [System.Security.Principal.WindowsIdentity]::GetCurrent().User
$security = [System.IO.Pipes.PipeSecurity]::new()
$security.SetAccessRuleProtection($true, $false)
$security.AddAccessRule([System.IO.Pipes.PipeAccessRule]::new($identity, [System.IO.Pipes.PipeAccessRights]::FullControl, [System.Security.AccessControl.AccessControlType]::Allow))
while ($true) {
    $pipe = [System.IO.Pipes.NamedPipeServerStream]::new($PipeName, [System.IO.Pipes.PipeDirection]::InOut, 1, [System.IO.Pipes.PipeTransmissionMode]::Byte, [System.IO.Pipes.PipeOptions]::None, 16384, 16384, $security)
    [Console]::WriteLine('READY')
    $pipe.WaitForConnection()
    try {
        $reader = [System.IO.StreamReader]::new($pipe)
        $writer = [System.IO.StreamWriter]::new($pipe)
        $writer.AutoFlush = $true
        $line = $reader.ReadLine()
        if ($null -ne $line -and $line.Length -le 8192) {
            [Console]::WriteLine($line)
            $writer.WriteLine([Console]::ReadLine())
        }
    } finally { $pipe.Dispose() }
}
