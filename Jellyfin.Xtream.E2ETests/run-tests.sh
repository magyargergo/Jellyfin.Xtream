#!/bin/bash
set -e

TEST_DLL="/src/Jellyfin.Xtream.E2ETests/bin/Release/net9.0/Jellyfin.Xtream.E2ETests.dll"

# Add filter if specified
if [ -n "$E2E_TEST_FILTER" ]; then
    echo "Running tests with filter: $E2E_TEST_FILTER"
    exec dotnet vstest "$TEST_DLL" \
        --Logger:"trx;LogFileName=results.trx" \
        --Logger:"console;verbosity=normal" \
        --ResultsDirectory:/results \
        --TestCaseFilter:"$E2E_TEST_FILTER"
else
    echo "Running all tests (no filter)"
    exec dotnet vstest "$TEST_DLL" \
        --Logger:"trx;LogFileName=results.trx" \
        --Logger:"console;verbosity=normal" \
        --ResultsDirectory:/results
fi
