# Lucide icons

KITN vendors Lucide 0.468.0 SVG icons from https://github.com/lucide-icons/lucide.
They are flattened by `tools/build_lucide_icons.py` and embedded in the mod for
native ImGui rendering. Lucide's ISC/Feather MIT license is included in
`KITN/Assets/Icons/LICENSE`, in the DLL resources and in the release output.

# MechJebLib

`KITN/TransferLambert.cs` adapts the Gooding Lambert solver from
`MechJebLib/Lambert/Gooding.cs`. The ejection calculation in
`KITN/TransferMath.cs` adapts `MechJebLib/Functions/SingleImpulseHyperbolicBurn.cs`.
`KITN/RendezvousMath.cs` adapts the two-impulse objective, moving-target velocity
match and synodic search scale from `MechJebLib/Maneuvers/TwoImpulseTransfer.cs`.
Source: https://github.com/MuMech/MechJeb2

Copyright Lamont Granquist, Sebastien Gaggini and the MechJeb contributors.

These source files offer `LicenseRef-PD-hp OR Unlicense OR CC0-1.0 OR 0BSD OR
MIT-0 OR MIT OR LGPL-2.1+`. KITN uses the **MIT** alternative for these files.
No GPL-only MechJeb application code is included.

Changes: game-independent vector types, finite-input validation, bounded
iterations, explicit handling of collinear Lambert endpoints, golden-section
ejection refinement, surface-clearance checks and transfer-grid integration.
Rendezvous uses a bounded multi-revolution search and local refinement in place
of ALGLIB, retains the target's actual inclination, adds a safe stand-off, and
corrects KSA's finite departure burn. Maneuver execution and close RCS guidance
are implemented for KSA.

`KITN/TransferHohmann.cs` adapts `HohmannTransferParameters` from
`MechJebLib/Functions/Astro.cs` under the same MIT alternative. KITN adds
validated scalar inputs, departure-phase seeds, configurable search bounds,
and refinement against actual orbital snapshots and finite burns.

## MIT License

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in
all copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN
THE SOFTWARE.
