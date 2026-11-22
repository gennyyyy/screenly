# Phase 0 Findings — Identity and Topology

Manual verification on real hardware. No behavior change in this commit.

## snapshot-diff mapping
EnumerateParsecDevices() before/after ADD; confirm index → device-name mapping and capture isolation.

Result: _fill in_

## identity stability
Stable identity across create/remove cycles; mapping not invalidated.

Result: _fill in_

## mode change (known + novel)
ChangeDisplaySettingsEx on single display; known preset and novel mode.

Result: _fill in_

## topology auto-extend
Windows extends desktop automatically; real monitors untouched.

Result: _fill in_

## capture during removal
Capture behavior while display is being removed.

Result: _fill in_

## ping during removal
Ping behavior during removal (hang timing).

Result: _fill in_

## REMOVE hang timing
Measured REMOVE ioctl duration.

Result: _fill in_
