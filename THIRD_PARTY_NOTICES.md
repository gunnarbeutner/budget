# Third-party notices

OpenBudget is licensed under the MIT License (see `LICENSE`). It includes or
loads the third-party components below, each under its own license.

## Shipped with the web app

| Component | Version | License | Copyright |
| --- | --- | --- | --- |
| [Bootstrap](https://getbootstrap.com/) (`wwwroot/css/bootstrap`) | 5.3.3 | [MIT](https://github.com/twbs/bootstrap/blob/main/LICENSE) | Copyright (c) 2011-2024 The Bootstrap Authors |
| [Bootstrap Icons](https://icons.getbootstrap.com/) (`wwwroot/css/bootstrap-icons`) | 1.11.3 | [MIT](https://github.com/twbs/icons/blob/main/LICENSE) | Copyright (c) 2019-2024 The Bootstrap Authors |
| [Chart.js](https://www.chartjs.org/) (loaded from jsDelivr) | 4.5.1 | [MIT](https://github.com/chartjs/Chart.js/blob/master/LICENSE.md) | Copyright (c) 2014-2024 Chart.js Contributors |
| [Google.Protobuf](https://github.com/protocolbuffers/protobuf) | 3.28.3 | [BSD-3-Clause](https://github.com/protocolbuffers/protobuf/blob/main/LICENSE) | Copyright 2008 Google Inc. |
| [PDF.js](https://mozilla.github.io/pdf.js/) (`wwwroot/lib/pdfjs`, pdfjs-dist legacy build; renders statement thumbnails and the viewer) | 4.10.38 | [Apache-2.0](https://github.com/mozilla/pdf.js/blob/master/LICENSE) (copy in `wwwroot/lib/pdfjs/LICENSE`) | Copyright Mozilla Foundation and contributors |
| [PuppeteerSharp](https://github.com/hardkoded/puppeteer-sharp) (drives the browser for web logins) | 25.12.0 | [MIT](https://github.com/hardkoded/puppeteer-sharp/blob/master/LICENSE) | Copyright (c) Darío Kondratiuk and contributors |
| [.NET / ASP.NET Core / Entity Framework Core](https://github.com/dotnet) | 11.0 preview | [MIT](https://github.com/dotnet/runtime/blob/main/LICENSE.TXT) | Copyright (c) .NET Foundation and Contributors |

## Shipped in the Docker image

| Component | Version | License | Copyright |
| --- | --- | --- | --- |
| [Chromium](https://www.chromium.org/) (Debian `chromium` package, started only for web logins) | as packaged by Debian trixie | [BSD-3-Clause](https://source.chromium.org/chromium/chromium/src/+/main:LICENSE) and the licenses of its bundled components (listed in the package's `/usr/share/doc/chromium/copyright` and at `chrome://credits`) | Copyright 2015 The Chromium Authors |
| [Debian GNU/Linux](https://www.debian.org/) base system, `fonts-liberation` (SIL OFL 1.1), `fonts-dejavu-core` (Bitstream Vera / public domain) | trixie | various, see `/usr/share/doc/*/copyright` in the image | the respective authors |

## Shipped with the macOS importer (`exporter/`)

| Component | Version | License | Copyright |
| --- | --- | --- | --- |
| [SQLCipher](https://www.zetetic.net/sqlcipher/) via [sqlcipher.swift](https://github.com/sqlcipher/sqlcipher.swift) | 4.16+ | BSD-3-Clause style ([Community Edition license](https://github.com/sqlcipher/sqlcipher/blob/master/LICENSE.md)) | Copyright (c) 2025, ZETETIC LLC |

SQLCipher's license requires that binary redistributions reproduce its
copyright notice and disclaimer, so include this file with any distributed
build of the importer app.

## File-format definitions

`src/OpenBudget/PortfolioPerformance/pp_client.proto` is a reduced subset of
the `client.proto` schema from
[Portfolio Performance](https://github.com/portfolio-performance/portfolio),
which is licensed under the
[Eclipse Public License 1.0](https://www.eclipse.org/legal/epl-v10.html).
Message and field numbers are kept identical so the file format can be read.
To the extent this file is a derivative work, it is provided under EPL-1.0;
the rest of OpenBudget is under the MIT License.

## Build and test tools (not distributed)

These are used only to build, analyze or test the code and are not part of
any distributed artifact: Grpc.Tools (Apache-2.0), Microsoft.Playwright
(Apache-2.0), xUnit (Apache-2.0), NUnit and NUnit3TestAdapter (MIT),
Microsoft.NET.Test.Sdk (MIT), Roslynator (Apache-2.0), Meziantou.Analyzer
(MIT) and SonarAnalyzer.CSharp (SONAR Source-Available License v1.0).
