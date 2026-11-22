$ErrorActionPreference='Stop'
. (Join-Path $PSScriptRoot 'EndpointEffects.ps1')
$base='Software\DSPi-EndpointEffects-'+[guid]::NewGuid().ToString('N')
$root=[Microsoft.Win32.Registry]::CurrentUser.CreateSubKey($base)
$fx='{d04e05a6-594b-4fb6-a80d-01af5eed7d1d},'
$single=$fx+'7'
$composite=$fx+'15'
$modes='{d3993a3f-99c2-4402-b5ec-a92a0367664b},7'
$oem='{11111111-2222-3333-4444-555555555555}'
$another='{81111111-2222-3333-4444-555555555555}'
$dspi='{e53f75ef-9901-4ec4-a8d1-2fe2606a7431}'
$mode='{81ab20b6-385d-4a39-b0ce-12ef8d998877}'
$hash='0'*64
function Assert([bool]$condition,[string]$message){if(!$condition){throw $message}}
function RoundTrip($key){
 $plan=@(New-DspiEffectPlan $key $hash) | ConvertTo-Json -Depth 8 -AsArray | ConvertFrom-Json
 Set-DspiEffectPlan $key $plan
 return ,$plan
}
try {
 $new=$root.CreateSubKey('Fresh')
 try{
  $plan=RoundTrip $new
  Assert ($new.GetValue($single) -eq $dspi) 'The fresh output must retain the original single DSPi registration.'
  Set-DspiEffectPlan $new $plan -Undo
  Assert ($new.ValueCount -eq 0) 'Fresh registry key did not roll back.'
 }finally{$new.Dispose()}
 $existing=$root.CreateSubKey('OEM')
 try{
  $existing.SetValue($single,$oem)
  $existing.SetValue($fx+'0','{DFF21CE1-F70F-11D0-B917-00A0C9223196}')
  $existing.SetValue($modes,[string[]]@($mode),[Microsoft.Win32.RegistryValueKind]::MultiString)
  $existing.SetValue('VendorFlag',19)
  $plan=RoundTrip $existing
  Assert ($null -eq $existing.GetValue($single)) 'Single-slot registration must hand off to the composite.'
  Assert (($existing.GetValue($composite) -join ',') -eq ($oem+','+$dspi)) 'OEM must stay ahead of DSPi.'
  Assert ($existing.GetValue('VendorFlag') -eq 19) 'Unrelated vendor values must be preserved.'
  Set-DspiEffectPlan $existing $plan -Undo
  Assert ($existing.GetValue($single) -eq $oem -and $null -eq $existing.GetValue($composite)) 'OEM single effect was not restored.'
  Assert (($existing.GetValue($modes) -join ',') -eq $mode) 'OEM processing modes were not restored.'
  $existing.DeleteValue($single)
  $existing.SetValue($composite,[string[]]@($oem,$another),[Microsoft.Win32.RegistryValueKind]::MultiString)
  $plan=RoundTrip $existing
  Assert (($existing.GetValue($composite) -join ',') -eq ($oem+','+$another+','+$dspi)) 'Original composite ordering must be preserved.'
  Set-DspiEffectPlan $existing $plan -Undo
  Assert (($existing.GetValue($composite) -join ',') -eq ($oem+','+$another)) 'Original composite effects were not restored.'
  $plan=@(New-DspiEffectPlan $existing $hash)
  $existing.SetValue($composite,[string[]]@($another),[Microsoft.Win32.RegistryValueKind]::MultiString)
  $blocked=$false
  try { Set-DspiEffectPlan $existing $plan } catch { $blocked=$true }
  Assert ($blocked -and ($existing.GetValue($composite) -join ',') -eq $another) 'A driver change must fail without writes.'
  $existing.DeleteValue($composite)
  $existing.SetValue($fx+'1',$oem)
  $blocked=$false
  try { $null=New-DspiEffectPlan $existing $hash } catch { $blocked=$true }
  Assert $blocked 'Legacy-only effects must remain protected.'
  $existing.SetValue($fx+'6',$another)
  $plan=RoundTrip $existing
  Assert ($existing.GetValue($single) -eq $dspi -and $existing.GetValue($fx+'6') -eq $another) 'An OEM mode effect must be preserved.'
  Set-DspiEffectPlan $existing $plan -Undo
  Assert ($null -eq $existing.GetValue($single) -and $existing.GetValue($fx+'6') -eq $another) 'An OEM mode-effect endpoint did not roll back.'
 }finally{$existing.Dispose()}
 'PASS fresh, OEM single and composite endpoint effects, rollback and conflict checks.'
}finally{
 $root.Dispose()
 [Microsoft.Win32.Registry]::CurrentUser.DeleteSubKeyTree($base,$false)
}
