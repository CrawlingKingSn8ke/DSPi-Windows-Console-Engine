$ErrorActionPreference='Stop'
$program=[Environment]::GetFolderPath('ProgramFiles')
$stage=Join-Path $program ('DSPi-SetupRuntime-'+$OperationId)
$sha=[Security.Cryptography.SHA256]::Create()
function Get-Hash([byte[]]$bytes) {
 [BitConverter]::ToString($sha.ComputeHash($bytes)).Replace('-','')
}
function Quote([string]$value) { "'" + $value.Replace("'","''") + "'" }
if($OperationId -cnotmatch '^[a-f0-9]{32}$' -or $SessionId -cnotmatch '^[a-f0-9]{32}$'){throw 'Invalid setup identifier.'}
if(Test-Path -LiteralPath $stage){throw 'The temporary runtime location already exists.'}
$runtimeStarted=$false
$failureCode=25
try{
 $adminFile=Join-Path $Payload 'Admin.ps1'
 $admin=[IO.File]::ReadAllBytes($adminFile)
 if((Get-Hash $admin) -cne $AdminHash){throw 'The setup helper changed before launch.'}
 $acl=New-Object Security.AccessControl.DirectorySecurity
 $acl.SetSecurityDescriptorSddlForm('O:BAG:BAD:P(A;OICI;FA;;;SY)(A;OICI;FA;;;BA)')
 [IO.Directory]::CreateDirectory($stage,$acl)|Out-Null
 $failureCode=20
 $archive=Join-Path $stage 'PowerShell.zip'
 [IO.File]::Copy($ArchivePath,$archive,$false)
 $file=[IO.File]::OpenRead($archive)
 try{$verified=[BitConverter]::ToString($sha.ComputeHash($file)).Replace('-','')}finally{$file.Dispose()}
 $failureCode=21
 if($verified -cne 'CE7484804C0F0AD4DDA10C958E71ACDAF7EEBFB22505C1B480F89E0E9F1DA94D'){throw 'The bundled Microsoft runtime failed its integrity check.'}
 $failureCode=20
 Add-Type -AssemblyName System.IO.Compression.FileSystem
 $runtime=Join-Path $stage 'runtime'
 [IO.Compression.ZipFile]::ExtractToDirectory($archive,$runtime)
 $hostExe=Join-Path $runtime 'pwsh.exe'
 $failureCode=22
 if((Get-AuthenticodeSignature -LiteralPath $hostExe).Status -ne 'Valid'){throw 'The bundled Microsoft runtime signature could not be verified.'}
 $lines=@(foreach($name in @('Operation','OperationId','SessionId','EndpointGuid','Payload','ExpectedUser','ModuleHash','WorkerHash','SharedHash','ApoHash')){
  '$'+$name+'='+(Quote (Get-Variable -Name $name -ValueOnly))
 })
 $lines+='$RestoreProtection='+(Quote ([string]$RestoreProtection))
 $lines+='$EnableAudioTesting='+(Quote ([string]$EnableAudioTesting))
 $lines+='$BootstrapHost='+(Quote $hostExe)
 # Keep CreateProcess arguments short on every Windows account and install path.
 $adminPath=Join-Path $stage 'Admin.ps1'
 [IO.File]::WriteAllBytes($adminPath,$admin)
 $lines+='. ([ScriptBlock]::Create([IO.File]::ReadAllText('+(Quote $adminPath)+').TrimStart([char]0xfeff)))'
 $encoded=[Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes($lines -join [Environment]::NewLine))
 $failureCode=23
 $runner=Start-Process -FilePath $hostExe -ArgumentList ('-NoLogo -NoProfile -NonInteractive -EncodedCommand '+$encoded) -WindowStyle Hidden -PassThru
 $runtimeStarted=$true
 $failureCode=24
 if(!$runner.WaitForExit(110000)){throw 'The bundled setup helper is still running.'}
 $runtimeStarted=$false
 if($runner.ExitCode){exit $runner.ExitCode}
}catch{
 [Console]::Error.WriteLine($_.Exception.Message)
 exit $failureCode
}finally{
 $sha.Dispose()
 if(!$runtimeStarted -and (Test-Path -LiteralPath $stage)){
  $actual=(Get-Item -LiteralPath $stage)
  if($actual.FullName -ine $stage -or $actual.Parent.FullName -ine $program -or ($actual.Attributes -band [IO.FileAttributes]::ReparsePoint)){
   throw 'The temporary runtime path changed.'
  }
  $task=Get-ScheduledTask -TaskName ('DSPi-EndpointSetup-'+$OperationId) -ErrorAction SilentlyContinue
  if(!$task){Remove-Item -LiteralPath $actual.FullName -Recurse -Force -ErrorAction SilentlyContinue}
 }
}

