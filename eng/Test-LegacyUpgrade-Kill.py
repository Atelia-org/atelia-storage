#!/usr/bin/env python3
"""SIGKILL offline upgrade at fixed probes, using copies of old-binary fixtures only.
Process interruption evidence, never power-loss evidence. Source fixtures stay unchanged.
"""
import argparse, hashlib, json, os, pathlib, select, shutil, signal, subprocess
REPO = pathlib.Path(__file__).resolve().parent.parent
BINARY = REPO / 'eng/LegacyUpgradeValidation/bin/Release/net10.0/Atelia.EventJournal.LegacyUpgradeValidation.dll'
TOOLKIT = REPO / 'tools/EventJournal.Toolkit/bin/Release/net10.0/Atelia.EventJournal.Toolkit.dll'
PHASES = ['CopyChunkWritten', 'LocatorWritten', 'CatalogWritten', 'FormatWritten', 'TargetAudited', 'BeforeManifestPublish', 'ManifestPublished']

def sha(path):
    h = hashlib.sha256()
    with path.open('rb') as stream:
        while chunk := stream.read(131072): h.update(chunk)
    return h.hexdigest()

def inventory(root):
    directories = sorted(['.'] + [p.relative_to(root).as_posix() for p in root.rglob('*') if p.is_dir()])
    files = {p.relative_to(root).as_posix(): {'length': p.stat().st_size, 'sha256': sha(p)} for p in sorted(root.rglob('*')) if p.is_file()}
    return {'directories': directories, 'files': files}

def materialize(kind, destination):
    fixtures = REPO / 'tests/EventJournal.Toolkit.Tests/LegacyFixtures'
    provenance = json.loads((fixtures / 'provenance.json').read_text())
    # Git cannot preserve empty roots: restore the recorded directory inventory first.
    for relative in provenance['fixtureDirectories'][kind]:
        p = pathlib.PurePosixPath(relative)
        if p.is_absolute() or '..' in p.parts: raise RuntimeError('Unsafe fixed fixture path')
        (destination / p).mkdir(parents=True, exist_ok=True)
    shutil.copytree(fixtures / kind / 'journal', destination / 'journal', dirs_exist_ok=True)
    return destination / 'journal'

def verify_manifest(bundle, source):
    manifest = json.loads((bundle / 'manifest.json').read_text())
    assert manifest['completed'] is True and manifest['factsCopiedByteExactly'] is True
    assert manifest['kind'] == 'EventJournalLegacyUpgrade'
    assert manifest['profileBaselineRevision'] == 'bb7c4fb3eb6477783c70ee61bc62b832be195d07'
    assert all(v == 'Passed' for v in manifest['validation'].values())
    target = bundle / 'journal'
    expected = {}
    for entry in manifest['outputs']:
        relative = pathlib.PurePosixPath(entry['relativePath'])
        if relative.is_absolute() or '..' in relative.parts: raise RuntimeError('Unsafe manifest output path')
        assert entry['relativePath'] not in expected
        expected[entry['relativePath']] = {'length': entry['length'], 'sha256': entry['sha256']}
    target_inventory = inventory(target)
    source_inventory = inventory(source)
    assert target_inventory['files'] == expected
    assert target_inventory['directories'] == manifest['targetDirectories']
    assert source_inventory['directories'] == manifest['sourceDirectories']
    source_files = source_inventory['files']
    assert {e['relativePath']: {'length': e['length'], 'sha256': e['sha256']} for e in manifest['sourceFiles']} == source_files
    for entry in manifest['sourceFiles']:
        if entry['kind'] != 'ExcludedDerived': assert expected[entry['relativePath']] == source_files[entry['relativePath']]
    assert not (target / 'cache').exists()
    run = subprocess.run(['dotnet', str(TOOLKIT), 'audit', str(target)], capture_output=True, text=True)
    assert run.returncode == 0, 'Post-release target audit failed'
    audit = json.loads(run.stdout)
    assert audit['completed'] and audit['factsStatus'] == 'Healthy' and audit['indexesStatus'] == 'Consistent'
    return {'manifest': 'CompleteAndHashesMatched', 'targetFullAudit': 'Passed', 'terminalCreatedReceived': False}

def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--output', required=True, help='new parent for all temporary source copies and bundles')
    parser.add_argument('--fixture', choices=['empty', 'events-only', 'complex', 'empty-active'], default='complex')
    args = parser.parse_args()
    if os.name != 'posix': raise SystemExit('Windows SIGKILL evidence is PendingPlatform')
    root = pathlib.Path(args.output).resolve()
    if root.exists(): raise SystemExit('Output must be new')
    root.mkdir(parents=True)
    with (root / 'results.jsonl').open('w') as results:
        for phase in PHASES:
            case = root / phase; case.mkdir()
            source = materialize(args.fixture, case / 'fixture')
            output = case / 'bundle'
            before = inventory(source)
            env = os.environ.copy(); env['LEGACY_KILL_PHASE'] = phase
            child = subprocess.Popen(['dotnet', str(BINARY), 'kill-child', str(source), str(output)], stdout=subprocess.PIPE, stderr=subprocess.PIPE, text=True, env=env)
            try:
                if not select.select([child.stdout], [], [], 60)[0]: raise RuntimeError('Kill-child readiness timeout')
                line = child.stdout.readline()
                event = json.loads(line)
                if event.get('status') != 'ReadyToKill' or event.get('phase') != phase: raise RuntimeError('Required kill probe was not reached')
                os.kill(child.pid, signal.SIGKILL); child.wait(timeout=15)
            finally:
                if child.poll() is None: child.kill(); child.wait(timeout=15)
            if child.returncode != -signal.SIGKILL: raise RuntimeError('Child did not exit by SIGKILL')
            if inventory(source) != before: raise RuntimeError('Killed upgrade changed source files/directories')
            present = (output / 'manifest.json').exists()
            if present != (phase == 'ManifestPublished'): raise RuntimeError('Wrong completion release visibility')
            verified = verify_manifest(output, source) if present else {'manifest': 'Absent', 'bundle': 'IncompleteAndNotEligibleForCutover'}
            record = {'phase': phase, 'mechanism': 'actual SIGKILL after existing internal probe', 'powerLossProof': False,
                'exitCode': child.returncode, 'sourceBytesAndDirectoriesUnchanged': True, 'verification': verified}
            results.write(json.dumps(record) + '\n'); results.flush(); print(json.dumps(record), flush=True)
        results.write(json.dumps({'platform': 'Windows', 'status': 'PendingPlatform', 'scope': 'create-only/rename and process interruption'}) + '\n')
if __name__ == '__main__': main()
