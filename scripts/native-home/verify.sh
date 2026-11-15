#!/bin/sh
# Compares the live values against a saved baseline and writes nothing.
# Runs on the TV as uid=901, piped into bash by run.py.
printf 'LK_RUN_BEGIN\n'
/usr/bin/dotnet /home/owner/share/tmp/sdk_tools/LKHomeControl.dll verify /home/owner/share/tmp/sdk_tools/LKHomeOriginal.json
printf 'LK_RUN_END\n'
