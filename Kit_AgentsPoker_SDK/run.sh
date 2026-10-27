#!/usr/bin/env bash
# AgentsPoker workbench:  ./run.sh list | ./run.sh match --agent MiAgente --builtin 'Heuristic*3' --verbose
cd "$(dirname "$0")" && exec dotnet run -c Release --project tools/AgentsPoker.Sim -- "$@"
