# Third-party notices

Space Maid's original code and project-authored assets are licensed under the MIT license in `LICENSE`. This grant does not replace the licenses of dependencies, externally supplied data, tools, media or model weights. Their notices and terms remain applicable.

## Bundled software

The following versions and package license declarations were checked against the restored NuGet manifests. Versions describe the current dependency set; rebuilding with changed dependencies requires updating these notices.

| Component | Version | License | Copyright / attribution |
| --- | --- | --- | --- |
| Velopack (update runtime, native installer and updater) | 1.2.161 | MIT | Velopack Ltd. All rights reserved.; authors Velopack Ltd, Caelan Sayler and Kevin Bost. |
| Microsoft.Data.Sqlite and Microsoft.Data.Sqlite.Core | 10.0.14 | MIT | Microsoft Corporation. All rights reserved. |
| Microsoft.Extensions.AI.Abstractions | 10.11.0 | MIT | Microsoft Corporation. All rights reserved. |
| Microsoft.Bcl.AsyncInterfaces, Microsoft.Bcl.Memory, Microsoft.Extensions.DependencyInjection.Abstractions, Microsoft.Extensions.Logging.Abstractions and System.Numerics.Tensors | 10.0.14 | MIT | Microsoft Corporation. All rights reserved. |
| CommunityToolkit.HighPerformance | 8.4.2 | MIT | .NET Foundation and Contributors. All rights reserved. |
| System.Interactive.Async and System.Linq.Async | 7.0.1 | MIT | .NET Foundation and Contributors. |
| LLamaSharp | 0.30.0 | MIT | Copyright (c) 2025 SciSharp STACK; package copyright also identifies SciSharp STACK 2026. |
| LLamaSharp.Backend.Cpu (llama.cpp native backend) | 0.30.0 | MIT | Copyright 2023 The llama.cpp Authors. All rights reserved. |
| SQLitePCLRaw.bundle_e_sqlite3, SQLitePCLRaw.config.e_sqlite3, SQLitePCLRaw.core and SQLitePCLRaw.provider.e_sqlite3 | 3.0.5 | Apache-2.0 | Copyright 2014-2026 SourceGear, LLC; author Eric Sink. |
| SQLite (native e_sqlite3 library) | 3.54.1 | Public domain | SQLite authors; package copyright identifies Copyright 2014-2026 SourceGear, LLC. The package's LICENSE.txt states that SQLite is public domain. |

Original projects and license references:

- [Velopack 1.2.161](https://github.com/velopack/velopack/blob/1.2.161/LICENSE).
- Microsoft packages: [dotnet](https://github.com/dotnet/dotnet/blob/main/LICENSE.txt), [AI abstractions](https://github.com/dotnet/extensions/blob/59e1741f6fb45cf7ae08e2df088aef71537a7bc4/LICENSE.TXT).
- [CommunityToolkit](https://github.com/CommunityToolkit/dotnet/blob/35dfe7b12da0e64384bfaec7ebc0883196a89f9f/LICENSE.md), [Reactive / Interactive](https://github.com/dotnet/reactive/blob/b1b81fbfe09298655b4dae5cfe0c2269e7323f4e/LICENSE).
- [LLamaSharp 0.30.0 source license](https://github.com/SciSharp/LLamaSharp/blob/d03b6cbca3c66357e2ed9ee9d0324f5eaeabfd4f/LICENSE), [llama.cpp](https://github.com/ggml-org/llama.cpp/blob/master/LICENSE).
- [SQLitePCLRaw](https://github.com/ericsink/SQLitePCL.raw), [SQLite public-domain dedication](https://sqlite.org/copyright.html).

The self-contained Windows model worker also includes the Microsoft .NET runtime. Its original notices accompany the worker as `Extensions/space-maid.assistant/Worker/Runtime-LICENSE.TXT` and `Extensions/space-maid.assistant/Worker/Runtime-THIRD-PARTY-NOTICES.TXT`. Retain those files when redistributing the worker. The standalone installer embeds its resolved .NET runtime notices, available through its **Licenses** link. The main WPF application uses the separately installed .NET Desktop Runtime.

### MIT terms for the components identified above

The copyright and attribution notices in the table apply to their respective components. The following MIT permission and disclaimer text accompanies those notices:

```text
Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.
```

## File-format registry: separate data terms

`src/SpaceMaid.Core/Analysis/Data/FileFormatRegistry.json.gz` and `FileFormatRegistry.provenance.json.gz` are projections of externally supplied research and are **excluded from Space Maid's MIT grant**. The collection has mixed upstream provenance and no blanket MIT or CC0 license. Source URLs, restrictions and association evidence are retained in the provenance sidecar; input hashes and projection limitations are documented in that directory's `README.md`.

The supplied research identifies ArchiveTeam (CC0), dexvert (Unlicense with exceptions), PRONOM JSON (MIT), Tika (Apache-2.0), Linguist and mime-db (MIT), libmagic (BSD), Shared MIME-info (GPL-2.0-or-later), EDAM (CC-BY-SA-4.0), and specialist sources with per-record or unknown documentation terms. These are the research report's attributions, not a new license grant or a claim that every upstream term has been satisfied. The original `sources` and source-record tables were not supplied. The provenance sidecar is not a complete upstream license bundle, and some reuse permissions remain unresolved.

Published packages retain the sidecar and its accompanying documentation under `Notices/FileFormatRegistry`. Consult the recorded sources and establish applicable permissions before redistributing the registry; adding this project's MIT license does not resolve those data permissions. The authored classifier and registry-generation code remain MIT licensed.

## External tools and user-provided content

ExifTool is discovered from a user's existing installation and is not bundled. It retains [ExifTool's own terms](https://exiftool.org/#license). Git, cloud sync applications and other installed tools retain their own licenses. User-provided media and local model weights are not bundled or relicensed by Space Maid; their owners' terms apply independently.

Test and build packages are development dependencies and are not included in the application package. Their own NuGet license declarations apply when redistributing those tools.

## Apache License 2.0

The following unmodified Apache License 2.0 text applies to the SQLitePCLRaw packages identified above.

```text


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
      represent, as a whole, an original work of authorship. For the purposes
      of this License, Derivative Works shall not include works that remain
      separable from, or merely link (or bind by name) to the interfaces of,
      the Work and Derivative Works thereof.

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
      Licensor for the purpose of discussing and improving the Work, but
      excluding communication that is conspicuously marked or otherwise
      designated in writing by the copyright owner as "Not a Contribution."

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

   9. Accepting Warranty or Additional Liability. While redistributing
      the Work or Derivative Works thereof, You may choose to offer,
      and charge a fee for, acceptance of support, warranty, indemnity,
      or other liability obligations and/or rights consistent with this
      License. However, in accepting such obligations, You may act only
      on Your own behalf and on Your sole responsibility, not on behalf
      of any other Contributor, and only if You agree to indemnify,
      defend, and hold each Contributor harmless for any liability
      incurred by, or claims asserted against, such Contributor by reason
      of your accepting any such warranty or additional liability.

   END OF TERMS AND CONDITIONS

   APPENDIX: How to apply the Apache License to your work.

      To apply the Apache License to your work, attach the following
      boilerplate notice, with the fields enclosed by brackets "[]"
      replaced with your own identifying information. (Don't include
      the brackets!)  The text should be enclosed in the appropriate
      comment syntax for the file format. We also recommend that a
      file or class name and description of purpose be included on the
      same "printed page" as the copyright notice for easier
      identification within third-party archives.

   Copyright [yyyy] [name of copyright owner]

   Licensed under the Apache License, Version 2.0 (the "License");
   you may not use this file except in compliance with the License.
   You may obtain a copy of the License at

       http://www.apache.org/licenses/LICENSE-2.0

   Unless required by applicable law or agreed to in writing, software
   distributed under the License is distributed on an "AS IS" BASIS,
   WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
   See the License for the specific language governing permissions and
   limitations under the License.

```
