#!/bin/bash
# M1 stress scenarios (short ones). Long soak is run separately.
cd "$(dirname "$0")/.."
EXE=src/WindowCast.Server/bin/Debug/net8.0-windows10.0.22621.0/WindowCast.Server.exe
TP=tools/TestPattern/bin/Debug/net8.0-windows/TestPattern.exe
OUT=/tmp/m1; mkdir -p $OUT
killtp() { taskkill //F //IM TestPattern.exe >/dev/null 2>&1; sleep 1; }

killtp
echo "############ S1: JPEG 8s (working set must stay flat)"
("$TP" --size 1280x720 --pos 100,100 &); sleep 3
"$EXE" spike --window "Test Pattern" --seconds 8 --encoder jpeg --csv $OUT/s1-jpeg.csv 2>&1 | grep -E "^t=|Summary"

echo "############ S2: H264 hw 8s + NAL check"
"$EXE" spike --window "Test Pattern" --seconds 8 --encoder h264 --out $OUT/s2.h264 2>&1 | grep -E "encoder\]|^t=  [48]s|Summary"
python tools/nal-types.py $OUT/s2.h264
killtp

echo "############ S3: resize every 0.5s for 30s (60 resizes)"
("$TP" --size 1280x720 --pos 100,100 --resize-every 0.5 &); sleep 3
"$EXE" spike --window "Test Pattern" --seconds 30 --encoder h264 --csv $OUT/s3-resize.csv 2>&1 | grep -cE "size\]" | sed 's/^/size-change events: /'
grep -E "Summary" -A0 $OUT/s3-resize.csv >/dev/null 2>&1
"$EXE" spike --window "Test Pattern" --seconds 5 --encoder h264 2>&1 | grep -E "Summary"
killtp

echo "############ S4: move every 0.7s for 12s"
("$TP" --size 1280x720 --pos 100,100 --move-every 0.7 &); sleep 3
"$EXE" spike --window "Test Pattern" --seconds 12 --encoder h264 2>&1 | grep -E "^t=|Summary|size\]"
killtp

echo "############ S5: minimize at 3s, restore at 8s"
("$TP" --size 1280x720 --pos 100,100 --minimize-at 5 --restore-at 10 &); sleep 2
"$EXE" spike --window "Test Pattern" --seconds 14 --encoder h264 --keep-going 2>&1 | grep -E "^t=|Summary|size\]|closed"
killtp

echo "############ S6: window closes itself at 5s"
("$TP" --size 1280x720 --pos 100,100 --close-after 8 &); sleep 3
"$EXE" spike --window "Test Pattern" --seconds 20 --encoder h264 2>&1 | grep -E "closed|Summary"
killtp

echo "############ S7: process killed at ~5s"
("$TP" --size 1280x720 --pos 100,100 &); sleep 3
(sleep 5; taskkill //F //IM TestPattern.exe >/dev/null 2>&1) &
"$EXE" spike --window "Test Pattern" --seconds 20 --encoder h264 2>&1 | grep -E "closed|Summary"
killtp

echo "############ S8: six windows at once, h264 hw, 15s"
for i in 0 1 2 3 4 5; do ("$TP" --size 640x400 --pos $((50 + i*300)),$((60 + (i%2)*450)) --title "Test Pattern $i" &); done; sleep 4
"$EXE" spike --window "Test Pattern" --all --seconds 15 --encoder h264 --csv $OUT/s8-six.csv 2>&1 | grep -E "^t=|Summary|encoder\]"
killtp

echo "############ S9: 4K window (3840x2160), h264 hw, 10s"
("$TP" --size 3840x2160 --pos 0,0 &); sleep 4
"$EXE" spike --window "Test Pattern" --seconds 10 --encoder h264 --csv $OUT/s9-4k.csv 2>&1 | grep -E "^t=|Summary|encoder\]|started"
killtp

echo "############ S10: full monitor 0 capture, h264 hw, 8s (test pattern running on it)"
("$TP" --size 1280x720 --pos 100,100 &); sleep 3
"$EXE" spike --monitor 0 --seconds 8 --encoder h264 2>&1 | grep -E "^t=|Summary|encoder\]|started"
killtp
echo "############ done"
