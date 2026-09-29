[CmdletBinding(SupportsShouldProcess)]
param(
    [string] $Target = 'AgentStudio/TaskServer/studio-robert-windows',

    # One-time import from the protected file a Task Server rotation response
    # was redirected to. Without it the credential is read as a SecureString
    # prompt, so it never appears on a command line or in shell history.
    [string] $FromFile,

    [switch] $RemoveSourceFile
)

<#
Stores or rotates the loopback Studio connector's Task Server credential in
Windows Credential Manager (a generic credential, persisted per user). The
connector (OrchestratorApi with OrchestratorApi__Profile=connector) re-reads
this entry at most every Connector:CredentialRefreshSeconds (default 5) and
immediately after the Task Server rejects the previous value, then proves the
new value with a fresh attach handshake. Rotation therefore needs no connector
restart. Run it as the user the connector Scheduled Task runs as.

The connector never reads the credential from an environment variable or from
studio-connector.env and refuses to boot if one is configured there.
#>

$ErrorActionPreference = 'Stop'

if (-not $Target.StartsWith('AgentStudio/TaskServer/', [StringComparison]::Ordinal)) {
    throw "Target must be under 'AgentStudio/TaskServer/' so the connector's Connector:CredentialTarget accepts it."
}

if (-not ('AgentStudio.CredentialWriter' -as [type])) {
    Add-Type -TypeDefinition @'
using System;
using System.ComponentModel;
using System.Runtime.InteropServices;

namespace AgentStudio
{
    public static class CredentialWriter
    {
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct NativeCredential
        {
            public uint Flags;
            public uint Type;
            public string TargetName;
            public string Comment;
            public System.Runtime.InteropServices.ComTypes.FILETIME LastWritten;
            public uint CredentialBlobSize;
            public IntPtr CredentialBlob;
            public uint Persist;
            public uint AttributeCount;
            public IntPtr Attributes;
            public string TargetAlias;
            public string UserName;
        }

        [DllImport("advapi32.dll", EntryPoint = "CredWriteW", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CredWrite(ref NativeCredential credential, uint flags);

        public static void Write(string target, IntPtr secretUnicode, int secretChars)
        {
            var credential = new NativeCredential
            {
                Type = 1, // CRED_TYPE_GENERIC
                TargetName = target,
                Comment = "Agent Studio loopback connector Task Server credential",
                CredentialBlobSize = (uint)(secretChars * 2),
                CredentialBlob = secretUnicode,
                Persist = 2, // CRED_PERSIST_LOCAL_MACHINE: this user, this machine, survives logoff
                UserName = "studio-connector",
            };
            if (!CredWrite(ref credential, 0))
                throw new Win32Exception(Marshal.GetLastWin32Error());
        }
    }
}
'@
}

if ($FromFile) {
    $raw = (Get-Content -LiteralPath $FromFile -Raw).Trim()
    # A rotation or creation response is JSON with a one-time "credential" field.
    if ($raw.StartsWith('{')) { $raw = ($raw | ConvertFrom-Json).credential }
    if ([string]::IsNullOrWhiteSpace($raw)) { throw "No credential found in $FromFile." }
    $secret = ConvertTo-SecureString -String $raw -AsPlainText -Force
    Remove-Variable raw
}
else {
    $secret = Read-Host -Prompt "Studio principal credential for $Target" -AsSecureString
}
if ($secret.Length -eq 0) { throw 'The credential is empty.' }

if (-not $PSCmdlet.ShouldProcess($Target, 'Store the Studio connector credential in Windows Credential Manager')) {
    return
}

$pointer = [Runtime.InteropServices.Marshal]::SecureStringToGlobalAllocUnicode($secret)
try {
    [AgentStudio.CredentialWriter]::Write($Target, $pointer, $secret.Length)
}
finally {
    [Runtime.InteropServices.Marshal]::ZeroFreeGlobalAllocUnicode($pointer)
    $secret.Dispose()
}

if ($FromFile -and $RemoveSourceFile) {
    Remove-Item -LiteralPath $FromFile -Force
}

Write-Host "Stored the Studio connector credential in Windows Credential Manager target '$Target'."
Write-Host 'Check http://[::1]:5031/readyz: it reports status "ready" once the Task Server accepts the new credential.'
