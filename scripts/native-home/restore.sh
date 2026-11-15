#!/bin/sh
# Restores the saved Home/startup values. Runs on the TV as uid=901, piped into
# bash by run.py, which requires the same saved baseline apply used.
printf 'LK_RUN_BEGIN\n'
/usr/bin/dotnet /home/owner/share/tmp/sdk_tools/LKHomeControl.dll restore /home/owner/share/tmp/sdk_tools/LKHomeOriginal.json
printf 'LK_RUN_END\n'
