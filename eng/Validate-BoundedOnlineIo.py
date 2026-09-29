#!/usr/bin/env python3
"""Opt-in Linux evidence runner: creates only a new output directory; no repair or live data.
Raw strace buffers are pointers, never payload. Cold = new journal, not OS cache drop.
"""
import argparse, hashlib, json, os, pathlib, re, signal, subprocess, time, shutil
REPO = pathlib.Path(__file__).resolve().parent.parent
DLL = REPO / 'tests/EventJournal.Validation/bin/Release/net10.0/Atelia.EventJournal.Validation.dll'
def call(*args):
    run = subprocess.run(['dotnet', str(DLL), *map(str,args)], capture_output=True, text=True)
    if run.returncode: raise RuntimeError(run.stdout + run.stderr)
    return [json.loads(s) for s in run.stdout.splitlines() if s.startswith('{')]
def digest(root):
    sha = hashlib.sha256(); count=0; size=0
    for p in sorted(root.rglob('*')):
        if p.is_file():
            sha.update(str(p.relative_to(root)).encode()); count+=1
            with p.open('rb') as f:
                while chunk:=f.read(1<<20): sha.update(chunk); size+=len(chunk)
    return {'sha256':sha.hexdigest(),'files':count,'bytes':size}
def trace_summary(path, root):
    # .NET Linux threads share the descriptor table. -f keeps their syscalls.
    # open/close successful results update live ownership; raw read uses hexadecimal fd/count/result.
    fds={}; reads=0; requested=0; returned=0; enumerations=0; opens=0; closes=0; maxfds=0; stats=0
    is_dataset=lambda value: value == str(root) or value.startswith(str(root)+'/')
    failures=0; interruptions=0; metadata_reads=0; metadata_requested=0; metadata_returned=0
    by_class={}
    def read_class(owner):
        relative=os.path.relpath(owner,root)
        if relative=='journal.format': return 'journalFormat'
        if relative.endswith('catalog.snapshot'): return 'catalogSnapshot'
        if relative.endswith('active.segment'): return 'locators'
        if relative.endswith('ref-op-log.rbf'): return 'refOpLog'
        if relative.startswith('events/') and relative.endswith('.rbf'): return 'eventSegments'
        if relative.startswith('refs/objects/') and relative.endswith('.rbf'): return 'refSegments'
        return 'other'
    pending={}; lines=[]; owners={}
    def owner_at_start(line):
        call=re.search(r'(read|pread64|getdents64)\((0x[0-9a-f]+|\d+),',line)
        return owners.get(int(call[2],0),'') if call else ''
    def track_ownership(line):
        opening=re.search(r'openat\(.*\)\s+=\s+(\d+)<([^>]+)>',line)
        closing=re.search(r'close\((\d+)<([^>]+)>\)\s+=\s+0',line)
        if opening: owners[int(opening[1])]=opening[2]
        elif closing: owners.pop(int(closing[1]),None)
    for rawline in path.read_text().splitlines():
        tid, _, line = rawline.partition(' ')
        line=line.lstrip()
        if '<unfinished ...>' in line:
            prefix=line.split('<unfinished ...>')[0]
            pending[tid]=(prefix,owner_at_start(prefix)); continue
        if 'resumed>' in line:
            if tid not in pending: raise RuntimeError('strace resumed without matching start')
            prefix,owner=pending.pop(tid)
            line=prefix+line.split('resumed>',1)[1]; lines.append((line,owner)); track_ownership(line); continue
        lines.append((line,owner_at_start(line))); track_ownership(line)
    if pending: raise RuntimeError('Incomplete strace records; cannot claim exact syscall evidence')
    for line,owner in lines:
        op=re.search(r'openat\(.*\)\s+=\s+(\d+)<([^>]+)>',line)
        if op:
            fd=int(op[1]); target=op[2]
            fds[fd]=target
            if is_dataset(target):
                opens+=1; maxfds=max(maxfds,sum(is_dataset(v) for v in fds.values()))
            continue
        close=re.search(r'close\((\d+)<([^>]+)>\)\s+=\s+0',line)
        if close:
            if is_dataset(close[2]): closes+=1
            fds.pop(int(close[1]),None); continue
        raw_start=re.search(r'(read|pread64|getdents64)\((0x[0-9a-f]+|\d+),',line)
        if raw_start and is_dataset(owner):
            raw=re.search(r'(read|pread64|getdents64)\((0x[0-9a-f]+|\d+),\s*0x[0-9a-f]+,\s*(0x[0-9a-f]+|\d+).*\)\s+=\s+(0x[0-9a-f]+|\d+|-1|\?)',line)
            if not raw: raise RuntimeError('Unclassified syscall for known dataset fd: '+line)
            attempted=int(raw[3],0)
            value=0
            if raw[4]=='?' or 'ERESTART' in line: interruptions+=1
            elif raw[4]=='-1': failures+=1
            else:
                value=int(raw[4],0)
                if value > (1<<63)-1: raise RuntimeError('Unclassified negative raw syscall result: '+line)
            if raw[1]=='getdents64': enumerations+=1
            else:
                reads+=1; requested+=attempted; returned+=value
                entry=by_class.setdefault(read_class(owner),{'calls':0,'requestedBytes':0,'returnedBytes':0})
                entry['calls']+=1; entry['requestedBytes']+=attempted; entry['returnedBytes']+=value
                if owner.endswith('/validation.fixture.json'):
                    metadata_reads+=1; metadata_requested+=attempted; metadata_returned+=value
        if 'newfstatat(' in line or 'statx(' in line:
            name=re.search(r'"([^"\n]*)"',line)
            directory=re.search(r'(?:newfstatat|statx)\([^<,]*<([^>]+)>',line)
            if name:
                target=name[1] if name[1].startswith('/') else os.path.normpath(os.path.join(directory[1],name[1])) if directory else ''
                if is_dataset(target): stats+=1
    return {'datasetReadsByClass':by_class,'datasetReadSyscalls':reads,'datasetRequestedBytes':requested,'datasetReturnedBytes':returned,
        'datasetFailedSyscalls':failures,'datasetInterruptedSyscalls':interruptions,
        'fixtureMetadataReadSyscalls':metadata_reads,'fixtureMetadataRequestedBytes':metadata_requested,'fixtureMetadataReturnedBytes':metadata_returned,
        'datasetGetdentsSyscalls':enumerations,'datasetOpenSyscalls':opens,'datasetCloseSyscalls':closes,
        'maxDatasetFdsFromTrace':maxfds,'datasetStatSyscalls':stats,'scope':'aggregate of all operation repetitions; external fixture sidecar/fixture construction/hash scans excluded'}
def measure(root, mode, repetitions, out, strace):
    before=digest(root)
    result=call('measure',root,mode,repetitions)[0]
    trace=out / f'{root.name}-{mode}.strace'
    run=subprocess.run([strace,'-f','-qq','-yy','-o',str(trace),'-e','trace=openat,close,read,pread64,getdents64,statx,newfstatat','-e','raw=read,pread64,getdents64',
        'dotnet',str(DLL),'measure',str(root),mode,str(repetitions)],capture_output=True,text=True)
    if run.returncode: raise RuntimeError(run.stdout+run.stderr)
    traced=json.loads(run.stdout.strip())
    result.update(trace_summary(trace,root)); result['timingScope']='separate untraced process; syscall counts from matching traced process'
    result['tracedRepetitions']=traced['repetitions']; result['before']=before; result['after']=digest(root)
    if result['before'] != result['after']: raise RuntimeError('Read-only measurement changed source bytes')
    return result
def kill_case(root,phase):
    p=subprocess.Popen(['dotnet',str(DLL),'kill-child',str(root),phase],stdout=subprocess.PIPE,stderr=subprocess.PIPE,text=True)
    # A separate timeout thread is unnecessary: select bounds readiness on POSIX.
    import select
    ready=select.select([p.stdout],[],[],60)[0]
    if not ready:
        p.kill(); p.wait(); raise RuntimeError('kill-child readiness timeout')
    line=p.stdout.readline(); event=json.loads(line)
    if event.get('status')!='ReadyToKill': raise RuntimeError(line)
    os.kill(p.pid, signal.SIGKILL); p.wait(timeout=15)
    before=digest(root); verification=call('verify',root,phase)[0]; after=digest(root)
    expected_reject=phase in ['rotation:NextFlush','rotation:LocatorCreate','rotation:LocatorFlush','rotation:LocatorReplace']
    if (verification['status']=='StrictReopenRejected') != expected_reject: raise RuntimeError(str(verification))
    if expected_reject and verification['code']!='NextSegmentPresent': raise RuntimeError(str(verification))
    if phase.startswith('rotation:') and not expected_reject:
        expected_active=2 if phase in ['rotation:LocatorPublished','rotation:OldDispose'] else 1
        if verification.get('active') != expected_active: raise RuntimeError('Killed locator active mismatch')
    if phase.startswith('Event'):
        expected_tail=96 if phase=='EventBeforeAppend' else 188
        if verification.get('eventTail') != expected_tail: raise RuntimeError('Killed append tail mismatch')
        expected_sequence=1 if phase=='EventBeforeAppend' else 2
        if verification.get('tailSequence') != expected_sequence or verification.get('nextSequence') != expected_sequence+1: raise RuntimeError('Killed append sequence mismatch')
    if phase.startswith('tag:'):
        expected_present=phase in ['tag:AfterAppend','tag:AfterDurableFlush']
        if verification.get('savedTagPresent') != expected_present: raise RuntimeError('Killed tag visibility mismatch')
    if before!=after: raise RuntimeError('Strict verification modified killed fixture')
    return {'mechanism':'SIGKILL after internal phase readiness; not power-loss proof','phase':phase,'exitCode':p.returncode,'verification':verification,'before':before,'after':after}
def main():
    parser=argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--output',required=True); parser.add_argument('--strace',default='strace')
    parser.add_argument('--scales',default='1000,100000,1000000'); parser.add_argument('--repetitions',type=int,default=25)
    parser.add_argument('--resume',action='store_true',help='reuse completed harness fixtures in the same output root; remeasure all, no fact regeneration')
    parser.add_argument('--public-root',help='new optional root for public fixture paths, e.g. /dev/shm/unique; production fsync stays enabled')
    parser.add_argument('--small',action='store_true',help='small fixture compatibility/kill run before explicit long run')
    args=parser.parse_args(); out=pathlib.Path(args.output).resolve(); out.mkdir(parents=True,exist_ok=args.resume)
    public_root=pathlib.Path(args.public_root).resolve() if args.public_root else out
    if public_root != out: public_root.mkdir(parents=True,exist_ok=args.resume)
    if args.resume:
        superseded=out/f'superseded-{time.time_ns()}'
        superseded.mkdir()
        for old in [*out.glob('*.strace'),out/'results.jsonl',out/'environment.json']:
            if old.exists(): shutil.move(str(old),superseded/old.name)
    (out/'environment.json').write_text(json.dumps({
        'measurementBinarySha256':hashlib.sha256(DLL.read_bytes()).hexdigest(),
        'sdkFromGlobalJson':json.loads((REPO/'global.json').read_text())['sdk']['version'],
        'osCache':'not dropped','cold':'new journal instance',
        'fixtureSidecars':'outside journal root','mounts':pathlib.Path('/proc/mounts').read_text().splitlines()[:1]
    },indent=2)+'\n')
    scenarios=[(kind,n) for kind in ['moves','orphans','control-allocations'] for n in map(int,args.scales.split(','))]
    scenarios+= [('segments',n) for n in ([1,10] if args.small else [1,100,10000])]
    scenarios+= [('tags',n) for n in ([10,100] if args.small else [100,1000,10000])]
    scenarios+= [('chain',n) for n in ([10,100] if args.small else [1000,10000])]
    scenarios+= [('cache',32),('churn-public',10 if args.small else 1100),('shrink-public',20 if args.small else 2500)]
    with (out/'results.jsonl').open('w') as results:
        for kind,n in scenarios:
            root=(public_root if kind.endswith('public') else out)/f'{kind}-{n}'
            sidecar=pathlib.Path(str(root)+'.fixture.json')
            if args.resume and root.exists() and sidecar.exists():
                manifest=json.loads(sidecar.read_text())
                if manifest['Kind'] != kind or manifest['Count'] != n: raise RuntimeError('Resume fixture identity mismatch')
                fixture={'status':'FixtureReused','kind':kind,'count':n,'construction':manifest['Construction'],'note':'existing complete generated facts; sidecar outside journal inventory'}
            else:
                fixture=call('fixture',root,kind,n)[0]
            fs=subprocess.run(['stat','-f','-c','%T',str(root)],capture_output=True,text=True,check=True).stdout.strip()
            fixture['filesystem']=fs
            print(json.dumps(fixture),flush=True); results.write(json.dumps(fixture)+'\n'); results.flush()
            modes=['head','warm-head'] if kind in ['moves','segments'] else ['cold-chain','warm-chain'] if kind=='chain' else ['cache'] if kind=='cache' else ['open']
            for mode in modes:
                result=measure(root,mode,args.repetitions,out,args.strace)
                result['filesystem']=fs
                result['construction']=fixture['construction']
                print(json.dumps(result),flush=True); results.write(json.dumps(result)+'\n'); results.flush()
        phases=['EventBeforeAppend','EventAfterAppend','EventAfterDurableFlush','tag:BeforeTargetFlush','tag:BeforeAppend','tag:AfterAppend','tag:AfterDurableFlush',
            'CheckpointBeforeLogFlush','CheckpointBeforeBoundary','CheckpointBeforeWrite','CheckpointBeforeTempFlush','CheckpointBeforeReplace','CheckpointAfterReplace','CheckpointBeforeInstall',
            'rotation:OldFlush','rotation:NextCreate','rotation:NextFlush','rotation:LocatorCreate','rotation:LocatorFlush','rotation:LocatorReplace','rotation:LocatorPublished','rotation:OldDispose']
        for phase in phases:
            killroot=out/('kill-'+phase.replace(':','-'))
            if killroot.exists(): raise RuntimeError('Kill fixture exists; use a new output root or remove only known prior harness kill directories explicitly')
            result=kill_case(killroot,phase)
            print(json.dumps(result),flush=True); results.write(json.dumps(result)+'\n'); results.flush()
        results.write(json.dumps({'platform':'Windows','status':'PendingPlatform','scope':'atomic replacement/process interruption semantics'})+'\n')
if __name__=='__main__': main()
