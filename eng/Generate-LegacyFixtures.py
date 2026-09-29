#!/usr/bin/env python3
"""Export fixed old sources, compile an independent public-API generator, generate new test fixtures.
Never loads old/new assemblies in one process; never modifies old production sources.
"""
import argparse, hashlib, json, pathlib, subprocess, tarfile
BASELINE = 'bb7c4fb3eb6477783c70ee61bc62b832be195d07'
REPO = pathlib.Path(__file__).resolve().parent.parent

def run(*args, cwd=None):
    subprocess.run(list(map(str, args)), cwd=cwd, check=True)

def digest(path):
    return hashlib.sha256(path.read_bytes()).hexdigest()

def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--work', required=True, help='new isolated export/build directory')
    parser.add_argument('--output', required=True, help='new fixture root')
    args = parser.parse_args()
    work = pathlib.Path(args.work).resolve(); output = pathlib.Path(args.output).resolve()
    if work.exists() or output.exists(): raise SystemExit('work and output must both be new')
    work.mkdir(parents=True); output.mkdir(parents=True)
    archive = work / 'old-source.tar'
    with archive.open('wb') as target:
        subprocess.run(['git', '-C', str(REPO), 'archive', BASELINE], stdout=target, check=True)
    source = work / 'source'; source.mkdir()
    with tarfile.open(archive) as tar: tar.extractall(source, filter='data')
    # Save exact exported source hashes before compiling, and verify unchanged afterward.
    source_hashes = {str(p.relative_to(source)): digest(p) for p in source.rglob('*') if p.is_file()}
    generator = source / 'LegacyFixtureGenerator'; generator.mkdir()
    code = REPO / 'eng/LegacyFixtureGenerator.cs.txt'
    (generator / 'Program.cs').write_bytes(code.read_bytes())
    (generator / 'LegacyFixtureGenerator.csproj').write_text('''<Project Sdk="Microsoft.NET.Sdk">
<PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net10.0</TargetFramework><ImplicitUsings>enable</ImplicitUsings><Nullable>enable</Nullable></PropertyGroup>
<ItemGroup><ProjectReference Include="../src/EventJournal/EventJournal.csproj" /></ItemGroup>
</Project>\n''')
    run('dotnet', 'build', generator / 'LegacyFixtureGenerator.csproj', '-c', 'Release', '-m:1', '-nr:false', cwd=source)
    run('dotnet', generator / 'bin/Release/net10.0/Atelia.LegacyFixtureGenerator.dll', output, cwd=source)
    for relative, expected in source_hashes.items():
        if digest(source / relative) != expected: raise RuntimeError('Old exported source changed: ' + relative)
    sdk = subprocess.check_output(['dotnet', '--version'], cwd=source, text=True).strip()
    provenance = {'schemaVersion': 1, 'sourceRevision': BASELINE, 'sdk': sdk, 'sourceArchiveSha256': digest(archive),
        'generatorVersion': 1, 'generatorSha256': digest(code), 'oldProductionSourceUnchanged': True,
        'assemblyBoundary': 'independent old-only process; new toolkit not loaded', 'fixtures': {}, 'fixtureDirectories': {}}
    for bundle in sorted(output.iterdir()):
        if not bundle.is_dir(): continue
        provenance['fixtureDirectories'][bundle.name] = [p.relative_to(bundle).as_posix() for p in sorted(bundle.rglob('*')) if p.is_dir()]
        provenance['fixtures'][bundle.name] = [{'relativePath': p.relative_to(bundle).as_posix(),
            'length': p.stat().st_size, 'sha256': digest(p)} for p in sorted(bundle.rglob('*')) if p.is_file()]
    (output / 'provenance.json').write_text(json.dumps(provenance, indent=2) + '\n')
    print(json.dumps({'status': 'LegacyFixturesGenerated', 'sourceRevision': BASELINE, 'output': str(output), 'sdk': sdk}))
if __name__ == '__main__': main()
