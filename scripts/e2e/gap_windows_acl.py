"""Check identity-store access under a disposable local standard-user token."""
import secrets
import lib
from gap_windows import winx

@lib.scenario('gap/AGT-02/windows-standard-token-denied')
def run():
    user='gapwinstd'; pwd='Aa1!'+secrets.token_urlsafe(18)
    try:
        ps=r'''$ErrorActionPreference='Stop'
New-LocalUser -Name USERNAME -Password (ConvertTo-SecureString 'PASSWORD' -AsPlainText -Force) -AccountNeverExpires -PasswordNeverExpires | Out-Null
Add-LocalGroupMember -Group Users -Member USERNAME -ErrorAction SilentlyContinue
Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
public class NativeLogon {
 [DllImport("advapi32.dll", CharSet=CharSet.Unicode, SetLastError=true)] public static extern bool LogonUser(string u,string d,string p,int type,int provider,out IntPtr token);
 [DllImport("kernel32.dll")] public static extern bool CloseHandle(IntPtr token);
}
'@
$token=[IntPtr]::Zero
if(-not [NativeLogon]::LogonUser('USERNAME','.','PASSWORD',3,0,[ref]$token)){throw ('LogonUser '+[Runtime.InteropServices.Marshal]::GetLastWin32Error())}
try {
 $i=New-Object Security.Principal.WindowsIdentity($token); $ctx=$i.Impersonate()
 try {
  $who=[Security.Principal.WindowsIdentity]::GetCurrent().Name
  try { [void][IO.File]::ReadAllBytes('C:\ProgramData\BackupAgent\agent-identity.bin'); $file='readable' } catch { $file=$_.Exception.InnerException.GetType().Name }
  try { [void][IO.Directory]::GetFiles('C:\ProgramData\BackupAgent'); $dir='listable' } catch { $dir=$_.Exception.InnerException.GetType().Name }
  "who=$who;file=$file;dir=$dir"
 } finally {$ctx.Undo();$i.Dispose()}
} finally {[void][NativeLogon]::CloseHandle($token)}'''.replace('USERNAME',user).replace('PASSWORD',pwd)
        rc,out=winx(ps); out=out.replace(pwd,'***')
        assert rc==0 and 'who=windows-test\\gapwinstd' in out.lower() and 'file=UnauthorizedAccessException' in out and 'dir=UnauthorizedAccessException' in out,out
        return {'evidence':out.strip()}
    finally:
        winx(f'Remove-LocalUser -Name {user} -ErrorAction SilentlyContinue')

if __name__=='__main__':run()
