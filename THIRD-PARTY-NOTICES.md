# Third-Party Notices

NuGet packages are not listed here: `deploy/build-package.ps1` lists them in the zip's `THIRD-PARTY-NOTICES.md` from the SBOM of
the shipped projects, and only the sections below (components outside NuGet) are copied into it.

* `PellyCodeMcpServer.exe` (MCP server for OpenCode, in the air-gap zip beside `pelly.exe`) adds these NuGet packages to the ones
  `pelly.exe` uses, as of `ModelContextProtocol` 2.4.0 and `Microsoft.Extensions.Hosting` 10.0.14:
  * `ModelContextProtocol`, `ModelContextProtocol.Core` 2.4.0: Apache-2.0.
  * `Microsoft.Extensions.Hosting` 10.0.14 and what it brings (`Microsoft.Extensions.Hosting.Abstractions`, `Configuration.CommandLine`,
    `Configuration.EnvironmentVariables`, `Configuration.UserSecrets`, `Diagnostics`, `Diagnostics.Abstractions`, `Logging`,
    `Logging.Abstractions`, `Logging.Configuration`, `Logging.Console`, `Logging.Debug`, `Logging.EventLog`, `Logging.EventSource`,
    `Options`, `Options.ConfigurationExtensions`, `Caching.Abstractions`, all `Microsoft.Extensions.*` 10.0.14; `System.Diagnostics.EventLog`
    10.0.14) and `Microsoft.Extensions.AI.Abstractions` 10.11.0 (from the MCP SDK): MIT.
  * All are within the license gate's allowed list (MIT, Apache-2.0, BSD-2-Clause, BSD-3-Clause); no new license exception.

## Qwen tokenizer files

* Files: `src/PellyCode.Core/Llm/Resources/vocab.json`, `merges.txt`, and `tokenizer-meta.json` (special tokens and pre-tokenizer regex extracted from `tokenizer.json`).
* Source: Hugging Face repository `Qwen/Qwen3.6-35B-A3B`.
* License: Apache License 2.0 (https://www.apache.org/licenses/LICENSE-2.0).
* SHA-256 of the source files:
  * `tokenizer.json`: `5f9e4d4901a92b997e463c1f46055088b6cca5ca61a6522d1b9f64c4bb81cb42`
  * `vocab.json`: `ce99b4cb2983d118806ce0a8b777a35b093e2000a503ebde25853284c9dfa003`
  * `merges.txt`: `a9d356d7bdf1ef4949e3e748e95b8e10ad9d4e2e838eddc38a0a7b6b94d1db8d`
