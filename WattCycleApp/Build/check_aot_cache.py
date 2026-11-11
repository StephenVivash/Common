"""Exercise the AOT cache target with MSBuild, without Apple SDKs or hardware."""

import json
import os
from pathlib import Path
import subprocess
import tempfile
import xml.etree.ElementTree as ET


target = Path(__file__).with_name("AppleAotCache.targets").resolve()
with tempfile.TemporaryDirectory(prefix="wattcycle-aot-") as directory:
    root = Path(directory)
    library = root / "Chart.dll"
    dependency = root / "Core.dll"
    library.write_bytes(b"unchanged library")
    dependency.write_bytes(b"dependency version one")

    project = ET.Element("Project")
    properties = ET.SubElement(project, "PropertyGroup")
    for key, value in {
        "Configuration": "Release",
        "TargetFramework": "net10.0-maccatalyst",
        "_RunAotCompiler": "true",
    }.items():
        ET.SubElement(properties, key).text = value
    items = ET.SubElement(project, "ItemGroup")
    ET.SubElement(items, "_AssembliesToAOT", Include=str(library))
    ET.SubElement(items, "_AssembliesToAOT", Include=str(dependency))
    ET.SubElement(project, "Import", Project=str(target))
    # Use the SDK target name to exercise BeforeTargets ordering as well.
    ET.SubElement(project, "Target", Name="_GenerateAOTCompileDependencyCache")
    fixture = root / "cache.proj"
    ET.ElementTree(project).write(fixture, encoding="unicode")

    def inputs(*options, succeeds=True):
        result = subprocess.run(
            ["dotnet", "msbuild", str(fixture), "-nologo", "-v:quiet",
             "-t:_GenerateAOTCompileDependencyCache", "-getItem:_AOTCompileCache",
             *options], capture_output=True, text=True, check=False)
        if not succeeds:
            assert result.returncode != 0, "Missing dependency silently accepted"
            return []
        assert result.returncode == 0, result.stdout + result.stderr
        return [item["Identity"] for item in
                json.loads(result.stdout)["Items"]["_AOTCompileCache"]]

    original = inputs()
    assert len(original) == 2, "Assembly set omitted from cache key"
    assert original == inputs(), "Unchanged input set invalidates cache"
    timestamp = dependency.stat()
    dependency.write_bytes(b"dependency version two")
    os.utime(dependency, ns=(timestamp.st_atime_ns, timestamp.st_mtime_ns))
    updated = inputs()
    assert updated != original, "Changed dependency with preserved mtime reused cache"
    assert updated[0] == original[0], "Unchanged library fingerprint changed"
    for option in ("-p:Configuration=Debug", "-p:TargetFramework=net10.0-ios",
                   "-p:_RunAotCompiler=false", "-p:DesignTimeBuild=true"):
        assert inputs(option) == [], f"Cache workaround unexpectedly enabled: {option}"
    dependency.unlink()
    inputs(succeeds=False)

print("PASS: AOT cache input tracking, no-op stability, preserved-mtime dependency "
      "changes, build configuration guards, and missing-input failure")
