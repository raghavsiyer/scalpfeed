# What moves Gateway's depth-slot count. Each trial asks for 5 symbols, cancels what it was given and
# leaves. The count of granted requests shows the limit at that moment. Market data only.
param([int]$Port = 4001, [int]$SettleSeconds = 45)

$raw = Join-Path $PSScriptRoot 'ibkr_raw.py'
$five = 'depth META; depth GOOG; depth AMZN; depth NFLX; depth IWM; wait 2.5'

function Run-Raw([int]$Client, [string]$Script) {
    $lines = python -I $raw --port $Port --client $Client --quiet-rows --script $Script
    $granted = @($lines | Select-String -Pattern 'depth id=.*rows=').Count
    $silent = @($lines | Select-String -Pattern 'depth id=.*silent').Count
    $refused = @($lines | Select-String -Pattern 'depth id=.*: refused').Count
    "    granted with rows $granted, silent $silent, refused $refused"
}

function Trial([string]$Name, [int]$Client) {
    "$(Get-Date -Format HH:mm:ss) trial: $Name"
    Run-Raw $Client "$five; cancelgranted; wait 0.5"
}

function Setup([string]$Name, [int]$Client, [string]$Script) {
    "$(Get-Date -Format HH:mm:ss) setup: $Name"
    Run-Raw $Client $Script
}

"settling $SettleSeconds s first"
Start-Sleep $SettleSeconds
Trial 'A0 after settling' 101
Trial 'A1 straight after a clean trial' 101
Start-Sleep $SettleSeconds
Trial "A2 after $SettleSeconds s" 101

Setup 'exit holding 3 granted, none refused, no cancels' 102 'depth NVDA; depth TSLA; depth AAPL; wait 2; exit'
Trial 'B1 straight after' 101
Start-Sleep $SettleSeconds
Trial "B2 after $SettleSeconds s" 101

Setup 'exit holding 3 granted + 2 refused, no cancels' 103 'depth NVDA; depth TSLA; depth AAPL; depth AMD; depth MSFT; wait 2; exit'
Trial 'C1 straight after' 101
Start-Sleep $SettleSeconds
Trial "C2 after $SettleSeconds s" 101

Setup 'clean exit: 3 granted + 2 refused, granted cancelled first' 104 'depth NVDA; depth TSLA; depth AAPL; depth AMD; depth MSFT; wait 2; cancelgranted; wait 1'
Trial 'D1 straight after' 101
Start-Sleep $SettleSeconds
Trial "D2 after $SettleSeconds s" 101
