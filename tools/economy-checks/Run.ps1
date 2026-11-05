param([string]$UnityEditorData)
$ErrorActionPreference = 'Stop'
$projectRoot = (Resolve-Path (Join-Path $PSScriptRoot '../..')).Path
if (!$UnityEditorData) {
    $versionLine = Select-String -Path (Join-Path $projectRoot 'ProjectSettings/ProjectVersion.txt') -Pattern '^m_EditorVersion: (.+)$'
    $version = $versionLine.Matches[0].Groups[1].Value.Trim()
    $UnityEditorData = Join-Path ${env:ProgramFiles} "Unity/Hub/Editor/$version/Editor/Data"
}
$compiler = Join-Path $UnityEditorData 'DotNetSdkRoslyn/csc.dll'
$runtime = Join-Path $UnityEditorData 'NetCoreRuntime/dotnet.exe'
$mono = Join-Path $UnityEditorData 'MonoBleedingEdge/bin/mono.exe'
$reference = Join-Path $UnityEditorData 'MonoBleedingEdge/lib/mono/4.8-api'
$output = Join-Path ([IO.Path]::GetTempPath()) ('gamsamo-economy-' + [guid]::NewGuid().ToString('N') + '.exe')
$testSave = $output + '.xml'
try {
    $sources = @(Get-ChildItem -LiteralPath (Join-Path $projectRoot 'Assets/_Project/Scripts/Economy') -Filter '*.cs' | ForEach-Object FullName)
    $sources += @(Get-ChildItem -LiteralPath $PSScriptRoot -Filter '*.cs' | ForEach-Object FullName)
    & $runtime $compiler /nologo /noconfig /nostdlib+ /warnaserror+ /target:exe "/out:$output" "/r:$reference/mscorlib.dll" "/r:$reference/System.dll" "/r:$reference/System.Core.dll" "/r:$reference/System.Xml.dll" @sources
    if ($LASTEXITCODE -ne 0) { throw 'Economy check compilation failed.' }
    & $mono $output --exercise $testSave
    if ($LASTEXITCODE -ne 0) { throw 'Economy checks failed.' }
    & $mono $output --resume $testSave
    if ($LASTEXITCODE -ne 0) { throw 'Fresh-process restore checks failed.' }
}
finally {
    if (Test-Path -LiteralPath $output) { Remove-Item -LiteralPath $output }
    if (Test-Path -LiteralPath $testSave) { Remove-Item -LiteralPath $testSave }
}
