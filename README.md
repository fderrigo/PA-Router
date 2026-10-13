# PA Router - experimental beta

Source-available local AI router. Licensed under PolyForm Noncommercial 1.0.0.
Commercial use is not permitted under this license. Third-party components retain their own licenses.

This prerelease is for experimentation and feedback. Semantic memory retrieval quality is
not qualified: relevant memories may be missed and unrelated memories may be returned.
Check recalled information against its source. Passing a build does not establish memory quality.

## Build

Install the .NET SDK version in global.json, then run:

    dotnet restore PA.Router.sln --locked-mode
    dotnet build PA.Router.sln -c Release --no-restore
    dotnet run --project src/PA.Router.Cli -c Release --no-build -- --help

Embedded JSON/YAML/SQL contracts are in resources/contracts. They are build inputs.
Development documentation and history are maintained separately and are not part of this export.

Support: https://github.com/fderrigo/PA-Router/issues
Security: https://github.com/fderrigo/PA-Router/security/advisories/new
Do not post credentials, databases, backups or customer content. No SLA is offered.

The public workflow checks locked restore, Release build and CLI help on Windows and Linux.
It does not execute the private test suites or qualify semantic memory quality.