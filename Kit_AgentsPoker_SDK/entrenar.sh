#!/usr/bin/env bash
# Laboratorio de entrenamiento:  bash entrenar.sh  (30 s por prueba)  |  bash entrenar.sh 300
cd "$(dirname "$0")" && exec dotnet run -c Release --project Entrenamiento -- "$@"
