#!/usr/bin/env bash
# Test loop against the Parallels Windows VM, driven from the Mac. No computer use needed:
# prlctl exec runs commands in the logged-in user's session, send-key-event sends real
# scancodes (RegisterHotKey sees them), capture saves the VM screen as PNG.
# Verified 2026-10-17 on Parallels 27.0.3, Windows 11 ARM64.
#
#   tools/vm.sh deploy <path/to/App.exe>   copy exe + test-audio/*.wav into %LOCALAPPDATA%\wsw\ via the ~/Downloads share
#   tools/vm.sh start                      start the deployed exe (detached)
#   tools/vm.sh stop                       kill it
#   tools/vm.sh notepad                    open an empty Notepad and focus it (paste target)
#   tools/vm.sh focus <window title>       bring a window to the front
#   tools/vm.sh type "hello world"         type lowercase letters, digits, space
#   tools/vm.sh combo ctrl alt d           press keys together, release in reverse
#   tools/vm.sh shot [file.png]            screenshot of the VM (default: /tmp/wsw-vm.png)
#   tools/vm.sh run <cmd...>               run a command as the logged-in user
#   tools/vm.sh log                        tail the app log (%LOCALAPPDATA%\wsw\wsw.log)
set -euo pipefail

VM="${WSW_VM:-Windows 11}"
SHARE_DIR="$HOME/Downloads/wsw-test"   # Downloads is shared into the VM as Z:\Downloads
GUEST_DIR='%LOCALAPPDATA%\wsw'
EXE_NAME="${WSW_EXE:-WhisperShortcut.exe}"

run() { prlctl exec "$VM" --current-user cmd /c "$*"; }

# Scancode set 1, decimal.
sc() {
  case "$1" in
    ctrl) echo 29 ;; alt) echo 56 ;; shift) echo 42 ;; win) echo 91 ;;
    enter) echo 28 ;; esc) echo 1 ;; space) echo 57 ;; tab) echo 15 ;; backspace) echo 14 ;;
    a) echo 30 ;; b) echo 48 ;; c) echo 46 ;; d) echo 32 ;; e) echo 18 ;; f) echo 33 ;;
    g) echo 34 ;; h) echo 35 ;; i) echo 23 ;; j) echo 36 ;; k) echo 37 ;; l) echo 38 ;;
    m) echo 50 ;; n) echo 49 ;; o) echo 24 ;; p) echo 25 ;; q) echo 16 ;; r) echo 19 ;;
    s) echo 31 ;; t) echo 20 ;; u) echo 22 ;; v) echo 47 ;; w) echo 17 ;; x) echo 45 ;;
    y) echo 44 ;; z) echo 21 ;;  # German layout: y/z swapped (QWERTZ)
    1) echo 2 ;; 2) echo 3 ;; 3) echo 4 ;; 4) echo 5 ;; 5) echo 6 ;;
    6) echo 7 ;; 7) echo 8 ;; 8) echo 9 ;; 9) echo 10 ;; 0) echo 11 ;;
    *) echo "unknown key: $1" >&2; return 1 ;;
  esac
}
press()   { prlctl send-key-event "$VM" -s "$(sc "$1")" -e press   >/dev/null; }
release() { prlctl send-key-event "$VM" -s "$(sc "$1")" -e release >/dev/null; }

cmd="${1:-}"; shift || true
case "$cmd" in
  deploy)
    mkdir -p "$SHARE_DIR"; cp "$1" "$SHARE_DIR/$EXE_NAME"; cp "$(dirname "$0")"/../test-audio/*.wav "$SHARE_DIR/"
    run "taskkill /im $EXE_NAME /f >nul 2>&1" || true
    sleep 2  # the killed process holds the exe open for a moment
    run "mkdir $GUEST_DIR 2>nul & copy /y Z:\\Downloads\\wsw-test\\* $GUEST_DIR\\ >nul && echo deployed"
    ;;
  start)
    # exec blocks while the GUI process lives, so detach on the Mac side.
    # Absolute path, no cmd /c: going through cmd leaves a console window in the VM.
    lad=$(run "echo %LOCALAPPDATA%" | tr -d '\r')
    nohup prlctl exec "$VM" --current-user "$lad\\wsw\\$EXE_NAME" >/dev/null 2>&1 &
    # First launch of a fresh 120 MB single-file exe takes 10-20 s (Defender scans it).
    for _ in $(seq 1 15); do
      sleep 2
      run "tasklist /fi \"imagename eq $EXE_NAME\" | findstr /i $EXE_NAME" 2>/dev/null && exit 0
    done
    echo "not running after 30 s" >&2; exit 1
    ;;
  stop) run "taskkill /im $EXE_NAME /f" || true ;;
  notepad) nohup prlctl exec "$VM" --current-user notepad.exe >/dev/null 2>&1 & sleep 4; "$0" focus Notepad ;;
  focus)
    # Windows blocks focus stealing for processes started from outside; AppActivate gets around it.
    # -EncodedCommand: prlctl mangles parentheses/quotes in plain arguments. First PowerShell
    # start in a fresh VM takes ~60 s ("Preparing modules for first use"), later ones ~2 s.
    enc=$(printf '%s' "(New-Object -ComObject WScript.Shell).AppActivate('$1')" | iconv -t UTF-16LE | base64)
    prlctl exec "$VM" --current-user powershell -NoProfile -NonInteractive -EncodedCommand "$enc" 2>/dev/null | grep -q True
    ;;
  type)
    s="$1"
    for ((i = 0; i < ${#s}; i++)); do
      ch="${s:i:1}"; [[ "$ch" == " " ]] && ch=space
      press "$ch"; release "$ch"
    done
    ;;
  combo)
    for k in "$@"; do press "$k"; done
    for ((i = $#; i >= 1; i--)); do release "${!i}"; done
    ;;
  shot)
    out="${1:-/tmp/wsw-vm.png}"; prlctl capture "$VM" --file "$out" >/dev/null; echo "$out"
    ;;
  run) run "$@" ;;
  log) run "powershell -NoProfile -Command Get-Content -Tail 40 \$env:LOCALAPPDATA\\wsw\\wsw.log" ;;
  *) sed -n 2,16p "$0"; exit 2 ;;
esac
