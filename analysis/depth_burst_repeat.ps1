# Repeats the depth sequence of `ScalpFeedHarness ibkr` at the protocol level while another client holds
# a slot, as Quantower did. Looks for a run where one cancel doesn't let a waiter in. Market data only.
param([int]$Port = 4001, [int]$Runs = 8, [string]$Holder = 'EVGN')

$raw = Join-Path $PSScriptRoot 'ibkr_raw.py'
$holderOut = Join-Path $env:TEMP 'raw_burst_holder.txt'
$holdSeconds = ($Runs * 15) + 10
$holder = Start-Process python -ArgumentList '-I', $raw, '--port', $Port, '--client', '92', '--quiet-rows', '--script', "`"depth $Holder; wait $holdSeconds; canceldepth $Holder; wait 1`"" -RedirectStandardOutput $holderOut -NoNewWindow -PassThru
Start-Sleep 2

$script = 'quote SPY; quote NVDA; quote AEHL; wait 4; depth SPY; depth NVDA; depth AEHL; wait 1.5; depth LABT; wait 3; canceldepth SPY; wait 0.3; depth AEHL; depth LABT; wait 1; depth AEHL; depth LABT; wait 1.5; cancelgranted; wait 0.5'
for ($i = 1; $i -le $Runs; $i++) {
    $lines = python -I $raw --port $Port --client 131 --quiet-rows --script $script
    $summary = @($lines | Select-String -Pattern '  depth id=' | ForEach-Object { ($_.Line -replace '^\s+depth id=\d+ ', '') -replace ': rows=\d+', ':ok' -replace ': refused', ':309' -replace ': silent.*', ':silent' })
    "{0} run {1}: {2}" -f (Get-Date -Format HH:mm:ss), $i, ($summary -join '  ')
}

$holder | Stop-Process -ErrorAction SilentlyContinue
