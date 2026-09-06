# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Project Overview

OpenVSP-Hydro is a fork of [OpenVSP](http://www.openvsp.org), NASA's open-source parametric aircraft
geometry tool (C++17). OpenVSP lets a user build a 3D parametric model of a vehicle from engineering
parameters and export it for downstream engineering analysis (CFD meshing, VSPAERO aerodynamics, mass
properties, structures, etc.). It ships a GUI app, a headless batch mode, an embedded scripting language
(AngelScript), and a C++ API wrapped for Python/MATLAB via SWIG.

Licensed under the NASA Open Source Agreement (NOSA) v1.3 — see `LICENSE`.

## Build System

The project is CMake-based (CMake >= 3.24) and split into three top-level CMake projects:

- **`Libraries/`** — builds bundled third-party dependencies (AngelScript, Clipper2, CMinpack, Code-Eli,
  CppTest, delabella, Eigen3, exprparse, FLTK, GLEW, GLM, libIGES, Libxml2, OpenABF, Pinocchio, STEPcode,
  Triangle). Produces `VSP_Libraries_Config.cmake`, consumed by the main project via `VSP_LIBRARY_PATH`.
- **`src/`** — the main OpenVSP project (all first-party source plus a few bundled deps in `src/external`).
- **`SuperProject/`** — convenience wrapper that builds `Libraries` then `src` in one shot; the easiest
  entry point for a new developer, but `src/` integrates better with IDEs when developing OpenVSP itself.

### Typical out-of-tree build (Linux/macOS, system libs where available)

```sh
mkdir build buildlibs
cd buildlibs && cmake -DCMAKE_BUILD_TYPE=Release \
    -DVSP_USE_SYSTEM_LIBXML2=true -DVSP_USE_SYSTEM_GLM=true \
    -DVSP_USE_SYSTEM_GLEW=true -DVSP_USE_SYSTEM_CMINPACK=true ../Libraries
make -j$(nproc)
cd ../build && cmake -DCMAKE_BUILD_TYPE=Release -DVSP_LIBRARY_PATH=$(pwd)/../buildlibs ../src
make -j$(nproc)
```

See `README.md` for full dependency/build details (Debian/RPM wiki links, Windows/Visual Studio steps,
macOS steps) and the complete list of `VSP_USE_SYSTEM_XXXX` / `XXXX_INSTALL_DIR` CMake variables. `.github/workflows/build.yml`
is the source of truth for exact per-OS dependency and CMake invocations (it only triggers on the `build`
branch, not on every push/PR).

### Useful CMake configuration flags (main `src` project)

- `VSP_NO_GRAPHICS` — skip FLTK/OpenGL/GLEW/GLM; build only the headless batch/API targets. Useful for HPC.
- `VSP_NO_VSPAERO` — skip building VSPAERO and its utilities.
- `VSP_NO_API_WRAPPERS` — skip SWIG/Python/MATLAB API generation.
- `VSP_NO_HELP` / `VSP_NO_DOC` / `VSP_NO_PYDOC` — skip help files / Doxygen docs / Python docs.
- `VSP_INSTALL_API_TEST` — include the `apitest` executable in the install package.
- `VSP_COPY_API` — stage API libs/headers into `<build>/api` for external C++ consumers (macOS/Linux only).
- `VSP_ENABLE_MATLAB_API` — build the MATLAB API (requires a custom SWIG build; niche).

Primary build targets produced under `src/vsp/`: `vsp` (GUI), `vspscript` (headless AngelScript-driven
batch executable), `apitest` / `apitest_g` (API test-suite executables). `make doc` generates Doxygen API
docs (requires Doxygen found and `VSP_NO_DOC` unset).

## Testing

Tests run via CTest, registered from `src/test/` (included from the bottom of `src/CMakeLists.txt`):

- **`PyTest`** (`src/test/py/`) — pytest suite exercising the Python API (`test_*.py` files under
  `src/test/py/tests/`). Requires the Python API to have been built (SWIG + numpy found).
- **`PySampleCodeTest`** / **`PyDegenGeomTest`** — `unittest`-based sample/DegenGeom API tests.
- **`AreaProj`**, **`TestChordAdjust`**, **`RoutingGeom`** (`src/test/scripttest/`) — run `.vspscript`
  files through the headless `vspscript` executable to exercise the AngelScript API end-to-end.
- **`dba_test`** (`src/test/dba_test/`) — standalone delabella-triangulation smoke-test executable.

Run from the build directory:

```sh
ctest                       # run all registered tests
ctest -R TestChordAdjust    # run a single test by name
ctest --output-on-failure   # show failure output
```

`APITestSuite*.cpp/.h` in `src/vsp/` are compiled into the `apitest`/`apitest_g` executables and run
outside CTest — invoke the built `apitest` binary directly for C++-level API coverage (mass properties,
CFD mesh, parasite drag, VSPAERO).

## Code Style

Formatting follows `vsp.astylerc` (run via [Artistic Style / astyle](http://astyle.sourceforge.net)):
Allman-style braces (`--style=break`), 4-space indents (tabs converted to spaces), padded operators and
parens, one-line blocks/statements kept, Linux line endings. Apply it to changed files with:

```sh
astyle --options=vsp.astylerc <file>
```

Every source file carries the NOSA license header comment block — match it in new files (see any existing
`.h`/`.cpp` for the exact text).

## Architecture

### Layering

```
geom_api / geom_api(_g)  →  public C/C++ API surface (VSP_Geom_API.h), SWIG-wrapped for Python/MATLAB (*.i files)
      ↓
geom_core                →  vehicle/geometry data model, parameter system, managers (no GUI/graphics deps)
      ↓
util, util_api, xmlvsp   →  low-level math (Vec2d/Vec3d/Matrix4d), XML I/O (libxml2 wrapper), misc utilities
      ↓
external                 →  bundled first-party-maintained third-party code compiled directly into src
```

`cfd_mesh` (surface intersection & meshing for CFD/FEA) and `vsp_aero` (VSPAERO solver + utilities, largely
a separate sub-project under `src/vsp_aero`) sit alongside `geom_core` and are driven through `AnalysisMgr`.
`gui_and_draw` + `vsp_graphic` are the FLTK/OpenGL front end and are compiled only when `VSP_NO_GRAPHICS`
is not set; note the `_g`-suffixed target variants (`geom_api_g`, `apitest_g`, `gui_interface_g`) are the
graphics-enabled builds of otherwise headless libraries — see the `VSP_*_LIBRARIES*` sets near the bottom
of `src/CMakeLists.txt` for exactly which libraries compose the GUI vs. headless vs. API-first link lines.

### VSPAERO is a separate executable, not a library call

This trips people up constantly. `VSPAEROMgr` (in `geom_core`) does **not** solve anything — it writes a
`.vspaero` case file, forks the external `vspaero` binary (`m_SolverProcess.ForkCmd(...)`), and parses the
solver's `.history` / `.polar` / `.lod` / `.group` output files back into `ResultsMgr`. It never sees a
panel, a bound vortex, or a matrix. The actual solve lives in `src/vsp_aero/Solver/` (~104k lines;
`VSP_Solver.C` alone is 36k) and is **matrix-free preconditioned GMRES with fast-multipole acceleration** —
there is no assembled AIC matrix anywhere. Anything that needs to touch influence coefficients has to go
into the solver and be expressible in a matrix-free, multipole-compatible form; options are passed to it as
**command-line flags** (see the `args` list in `VSPAEROMgrSingleton::ComputeSolver`), not case-file entries.

**Fork-specific solver modification:** `src/vsp_aero/` is otherwise vendored upstream code, but this fork
adds a free-surface image option to it — `ImagePlaneSign_` in `VSP_Solver.H/.C` (+1 rigid ground plane,
−1 free surface), applied via `ApplyImagePlaneSign()` at the 38 `DoGroundEffectsAnalysis()` reflection
sites, plus a `-freesurface` flag in `vspaero.C`. The default (+1) is a no-op, so ground effect is
bit-for-bit unchanged. Keep this in mind when merging upstream VSPAERO updates.

### Core data model (`src/geom_core`)

- **`Vehicle`** (`Vehicle.h/.cpp`) is the root object: owns the tree of `Geom` objects, global settings,
  undo/redo, and file I/O (native `.vsp3` XML format via `xmlvsp`). Accessed as a singleton through
  **`VehicleMgr`**.
- **`Parm`/`ParmContainer`** (`Parm.h`, `ParmContainer.h`, `ParmMgr.h`) implement the parametric-value
  system: every editable numeric value in the model (dimensions, angles, counts...) is a `Parm` owned by a
  `ParmContainer`, addressable by a stable ID, undoable (`ParmUndo.h`), linkable (`LinkMgr`), and
  drivable by design variables (`DesignVarMgr`) or the advanced-link AngelScript engine (`AdvLinkMgr`).
  This ID-addressable parameter system is *the* mechanism the GUI, API, scripting, and file format all
  build on — changes to geometry almost always go through `Parm`, not raw member fields.
- **`Geom`** (`Geom.h`) is the base class for every geometry type; concrete geometries live in
  `*Geom.h/.cpp` (e.g. `WingGeom`, `FuselageGeom`, `PropGeom`, `BORGeom`, `MeshGeom`, `CustomGeom` for
  user/AngelScript-defined geometry, etc.).
- **Manager singletons** — most cross-cutting subsystems follow a `FooMgr : public ParmContainer` (or
  plain) singleton pattern with a `FooMgr.GetInstance()` accessor, e.g. `AnalysisMgr`, `LinkMgr`,
  `SubSurfaceMgr`, `MaterialMgr`, `StructureMgr`, `VSPAEROMgr`, `ParasiteDragMgr`, `WaveDragMgr`,
  `MeasureMgr`, `VarPresetMgr`, `ModeMgr`, `AttributeManager`, `ResultsMgr`, `ScriptMgr`. When adding a
  new cross-cutting feature, this is the established pattern to follow.
- **`AnalysisMgr`/`Analysis`** (`AnalysisMgr.h`) is the registry/execution framework for named, scriptable
  analyses (CFD mesh, VSPAERO, mass properties, parasite drag, wave drag, ...); each analysis takes named
  inputs and produces named results through `ResultsMgr`, and is exposed uniformly to the GUI, C++ API,
  and AngelScript.

### API surface (`src/geom_api`)

`VSP_Geom_API.h/.cpp` is the single flat C++ API consumed by everything external: the GUI's `GuiInterface`,
the headless `vspscript`/AngelScript bindings, and the SWIG-generated Python/MATLAB modules (`vsp.i`,
`vsp_python.i`, `vsp_g.i`, `matlab_api/`). When extending the API, add the function here and it becomes
available to Python/MATLAB automatically once SWIG regenerates bindings — check `examples/scripts` and
`src/test/py/tests` for the expected call conventions before changing signatures.

### Scripting

Two independent scripting layers exist:
- **AngelScript** (`.vspscript` files, `CustomGeom`/`AdvLinkMgr` "custom" definitions) is embedded in the
  batch `vspscript` executable and in `CustomGeom` for user-defined parametric geometry — see
  `examples/scripts/` and `src/test/scripttest/`, and root-level `TestScript.as` / `TestXSec.fxs`.
- **Python API** (`src/python_api`, generated via SWIG from `geom_api/*.i`) — the primary scripting
  interface for external analysis workflows; see `examples/scripts` and `src/test/py`.

### Directory map

| Path | Purpose |
|---|---|
| `src/geom_core` | Vehicle/geometry data model, Parm system, managers, analyses |
| `src/geom_api` | Flat public API + SWIG interface files (Python/MATLAB bindings) |
| `src/util`, `src/util_api` | Math primitives, file/string utilities |
| `src/xmlvsp` | libxml2-based XML read/write used for `.vsp3` files |
| `src/cfd_mesh` | Surface intersection & CFD/FEA meshing |
| `src/vsp_aero` | VSPAERO solver, viewer, and supporting utilities (near-standalone sub-project) |
| `src/gui_and_draw` | FLTK GUI screens (one `*Screen.cpp/.h` pair per dialog/panel) |
| `src/vsp_graphic` | OpenGL rendering layer used by the GUI |
| `src/vsp` | Executable entry points (`main.cpp`, `apitest_main.cpp`, `scriptonly_main.cpp`) and `APITestSuite*` |
| `src/python_api`, `src/matlab_api` | Generated/packaged language bindings |
| `src/external` | Bundled third-party code built directly into the main project |
| `src/test` | CTest-registered Python, AngelScript, and delabella tests |
| `Libraries` | CMake project building bundled third-party dependencies |
| `SuperProject` | Wrapper CMake project building `Libraries` + `src` together |
| `examples` | Example `.vsp3` models, scripts (`CustomScripts`, `scripts`, `matlab`, `vspaero_ex`), textures, airfoils |

## Versioning / Changelog

Version numbers come from git tags via `src/cmake/VSP_Version.cmake` (`INCLUDE( VSP_Version )` in
`src/CMakeLists.txt`). `CHANGELOG.md` is regenerated from tags with `./changelog.sh` (run from repo root,
requires a full tag history).
