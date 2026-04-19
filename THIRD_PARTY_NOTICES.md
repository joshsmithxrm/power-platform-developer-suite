# Third-Party Notices

The Power Platform Developer Suite (PPDS) is distributed under the MIT License. See [LICENSE](LICENSE) for the full text.

PPDS includes or depends on the third-party software components listed below. Each is distributed under its own license. This document attributes those components in accordance with their license terms.

Generated for the v1.0.0 launch by walking restored NuGet `project.assets.json` graphs for the runtime projects (PPDS.Auth, PPDS.Cli, PPDS.Dataverse, PPDS.Mcp, PPDS.Migration, PPDS.Plugins, PPDS.Query) and `license-checker-rseidelsohn --production` for the VS Code extension. Build-time-only references (`PrivateAssets="all"` — MinVer, Microsoft.SourceLink.GitHub) and test frameworks (xunit, Moq, FakeXrmEasy, FluentAssertions, coverlet, Microsoft.NET.Test.Sdk) are excluded: they ship only with test assemblies, not with distributed packages. `PPDS.Analyzers` is itself a development-dependency analyzer package and is likewise excluded.

PPDS additionally vendors a subset of `microsoft/git-credential-manager` source (MIT, attributed separately in the "Vendored Source" section below); the counts in the table below are package-based and do not include vendored code.

## Summary

| Ecosystem | Unique Packages | License Breakdown |
|-----------|-----------------|-------------------|
| NuGet (.NET runtime) | 97 | MIT: 91; Apache-2.0: 3; MS-PL OR Apache-2.0 (dual): 1; proprietary / Microsoft EULA: 2 |
| NPM (VS Code extension) | 13 | MIT: 11; 0BSD: 2 |

---

## Apache License 2.0

The following components are distributed under the Apache License, Version 2.0. The full license text is reproduced once below, and each component is listed underneath. Copyright (c) .NET Foundation and Contributors.

<details>
<summary>Apache License 2.0 (click to expand)</summary>

```
                                 Apache License
                           Version 2.0, January 2004
                        http://www.apache.org/licenses/

   TERMS AND CONDITIONS FOR USE, REPRODUCTION, AND DISTRIBUTION

   1. Definitions.

      "License" shall mean the terms and conditions for use, reproduction,
      and distribution as defined by Sections 1 through 9 of this document.

      "Licensor" shall mean the copyright owner or entity authorized by
      the copyright owner that is granting the License.

      "Legal Entity" shall mean the union of the acting entity and all
      other entities that control, are controlled by, or are under common
      control with that entity. For the purposes of this definition,
      "control" means (i) the power, direct or indirect, to cause the
      direction or management of such entity, whether by contract or
      otherwise, or (ii) ownership of fifty percent (50%) or more of the
      outstanding shares, or (iii) beneficial ownership of such entity.

      "You" (or "Your") shall mean an individual or Legal Entity
      exercising permissions granted by this License.

      "Source" form shall mean the preferred form for making modifications,
      including but not limited to software source code, documentation
      source, and configuration files.

      "Object" form shall mean any form resulting from mechanical
      transformation or translation of a Source form, including but
      not limited to compiled object code, generated documentation,
      and conversions to other media types.

      "Work" shall mean the work of authorship, whether in Source or
      Object form, made available under the License, as indicated by a
      copyright notice that is included in or attached to the work
      (an example is provided in the Appendix below).

      "Derivative Works" shall mean any work, whether in Source or Object
      form, that is based on (or derived from) the Work and for which the
      editorial revisions, annotations, elaborations, or other modifications
      represent, as a whole, an original work of authorship. For purposes
      of this License, Derivative Works shall not include works that remain
      separable from, or merely link (or bind by name) to the interfaces of,
      the Work and its Derivative Works thereof.

      "Contribution" shall mean any work of authorship, including
      the original version of the Work and any modifications or additions
      to that Work or Derivative Works thereof, that is intentionally
      submitted to Licensor for inclusion in the Work by the copyright owner
      or by an individual or Legal Entity authorized to submit on behalf of
      the copyright owner. For the purposes of this definition, "submitted"
      means any form of electronic, verbal, or written communication sent
      to the Licensor or its representatives, including but not limited to
      communication on electronic mailing lists, source code control systems,
      and issue tracking systems that are managed by, or on behalf of, the
      Licensor for the purpose of tracking or improving the Work, but
      excluding communication that is conspicuously marked or otherwise
      designated in writing by the copyright owner as "not a Contribution."

      "Contributor" shall mean Licensor and any individual or Legal Entity
      on behalf of whom a Contribution has been received by Licensor and
      subsequently incorporated within the Work.

   2. Grant of Copyright License. Subject to the terms and conditions of
      this License, each Contributor hereby grants to You a perpetual,
      worldwide, non-exclusive, no-charge, royalty-free, irrevocable
      copyright license to reproduce, prepare Derivative Works of,
      publicly display, publicly perform, sublicense, and distribute the
      Work and such Derivative Works in Source or Object form.

   3. Grant of Patent License. Subject to the terms and conditions of
      this License, each Contributor hereby grants to You a perpetual,
      worldwide, non-exclusive, no-charge, royalty-free, irrevocable
      (except as stated in this section) patent license to make, have made,
      use, offer to sell, sell, import, and otherwise transfer the Work,
      where such license applies only to those patent claims licensable
      by such Contributor that are necessarily infringed by their
      Contribution(s) alone or by combination of their Contribution(s)
      with the Work to which such Contribution(s) was submitted. If You
      institute patent litigation against any entity (including a
      cross-claim or counterclaim in a lawsuit) alleging that the Work
      or a Contribution incorporated within the Work constitutes direct
      or contributory patent infringement, then any patent licenses
      granted to You under this License for that Work shall terminate
      as of the date such litigation is filed.

   4. Redistribution. You may reproduce and distribute copies of the
      Work or Derivative Works thereof in any medium, with or without
      modifications, and in Source or Object form, provided that You
      meet the following conditions:

      (a) You must give any other recipients of the Work or
          Derivative Works a copy of this License; and

      (b) You must cause any modified files to carry prominent notices
          stating that You changed the files; and

      (c) You must retain, in the Source form of any Derivative Works
          that You distribute, all copyright, patent, trademark, and
          attribution notices from the Source form of the Work,
          excluding those notices that do not pertain to any part of
          the Derivative Works; and

      (d) If the Work includes a "NOTICE" text file as part of its
          distribution, then any Derivative Works that You distribute must
          include a readable copy of the attribution notices contained
          within such NOTICE file, excluding those notices that do not
          pertain to any part of the Derivative Works, in at least one
          of the following places: within a NOTICE text file distributed
          as part of the Derivative Works; within the Source form or
          documentation, if provided along with the Derivative Works; or,
          within a display generated by the Derivative Works, if and
          wherever such third-party notices normally appear. The contents
          of the NOTICE file are for informational purposes only and
          do not modify the License. You may add Your own attribution
          notices within Derivative Works that You distribute, alongside
          or as an addendum to the NOTICE text from the Work, provided
          that such additional attribution notices cannot be construed
          as modifying the License.

      You may add Your own copyright statement to Your modifications and
      may provide additional or different license terms and conditions
      for use, reproduction, or distribution of Your modifications, or
      for any such Derivative Works as a whole, provided Your use,
      reproduction, and distribution of the Work otherwise complies with
      the conditions stated in this License.

   5. Submission of Contributions. Unless You explicitly state otherwise,
      any Contribution intentionally submitted for inclusion in the Work
      by You to the Licensor shall be under the terms and conditions of
      this License, without any additional terms or conditions.
      Notwithstanding the above, nothing herein shall supersede or modify
      the terms of any separate license agreement you may have executed
      with Licensor regarding such Contributions.

   6. Trademarks. This License does not grant permission to use the trade
      names, trademarks, service marks, or product names of the Licensor,
      except as required for reasonable and customary use in describing the
      origin of the Work and reproducing the content of the NOTICE file.

   7. Disclaimer of Warranty. Unless required by applicable law or
      agreed to in writing, Licensor provides the Work (and each
      Contributor provides its Contributions) on an "AS IS" BASIS,
      WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or
      implied, including, without limitation, any warranties or conditions
      of TITLE, NON-INFRINGEMENT, MERCHANTABILITY, or FITNESS FOR A
      PARTICULAR PURPOSE. You are solely responsible for determining the
      appropriateness of using or redistributing the Work and assume any
      risks associated with Your exercise of permissions under this License.

   8. Limitation of Liability. In no event and under no legal theory,
      whether in tort (including negligence), contract, or otherwise,
      unless required by applicable law (such as deliberate and grossly
      negligent acts) or agreed to in writing, shall any Contributor be
      liable to You for damages, including any direct, indirect, special,
      incidental, or consequential damages of any character arising as a
      result of this License or out of the use or inability to use the
      Work (including but not limited to damages for loss of goodwill,
      work stoppage, computer failure or malfunction, or any and all
      other commercial damages or losses), even if such Contributor
      has been advised of the possibility of such damages.

   9. Accepting Warranty or Support. While redistributing the Work or
      Derivative Works thereof, You may choose to offer, and charge a
      fee for, acceptance of support, warranty, indemnity, or other
      liability obligations and/or rights consistent with this License.
      However, in accepting such obligations, You may act only on Your
      own behalf and on Your sole responsibility, not on behalf of any
      other Contributor, and only if You agree to indemnify, defend, and
      hold each Contributor harmless for any liability incurred by, or
      claims asserted against, such Contributor by reason of your
      accepting any such warranty or support.

   END OF TERMS AND CONDITIONS

```

</details>

### NuGet packages distributed under Apache-2.0

- **Microsoft.Extensions.Caching.Abstractions** 3.1.8, 8.0.0, 9.0.11 — https://asp.net/
- **Microsoft.Extensions.Caching.Memory** 3.1.8, 8.0.1, 9.0.11 — https://asp.net/
- **Microsoft.Extensions.Http** 3.1.8 — https://asp.net/

These are the 3.1.x line of `Microsoft.Extensions.Caching.*` and `Microsoft.Extensions.Http`. Transitive 8.x and 9.x versions of the same libraries are MIT-licensed (see below).

---

## MS-PL OR Apache-2.0 (dual-licensed)

### CsvHelper 33.1.0

License: MS-PL OR Apache-2.0 (dual-licensed; recipient may choose either)
Project: https://joshclose.github.io/CsvHelper/

Copyright (c) Josh Close. Licensed under the Microsoft Public License (MS-PL) or Apache License 2.0.

See the Apache-2.0 full text above. The Microsoft Public License (MS-PL) is available at <https://opensource.org/license/ms-pl-html>.

---

## Proprietary / Microsoft EULA

The following runtime dependencies are distributed under proprietary vendor terms rather than an OSI-approved license. They are included under each vendor's published license.

### Microsoft.Data.SqlClient.SNI.runtime 6.0.2

Project: https://github.com/dotnet/SqlClient
License: https://www.nuget.org/packages/Microsoft.Data.SqlClient.SNI.runtime/6.0.2/License

Microsoft Software License Terms for the SqlClient SNI native runtime. Redistributable-code license permitting shipment inside applications built on .NET.

### Microsoft.PowerPlatform.Dataverse.Client 1.2.10

Project: https://github.com/microsoft/PowerPlatform-DataverseServiceClient
License: https://go.microsoft.com/fwlink/?linkid=2108407

Microsoft Software License Terms for the Dataverse ServiceClient. Redistributable as part of end-user applications that use Microsoft Dataverse.

---

## Vendored Source (MIT)

### git-credential-manager

Project: https://github.com/git-ecosystem/git-credential-manager
License: MIT (full text below under "NuGet packages under the MIT License")
Copyright (c) Microsoft Corporation and contributors.

A minimal subset of the `microsoft/git-credential-manager` source for cross-platform
credential storage (Windows Credential Manager, macOS Keychain, Linux libsecret) is
vendored into `src/PPDS.Auth/Internal/CredentialStore/` at commit
`5fa7116896c82164996a609accd1c5ad90fe730a` (tag v2.7.3). Original Microsoft/GitHub
MIT copyright is attributed in each vendored file's header.

---

## NuGet packages under the MIT License

The following NuGet packages are distributed under the MIT License. The canonical MIT license text is:

> Permission is hereby granted, free of charge, to any person obtaining a copy of this software and associated documentation files (the "Software"), to deal in the Software without restriction, including without limitation the rights to use, copy, modify, merge, publish, distribute, sublicense, and/or sell copies of the Software, and to permit persons to whom the Software is furnished to do so, subject to the following conditions: The above copyright notice and this permission notice shall be included in all copies or substantial portions of the Software. THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.

Copyright notices vary per project; consult each project's repository for the specific copyright holder.

| Package | Version(s) | Project |
|---------|------------|---------|
| Azure.Core | 1.50.0, 1.51.1 | https://github.com/Azure/azure-sdk-for-net/blob/Azure.Core_1.50.0/sdk/core/Azure.Core/README.md |
| Azure.Identity | 1.17.1, 1.19.0 | https://github.com/Azure/azure-sdk-for-net/blob/Azure.Identity_1.17.1/sdk/identity/Azure.Identity/README.md |
| DotNetConfig | 1.2.0 | https://github.com/dotnetconfig/dotnet-config |
| MessagePack | 2.5.198 | https://github.com/MessagePack-CSharp/MessagePack-CSharp |
| MessagePack.Annotations | 2.5.198 | https://github.com/MessagePack-CSharp/MessagePack-CSharp |
| Microsoft.Bcl.AsyncInterfaces | 10.0.2, 8.0.0 | https://dot.net/ |
| Microsoft.Bcl.Cryptography | 10.0.5 | https://dot.net/ |
| Microsoft.Build.Tasks.Git | 10.0.201 | https://github.com/dotnet/dotnet |
| Microsoft.Data.SqlClient | 6.1.4 | https://aka.ms/sqlclientproject |
| Microsoft.Extensions.AI.Abstractions | 9.5.0 | https://dot.net/ |
| Microsoft.Extensions.Configuration | 10.0.5 | https://dot.net/ |
| Microsoft.Extensions.Configuration.Abstractions | 10.0.3, 10.0.5 | https://dot.net/ |
| Microsoft.Extensions.Configuration.Binder | 10.0.5 | https://dot.net/ |
| Microsoft.Extensions.Configuration.CommandLine | 10.0.5 | https://dot.net/ |
| Microsoft.Extensions.Configuration.EnvironmentVariables | 10.0.5 | https://dot.net/ |
| Microsoft.Extensions.Configuration.FileExtensions | 10.0.5 | https://dot.net/ |
| Microsoft.Extensions.Configuration.Json | 10.0.5 | https://dot.net/ |
| Microsoft.Extensions.Configuration.UserSecrets | 10.0.5 | https://dot.net/ |
| Microsoft.Extensions.DependencyInjection | 10.0.5 | https://dot.net/ |
| Microsoft.Extensions.DependencyInjection.Abstractions | 10.0.5 | https://dot.net/ |
| Microsoft.Extensions.Diagnostics | 10.0.5 | https://dot.net/ |
| Microsoft.Extensions.Diagnostics.Abstractions | 10.0.3, 10.0.5 | https://dot.net/ |
| Microsoft.Extensions.FileProviders.Abstractions | 10.0.3, 10.0.5 | https://dot.net/ |
| Microsoft.Extensions.FileProviders.Physical | 10.0.5 | https://dot.net/ |
| Microsoft.Extensions.FileSystemGlobbing | 10.0.5 | https://dot.net/ |
| Microsoft.Extensions.Hosting | 10.0.5 | https://dot.net/ |
| Microsoft.Extensions.Hosting.Abstractions | 10.0.3, 10.0.5 | https://dot.net/ |
| Microsoft.Extensions.Logging | 10.0.5 | https://dot.net/ |
| Microsoft.Extensions.Logging.Abstractions | 10.0.5 | https://dot.net/ |
| Microsoft.Extensions.Logging.Configuration | 10.0.5 | https://dot.net/ |
| Microsoft.Extensions.Logging.Console | 10.0.5 | https://dot.net/ |
| Microsoft.Extensions.Logging.Debug | 10.0.5 | https://dot.net/ |
| Microsoft.Extensions.Logging.EventLog | 10.0.5 | https://dot.net/ |
| Microsoft.Extensions.Logging.EventSource | 10.0.5 | https://dot.net/ |
| Microsoft.Extensions.ObjectPool | 8.0.10 | https://asp.net/ |
| Microsoft.Extensions.Options | 10.0.5 | https://dot.net/ |
| Microsoft.Extensions.Options.ConfigurationExtensions | 10.0.5 | https://dot.net/ |
| Microsoft.Extensions.Primitives | 10.0.5 | https://dot.net/ |
| Microsoft.Identity.Client | 4.83.1 | https://go.microsoft.com/fwlink/?linkid=844761 |
| Microsoft.Identity.Client.Extensions.Msal | 4.83.1 | https://go.microsoft.com/fwlink/?linkid=844761 |
| Microsoft.IdentityModel.Abstractions | 8.17.0 | https://github.com/AzureAD/azure-activedirectory-identitymodel-extensions-for-dotnet |
| Microsoft.IdentityModel.JsonWebTokens | 8.17.0 | https://github.com/AzureAD/azure-activedirectory-identitymodel-extensions-for-dotnet |
| Microsoft.IdentityModel.Logging | 8.17.0 | https://github.com/AzureAD/azure-activedirectory-identitymodel-extensions-for-dotnet |
| Microsoft.IdentityModel.Protocols | 7.7.1 | https://github.com/AzureAD/azure-activedirectory-identitymodel-extensions-for-dotnet |
| Microsoft.IdentityModel.Protocols.OpenIdConnect | 7.7.1 | https://github.com/AzureAD/azure-activedirectory-identitymodel-extensions-for-dotnet |
| Microsoft.IdentityModel.Tokens | 8.17.0 | https://github.com/AzureAD/azure-activedirectory-identitymodel-extensions-for-dotnet |
| Microsoft.NET.StringTools | 18.0.2 | http://go.microsoft.com/fwlink/?LinkId=624683 |
| Microsoft.SqlServer.Server | 1.0.0 | https://aka.ms/sqlclientproject |
| Microsoft.SqlServer.TransactSql.ScriptDom | 170.191.0 | https://github.com/microsoft/SqlScriptDOM |
| Microsoft.VisualStudio.Threading.Only | 17.14.15 | https://microsoft.github.io/vs-threading/ |
| Microsoft.VisualStudio.Validation | 17.13.22 | https://github.com/Microsoft/vs-validation |
| Microsoft.Win32.SystemEvents | 6.0.0 | https://dot.net/ |
| ModelContextProtocol | 0.2.0-preview.3 | https://github.com/modelcontextprotocol/csharp-sdk |
| ModelContextProtocol.Core | 0.2.0-preview.3 | https://github.com/modelcontextprotocol/csharp-sdk |
| Nerdbank.MessagePack | 1.0.2 | https://aarnott.github.io/Nerdbank.MessagePack/ |
| Nerdbank.Streams | 2.13.16 | https://github.com/AArnott/Nerdbank.Streams |
| Newtonsoft.Json | 13.0.1, 13.0.3 | https://www.newtonsoft.com/json |
| NStack.Core | 1.1.1 | https://github.com/gui-cs/NStack/ |
| PolyType | 1.0.0 | https://eiriktsarpalis.github.io/PolyType/ |
| RadLine | 0.9.0 | https://github.com/spectreconsole/radline |
| Spectre.Console | 0.54.0 | https://github.com/spectreconsole/spectre.console |
| StreamJsonRpc | 2.24.84 | https://www.nuget.org/packages/StreamJsonRpc |
| System.ClientModel | 1.8.0, 1.9.0 | https://github.com/Azure/azure-sdk-for-net/blob/System.ClientModel_1.8.0/sdk/core/System.ClientModel/README.md |
| System.CodeDom | 9.0.4 | https://dot.net/ |
| System.Collections.Immutable | 9.0.1 | https://dot.net/ |
| System.CommandLine | 2.0.5 | https://github.com/dotnet/command-line-api |
| System.Configuration.ConfigurationManager | 6.0.0, 8.0.1, 9.0.11 | https://dot.net/ |
| System.Diagnostics.DiagnosticSource | 10.0.5 | https://dot.net/ |
| System.Diagnostics.EventLog | 10.0.5, 8.0.1, 9.0.11 | https://dot.net/ |
| System.Drawing.Common | 6.0.0 | https://dot.net/ |
| System.Formats.Asn1 | 10.0.5 | https://dot.net/ |
| System.IdentityModel.Tokens.Jwt | 8.17.0 | https://github.com/AzureAD/azure-activedirectory-identitymodel-extensions-for-dotnet |
| System.IO.Hashing | 10.0.5 | https://dot.net/ |
| System.IO.Pipelines | 10.0.5 | https://dot.net/ |
| System.Management | 9.0.4 | https://dot.net/ |
| System.Memory.Data | 10.0.1, 8.0.1 | https://dot.net/ |
| System.Net.ServerSentEvents | 10.0.0-preview.4.25258.110 | https://dot.net/ |
| System.Reflection.Metadata | 9.0.1 | https://dot.net/ |
| System.Reflection.MetadataLoadContext | 9.0.1 | https://dot.net/ |
| System.Reflection.TypeExtensions | 4.7.0 | https://github.com/dotnet/corefx |
| System.Runtime.Caching | 4.7.0 | https://github.com/dotnet/corefx |
| System.Security.Cryptography.Pkcs | 10.0.5, 8.0.1 | https://dot.net/ |
| System.Security.Cryptography.ProtectedData | 8.0.0, 9.0.11 | https://dot.net/ |
| System.Security.Cryptography.Xml | 8.0.2, 8.0.3 | https://dot.net/ |
| System.Security.Permissions | 6.0.0 | https://dot.net/ |
| System.ServiceModel.Http | 8.1.2 | https://github.com/dotnet/wcf |
| System.ServiceModel.Primitives | 8.1.2 | https://github.com/dotnet/wcf |
| System.Text.Encodings.Web | 10.0.5 | https://dot.net/ |
| System.Text.Json | 10.0.5 | https://dot.net/ |
| System.Windows.Extensions | 6.0.0 | https://dot.net/ |
| Terminal.Gui | 1.19.0 | https://github.com/gui-cs/Terminal.Gui/ |

---

## Node.js / NPM Dependencies (VS Code Extension)

The following NPM packages are production dependencies of the `power-platform-developer-suite` VS Code extension, resolved from `src/PPDS.Extension/package-lock.json` with `license-checker-rseidelsohn --production`. DevDependencies (esbuild, eslint, vitest, `@playwright/test`, `@vscode/vsce`, TypeScript, etc.) are excluded because they do not ship with the VSIX.

| Package | Version | License | Repository |
|---------|---------|---------|------------|
| @microsoft/fast-element | 1.14.0 | MIT | https://github.com/Microsoft/fast |
| @microsoft/fast-foundation | 2.50.0 | MIT | https://github.com/Microsoft/fast |
| @microsoft/fast-react-wrapper | 0.3.25 | MIT | https://github.com/Microsoft/fast |
| @microsoft/fast-web-utilities | 5.4.1 | MIT | https://github.com/Microsoft/fast |
| @types/trusted-types | 1.0.6 | MIT | https://github.com/DefinitelyTyped/DefinitelyTyped |
| @vscode/webview-ui-toolkit | 1.4.0 | MIT | https://github.com/microsoft/vscode-webview-ui-toolkit |
| exenv-es6 | 1.1.1 | MIT | https://github.com/chrisdholt/exenv-es6 |
| monaco-editor | 0.53.0 | MIT | https://github.com/microsoft/monaco-editor |
| react | 19.2.4 | MIT | https://github.com/facebook/react |
| tabbable | 5.3.3 | MIT | https://github.com/focus-trap/tabbable |
| tslib | 1.14.1 | 0BSD | https://github.com/Microsoft/tslib |
| tslib | 2.8.1 | 0BSD | https://github.com/Microsoft/tslib |
| vscode-jsonrpc | 8.2.1 | MIT | https://github.com/Microsoft/vscode-languageserver-node |

`tslib` is distributed under the [BSD Zero Clause License (0BSD)](https://opensource.org/license/0bsd), which is functionally equivalent to public-domain dedication and imposes no attribution requirement. All other NPM production dependencies are MIT-licensed.

---

## Updating this file

Regenerate on dependency changes:

1. `dotnet restore PPDS.sln --source https://api.nuget.org/v3/index.json`
2. `dotnet tool install -g dotnet-project-licenses` (runs on .NET 7; set `DOTNET_ROLL_FORWARD=LatestMajor` on .NET 8+-only machines), then `dotnet-project-licenses --input PPDS.sln --use-project-assets-json --include-transitive --unique --json`. Walk each runtime project's `obj/project.assets.json` to determine which packages are runtime vs build-only — exclude anything referenced from test projects only, and anything with `PrivateAssets="all"` in its csproj.
3. `cd src/PPDS.Extension && npx license-checker-rseidelsohn --production --json`.
4. Flag any new license types beyond MIT / Apache-2.0 / BSD / ISC / 0BSD / MS-PL for legal review before shipping.
