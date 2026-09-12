$path = "$env:LOCALAPPDATA\LanNotify\ui-state.json"
$state = @{
    TestMessage   = "Agent 任务已完成"
    AddressMasked = $false
    DarkMode      = $false
} | ConvertTo-Json
Set-Content -Path $path -Value $state -Encoding UTF8
"written:"
Get-Content $path -Raw
