"""Check the packages `build/pack.ps1` (or `build/pack.sh`) produced, without NuGet.

    python tools/scripts/test/check_nupkg.py [packages-dir] [version]

`packages-dir` defaults to ``temp/packages``.  The version is read from
``src/Directory.Build.props`` -- the one place it is defined -- and a version given on the
command line is checked against it as well.

What is checked is the contract a consumer sees, not the build: the six files, their names,
what each one holds entry by entry, the licence files, the dependency groups per target
framework with their ranges, and that no package carries anything extra (no build/ or tools/
directory, which is why the packages are plain libraries).

Anything that reads this contract has to be changed together with it: the .nuspecs in build/,
README.md's library section and docs/340-nuget-packages.md.

The nupkg is a zip, so this needs nothing but the standard library.
"""

from __future__ import annotations

import sys
import zipfile
import xml.etree.ElementTree as ET
from pathlib import Path

ROOT = Path(__file__).resolve().parents[3]
TYPE = "{http://schemas.microsoft.com/packaging/2013/05/nuspec.xsd}"

# id -> the assembly it ships, the extra files beyond LICENSE/NOTICE and the dependency groups.
# (The assembly paths are per target framework: build/pack.ps1 packs the three projects once.)
CONTRACT = {
    "Bluefire.Indra.Ocr.Sharp.Core": {
        "assembly": "Indra.Ocr.Core",
        "extra": ["THIRD-PARTY-LICENSES/MODIFIED-OPENRAIL-M.txt"],
        "dependencies": {
            "net10.0": {
                "Microsoft.ML.OnnxRuntime": "[1.31.0, 1.32.0)",
            },
            "netstandard2.0": {
                "Microsoft.ML.OnnxRuntime": "[1.31.0, 1.32.0)",
                "System.Memory": "4.5.5",
                "System.Runtime.CompilerServices.Unsafe": "6.0.0",
                "System.Text.Json": "8.0.5",
            },
        },
    },
    "Bluefire.Indra.Ocr.Sharp.Pdf": {
        "assembly": "Indra.Ocr.Pdf",
        "extra": [],
        "dependencies": {
            "net10.0": {
                "Bluefire.Indra.Ocr.Sharp.Core": "[{version}]",
                "bblanchon.PDFium.Linux": "156.0.8076",
                "bblanchon.PDFium.Win32": "156.0.8076",
                "bblanchon.PDFium.macOS": "156.0.8076",
            },
            "netstandard2.0": {
                "Bluefire.Indra.Ocr.Sharp.Core": "[{version}]",
                "bblanchon.PDFium.Linux": "156.0.8076",
                "bblanchon.PDFium.Win32": "156.0.8076",
                "bblanchon.PDFium.macOS": "156.0.8076",
                "System.Memory": "4.5.5",
                "System.Runtime.CompilerServices.Unsafe": "6.0.0",
            },
        },
    },
    "Bluefire.Indra.Ocr.Sharp": {
        "assembly": "Indra.Ocr",
        "extra": [],
        "dependencies": {
            "net10.0": {
                "Bluefire.Indra.Ocr.Sharp.Core": "[{version}]",
                "SixLabors.ImageSharp": "[2.1.13, 3.0.0)",
            },
            "netstandard2.0": {
                "Bluefire.Indra.Ocr.Sharp.Core": "[{version}]",
                "SixLabors.ImageSharp": "[2.1.13, 3.0.0)",
                "System.Memory": "4.5.5",
                "System.Runtime.CompilerServices.Unsafe": "6.0.0",
            },
        },
    },
}

TARGET_FRAMEWORKS = ["net10.0", "netstandard2.0"]

# NuGet's own entries in every package; the directory is a prefix, the rest are exact.
BOOKKEEPING_PREFIX = "package/services/metadata/core-properties/"
BOOKKEEPING = ["[Content_Types].xml", "_rels/.rels"]

# A package that carried any of these would be doing more than shipping assemblies.
FORBIDDEN = ("build/", "buildTransitive/", "tools/", "content/", "contentFiles/", "scripts/",
             "analyzers/", "lib/net4", "lib/netstandard1")

FAILURES: list[str] = []
CHECKS = 0


def check(what: str, ok: bool, detail: str = "") -> None:
    global CHECKS
    CHECKS += 1
    if ok:
        print(f"  ok   {what}")
        return
    print(f"  FAIL {what}" + (f": {detail}" if detail else ""))
    FAILURES.append(what)


def contract_version() -> str:
    """The version the packages carry: ``<Version>-<PackageFlavor>`` from the one place it lives."""
    props = ET.parse(ROOT / "src" / "Directory.Build.props").getroot()
    version = props.findtext("PropertyGroup/Version", "").strip()
    flavor = props.findtext("PropertyGroup/PackageFlavor", "").strip()
    return f"{version}-{flavor}" if flavor else version


def entries(path: Path) -> list[str]:
    with zipfile.ZipFile(path) as zf:
        return sorted(name for name in zf.namelist() if not name.endswith("/"))


def packed_nuspec(path: Path) -> ET.Element:
    with zipfile.ZipFile(path) as zf:
        name = next(n for n in zf.namelist() if n.endswith(".nuspec"))
        return ET.fromstring(zf.read(name))


def read_entry(path: Path, name: str) -> bytes:
    with zipfile.ZipFile(path) as zf:
        return zf.read(name)


def expected_entries(assembly: str, extra: list[str]) -> list[str]:
    files = [f"lib/{tfm}/{assembly}.dll" for tfm in TARGET_FRAMEWORKS]
    return sorted(files + extra + ["LICENSE", "NOTICE"])


def symbols_entries(assembly: str) -> list[str]:
    return sorted(f"lib/{tfm}/{assembly}.pdb" for tfm in TARGET_FRAMEWORKS)


def check_metadata(nupkg: Path, package_id: str, assembly: str, version: str,
                   dependencies: dict[str, dict[str, str]], extra: list[str]) -> None:
    root = packed_nuspec(nupkg)
    metadata = root.find(f"{TYPE}metadata")
    check(f"{package_id}: the nuspec has metadata", metadata is not None)
    if metadata is None:
        return

    check(f"{package_id}: the id is the package id",
          metadata.findtext(f"{TYPE}id") == package_id, metadata.findtext(f"{TYPE}id") or "?")
    check(f"{package_id}: the version is the one the props define",
          metadata.findtext(f"{TYPE}version") == version, metadata.findtext(f"{TYPE}version") or "?")

    license_el = metadata.find(f"{TYPE}license")
    check(f"{package_id}: the licence is the SPDX expression",
          license_el is not None and license_el.get("type") == "expression"
          and (license_el.text or "").strip() == "Apache-2.0",
          ET.tostring(license_el).decode() if license_el is not None else "no <license>")
    check(f"{package_id}: the licence text is still in the package",
          "LICENSE" in expected_entries(assembly, extra))

    check(f"{package_id}: the project url is the port's",
          metadata.findtext(f"{TYPE}projectUrl") == "https://github.com/cnbluefire/indra-ocr-sharp",
          metadata.findtext(f"{TYPE}projectUrl") or "?")
    repository = metadata.find(f"{TYPE}repository")
    check(f"{package_id}: the repository url is the port's",
          repository is not None and repository.get("url") == "https://github.com/cnbluefire/indra-ocr-sharp.git",
          repository.get("url") if repository is not None else "no <repository>")

    description = (metadata.findtext(f"{TYPE}description") or "").strip()
    check(f"{package_id}: the description says what it is", len(description) > 100, f"{len(description)} chars")
    check(f"{package_id}: the tags are there", "ocr" in (metadata.findtext(f"{TYPE}tags") or ""),
          metadata.findtext(f"{TYPE}tags") or "?")
    check(f"{package_id}: the licence needs no acceptance",
          (metadata.findtext(f"{TYPE}requireLicenseAcceptance") or "").lower() == "false")

    groups = metadata.find(f"{TYPE}dependencies")
    found = {}
    if groups is not None:
        for group in groups.findall(f"{TYPE}group"):
            found[normalize_tfm(group.get("targetFramework") or "")] = {
                d.get("id"): d.get("version") for d in group.findall(f"{TYPE}dependency")
            }
    wanted = {normalize_tfm(tfm): {i: v.format(version=version) for i, v in deps.items()}
              for tfm, deps in dependencies.items()}
    check(f"{package_id}: the dependency groups are the contract's",
          found == wanted, f"{found} != {wanted}")


def normalize_tfm(tfm: str) -> str:
    """The package writes .NETStandard2.0 where the project said netstandard2.0."""
    return tfm.lstrip(".").lower()


def check_package(packages: Path, package_id: str, version: str) -> None:
    assembly = CONTRACT[package_id]["assembly"]
    extra = CONTRACT[package_id]["extra"]
    dependencies = CONTRACT[package_id]["dependencies"]

    print(f"\n== {package_id} {version} ==")
    nupkg = packages / f"{package_id}.{version}.nupkg"
    snupkg = packages / f"{package_id}.{version}.snupkg"
    check(f"{package_id}: the .nupkg is there", nupkg.is_file(), nupkg.name)
    check(f"{package_id}: the .snupkg is there", snupkg.is_file(), snupkg.name)
    if not nupkg.is_file() or not snupkg.is_file():
        return

    wanted = expected_entries(assembly, extra) + BOOKKEEPING + [f"{package_id}.nuspec"]
    have = entries(nupkg)
    # NuGet writes one core-properties file whose name it picks; it is not part of the contract.
    have_contract = [n for n in have if not n.startswith(BOOKKEEPING_PREFIX)]
    check(f"{package_id}: the .nupkg holds exactly the contract's entries",
          sorted(have_contract) == sorted(wanted),
          f"missing {sorted(set(wanted) - set(have_contract))} extra {sorted(set(have_contract) - set(wanted))}")
    check(f"{package_id}: no .pdb in the .nupkg", not [n for n in have if n.endswith(".pdb")])
    check(f"{package_id}: NuGet's bookkeeping is there",
          any(n.startswith(BOOKKEEPING_PREFIX) for n in have)
          and all(n in have for n in BOOKKEEPING))

    symbols = [n for n in entries(snupkg) if not n.startswith(BOOKKEEPING_PREFIX)]
    check(f"{package_id}: the .snupkg holds the two debug files",
          sorted(symbols) == sorted(symbols_entries(assembly) + BOOKKEEPING + [f"{package_id}.nuspec"]),
          ", ".join(entries(snupkg)))

    for name in FORBIDDEN:
        offenders = [n for n in entries(nupkg) if n.startswith(name)]
        check(f"{package_id}: nothing under {name}", not offenders, ", ".join(offenders))

    # The right assembly in the right package: it is a PE file whose metadata names it, and the
    # two target frameworks are not the same file.
    for tfm in TARGET_FRAMEWORKS:
        data = read_entry(nupkg, f"lib/{tfm}/{assembly}.dll")
        check(f"{package_id}: lib/{tfm}/{assembly}.dll is a managed assembly",
              data[:2] == b"MZ" and assembly.encode("ascii") in data,
              f"{len(data)} bytes")

    net10 = read_entry(nupkg, f"lib/net10.0/{assembly}.dll")
    ns20 = read_entry(nupkg, f"lib/netstandard2.0/{assembly}.dll")
    check(f"{package_id}: the two target frameworks are different builds", net10 != ns20,
          f"{len(net10)} vs {len(ns20)} bytes")

    check_metadata(nupkg, package_id, assembly, version, dependencies, extra)


def main() -> int:
    packages = Path(sys.argv[1]) if len(sys.argv) > 1 else ROOT / "temp" / "packages"
    expected = contract_version()
    given = sys.argv[2] if len(sys.argv) > 2 else expected

    print(f"check-nupkg: {packages}")
    check("the version comes from src/Directory.Build.props", expected == given,
          f"{expected} != {given}")
    check("nothing but the six files is in the package folder",
          sorted(p.name for p in packages.glob("*.*")) == sorted(
              f"{package_id}.{expected}{suffix}"
              for package_id in CONTRACT
              for suffix in (".nupkg", ".snupkg")),
          ", ".join(sorted(p.name for p in packages.glob("*.*"))))

    for package_id in CONTRACT:
        check_package(packages, package_id, expected)

    print()
    if FAILURES:
        print(f"check-nupkg: FAILED ({len(FAILURES)} of {CHECKS} checks)")
        for failure in FAILURES:
            print(f"  - {failure}")
        return 1

    print(f"check-nupkg: OK ({CHECKS} checks)")
    return 0


if __name__ == "__main__":
    sys.exit(main())
