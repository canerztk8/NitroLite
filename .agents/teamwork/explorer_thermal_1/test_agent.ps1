$dllPath = "c:\Users\caner\Documents\antigravity\eager-shannon\NitroLite\bin\Debug\net10.0-windows\NitroLite.dll"
[System.Reflection.Assembly]::LoadFrom($dllPath) | Out-Null

$client = New-Object NitroLite.Core.AcerAgentClient
$task = $client.EnsureConnectedAsync()
$connected = $task.GetAwaiter().GetResult()
Write-Output "Connected to AcerAgent: $connected"

if ($connected) {
    # Let's inspect SendRawAsync via reflection
    $sendMethod = [NitroLite.Core.AcerAgentClient].GetMethod("SendRawAsync", [System.Reflection.BindingFlags]"NonPublic, Instance")
    if ($sendMethod) {
        $payload = [PSCustomObject]@{ Function = "GET_MONITOR_DATA" }
        $rawTask = $sendMethod.Invoke($client, @([uint32]10, $payload))
        $rawJson = $rawTask.GetAwaiter().GetResult()
        Write-Output "=== RAW GET_MONITOR_DATA JSON ==="
        Write-Output $rawJson
    }
}
$client.Dispose()
