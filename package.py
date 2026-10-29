from pathlib import Path
from hashlib import sha256
from zipfile import ZipFile, ZIP_DEFLATED

root = Path(__file__).resolve().parent
out = root / "release"
out.mkdir(exist_ok=True)
archive = out / "Kellys-GATES-0.6.0-draft-r2.zip"
sources = {
    "UrsaMinorThrottle.dll": root / "bin/Release/net472/UrsaMinorThrottle.dll",
    "README.md": root / "README.md",
    "COMPATIBILITY.md": root / "COMPATIBILITY.md",
    "RELEASE-NOTES.md": root / "RELEASE-NOTES.md",
}
if archive.exists():
    raise SystemExit("Draft archive already exists; preserve the original.")
data = {name: path.read_bytes() for name, path in sources.items()}
manifest = "".join(f"{sha256(blob).hexdigest()}  {name}\n" for name, blob in sorted(data.items()))
with ZipFile(archive, "x", ZIP_DEFLATED) as z:
    for name, blob in sorted(data.items()):
        z.writestr(name, blob)
    z.writestr("SHA256SUMS.txt", manifest.encode())
with ZipFile(archive) as z:
    assert z.testzip() is None
    assert set(z.namelist()) == set(data) | {"SHA256SUMS.txt"}
    for name, blob in data.items():
        assert z.read(name) == blob
    assert z.read("SHA256SUMS.txt") == manifest.encode()
print(f"Archive: {archive}\nSHA-256: {sha256(archive.read_bytes()).hexdigest()}\n{manifest}")
