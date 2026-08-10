# Third-party notices

## Skua

This repository is based on and remains connected as a GitHub fork of:

- `auqw/Skua`

The upstream Git history and contributor attribution are retained.

At the time this Linux release candidate was prepared, the upstream repository did not expose a root software license file in its repository view. This project therefore does not apply a new blanket license to upstream Skua code.

## Aquastar

The Electron/Ruffle client originated from Aquastar. Its original MIT license is preserved at:

- `Skua.Linux.Client/LICENSE`

The license copyright notice names `aquasp` (2025).

## Ruffle

Ruffle is consumed as the npm dependency `@ruffle-rs/ruffle` rather than vendored as source code in this repository.

Ruffle is offered under either the Apache License 2.0 or MIT License, at the user's option. See the upstream Ruffle project for its complete license and third-party notices.

## Electron, .NET and npm dependencies

Electron, the .NET runtime and other npm/NuGet dependencies retain their respective upstream licenses. Generated `node_modules`, .NET build output and packaged AppImage payloads are not committed to this source repository.
