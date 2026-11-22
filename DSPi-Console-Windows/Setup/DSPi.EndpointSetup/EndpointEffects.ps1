# GPL-3.0. Plan only the endpoint values required by DSPi.
function Read-DspiEffectValue($key, [string]$name) {
 if(!$key -or $name -notin $key.GetValueNames()){return $null}
 $kind=$key.GetValueKind($name).ToString()
 if($kind -notin @('String','MultiString')){throw "Unsupported type for endpoint effect value $name."}
 [pscustomobject]@{Kind=$kind;Values=@($key.GetValue($name))}
}
function Test-DspiEffectValue($actual,$expected) {
 if($null -eq $actual -or $null -eq $expected){return $null -eq $actual -and $null -eq $expected}
 if($actual.Kind -cne $expected.Kind -or $actual.Values.Count -ne $expected.Values.Count){return $false}
 for($i=0;$i -lt $actual.Values.Count;$i++){if($actual.Values[$i] -cne $expected.Values[$i]){return $false}}
 return $true
}
function New-DspiEffectPlan($key,[string]$bindingHash) {
 $fx='{d04e05a6-594b-4fb6-a80d-01af5eed7d1d},'
 $modeKey='{d3993a3f-99c2-4402-b5ec-a92a0367664b},7'
 $bindingKey='{67fe7ad1-1232-4c16-94ce-83a4f63be981},1'
 $dspi='{e53f75ef-9901-4ec4-a8d1-2fe2606a7431}'
 $default='{C18E2F7E-933D-4965-B7D1-1EEF228D2AF3}'
 $rows=@{}
 foreach($name in ($fx+'0'),($fx+'7'),($fx+'15'),$modeKey,$bindingKey){
  $before=Read-DspiEffectValue $key $name
  $rows[$name]=[pscustomobject]@{Name=$name;Before=$before;After=$before}
 }
 $single=$rows[$fx+'7']
 $combined=$rows[$fx+'15']
 $association=$rows[$fx+'0']
 $modes=$rows[$modeKey]
 $binding=$rows[$bindingKey]
 if($binding.Before){throw 'The output already has a DSPi binding.'}
 foreach($row in @($single,$combined,$modes,$association)){
  if(!$row.Before){continue}
  $kind=if($row -in @($single,$association)){'String'}else{'MultiString'}
  if($row.Before.Kind -ne $kind -or !$row.Before.Values.Count){throw 'The driver effect configuration is not supported.'}
  foreach($value in $row.Before.Values){if($value -notmatch '^\{[a-f0-9]{8}(-[a-f0-9]{4}){3}-[a-f0-9]{12}\}$'){throw 'The driver effect configuration is not supported.'}}
 }
 $names=if($key){$key.GetValueNames()}
 $legacy=($fx+'1') -in $names -or ($fx+'2') -in $names
 $modern=@(5,6,7,13,14,15).Where({($fx+$_) -in $names}).Count -gt 0
 if($legacy -and !$modern){
  throw 'This driver uses a legacy audio effects chain.'
 }
 $binding.After=[pscustomobject]@{Kind='String';Values=@($bindingHash)}
 if(!$association.Before){$association.After=[pscustomobject]@{Kind='String';Values=@('{DFF21CE1-F70F-11D0-B917-00A0C9223196}')}}
 if(!$modes.Before){$modes.After=[pscustomobject]@{Kind='MultiString';Values=@($default)}}
 elseif($default -notin $modes.Before.Values){$modes.After=[pscustomobject]@{Kind='MultiString';Values=@($modes.Before.Values)+@($default)}}
 if($combined.Before -or $single.Before){
  $oem=if($combined.Before){@($combined.Before.Values)}else{@($single.Before.Values)}
  if($dspi -in $oem -or ($single.Before -and $dspi -in $single.Before.Values)){throw 'DSPi already exists in the endpoint effect chain.'}
  $combined.After=[pscustomobject]@{Kind='MultiString';Values=@($oem)+@($dspi)}
  $single.After=$null
 }else{
  $single.After=[pscustomobject]@{Kind='String';Values=@($dspi)}
 }
 # Registry writes that make Windows load the APO go last.
 $association,$binding,$modes,$combined,$single
}
function Set-DspiEffectPlan($key,$plan,[switch]$Undo,[switch]$CheckOnly) {
 foreach($row in $plan){
  if($Undo -and (Test-DspiEffectValue $row.Before $row.After)){continue}
  $actual=Read-DspiEffectValue $key $row.Name
  if((Test-DspiEffectValue $actual $row.Before) -or ($Undo -and (Test-DspiEffectValue $actual $row.After))){continue}
  throw "The endpoint effect configuration changed at $($row.Name)."
 }
 if($CheckOnly){return}
 foreach($row in $plan){
  if(Test-DspiEffectValue $row.Before $row.After){continue}
  $value=if($Undo){$row.Before}else{$row.After}
  if($null -eq $value){$key.DeleteValue($row.Name,$false)}
  elseif($value.Kind -eq 'String'){$key.SetValue($row.Name,[string]$value.Values[0],[Microsoft.Win32.RegistryValueKind]::String)}
  else{$key.SetValue($row.Name,[string[]]$value.Values,[Microsoft.Win32.RegistryValueKind]::MultiString)}
 }
 $key.Flush()
}
