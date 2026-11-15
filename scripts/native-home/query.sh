#!/bin/sh
# Read-only: prints the current Home/startup values from the LKHomeQuery
# assembly. Runs on the TV as uid=901, piped into bash by run.py.
printf 'LK_RUN_BEGIN\n'
/usr/bin/dotnet /home/owner/share/tmp/sdk_tools/LKHomeQuery.dll
printf 'LK_RUN_END\n'
