#!/usr/bin/env python3
"""Independent pinned original ClientChat process against actual published upgraded Server.
Only synthetic credentials/private state; no game assemblies, adapter-generated expected packets, or deployment.
"""
import argparse
import hashlib
import json
import os
from pathlib import Path
import re
import shutil
import sqlite3
import xml.etree.ElementTree as ET
import subprocess
import sys
import tarfile
import tempfile
import time
import zipfile
from legacy_probe import archive, ORIGINAL, PROTOBUF
from check import ROOT, digest


def main():
    p = argparse.ArgumentParser(description=__doc__)
    p.add_argument('--original-source', required=True, type=Path)
    p.add_argument('--pastel-package', required=True, type=Path)
    p.add_argument('--framework-root', required=True, type=Path)
    p.add_argument('--server', required=True, type=Path)
    p.add_argument('--config-writer', required=True, type=Path)
    p.add_argument('--modern-probe', type=Path, help='Optional compiled lifecycle probe for genuine mixed UDP phase')
    p.add_argument('--output', required=True, type=Path)
    p.add_argument('--trade', action='store_true', help='Run actual original Trading modules with explicitly enabled lossy trade adapter')
    a = p.parse_args()
    out = a.output.resolve(); out.mkdir(parents=True, exist_ok=False)
    result = {'schema': 1, 'passed': False, 'originalSha': ORIGINAL, 'protobufSha': PROTOBUF,
              'profile': 'thomotron Original ClientTrading against published upgraded Main / UDP' if a.trade else 'thomotron Original ClientChat against published upgraded Main / UDP',
              'gameAcceptance': False, 'seed': None, 'faultIds': [], 'checks': []}
    def run(name, command, cwd, timeout=180):
        with (out/(name+'.log')).open('w') as log:
            r = subprocess.run([str(x) for x in command], cwd=cwd, stdout=log, stderr=subprocess.STDOUT, timeout=timeout)
        result['checks'].append({'name': name, 'command': [str(x) for x in command], 'exit': r.returncode})
        if r.returncode: raise RuntimeError(name+' failed; see isolated log')
    try:
        for name in ['ChatExtension.Server.dll', 'LegacyAdapter.Server.dll']:
            if not (a.server.parent/'Extensions'/name).is_file():
                raise RuntimeError('Incomplete published extension closure: '+name)
        with tempfile.TemporaryDirectory(prefix='phinix-original-chat-') as temporary:
            work = Path(temporary); source = work/'original'; private = work/'private'; private.mkdir(mode=0o700)
            archive(a.original_source, ORIGINAL, ['Common'], source)
            archive(a.original_source/'Dependencies/protobuf', PROTOBUF, ['csharp'], source/'Dependencies/protobuf')
            lib = source/'.nuget/LiteNetLib.0.7.7.3/lib/net35/LiteNetLib.dll'; lib.parent.mkdir(parents=True)
            origin_lib = ROOT/'Dependencies/Phinix.Common/libs/net35/LiteNetLib.dll'
            if digest(origin_lib) != 'c4ad697e3032a221892d894f1f225738a819b4c061b3de7f8ac4437b01b9e0c8': raise RuntimeError('Original fixed LiteNetLib mismatch')
            shutil.copy2(origin_lib, lib)
            pastel = source/'.nuget/Pastel.1.3.1'; pastel.mkdir()
            with zipfile.ZipFile(a.pastel_package) as z:
                for name in z.namelist():
                    if Path(name).is_absolute() or '..' in Path(name).parts: raise RuntimeError('Unsafe package entry')
                z.extractall(pastel)
            pastel_lib = pastel/'lib/netstandard2.0/Pastel.dll'
            if digest(pastel_lib) != '43a7ef0c7dfc76986a3e955472795d0979213f3bfc7f57e2ecf1e5771f4940ed': raise RuntimeError('Original fixed Pastel mismatch')
            result['sourceHashes'] = {str(f.relative_to(source)): digest(f) for f in source.rglob('*') if f.is_file() and f.suffix in ('.cs', '.proto', '.csproj')}
            result['dependencyHashes'] = {'originalLiteNetLib': digest(lib), 'originalPastel': digest(pastel_lib), 'pastelNupkg': digest(a.pastel_package)}
            result['serverSha256'] = digest(a.server)
            result['serverFiles'] = {str(f.relative_to(a.server.parent)): digest(f) for f in a.server.parent.rglob('*') if f.is_file()}
            result['probeSourceHashes'] = {str(f.relative_to(ROOT)): digest(f) for f in (ROOT/'Tests/OriginalChatClientProbe').glob('*') if f.is_file()}
            proto = source/'Dependencies/protobuf/csharp/src/Google.Protobuf/Google.Protobuf.csproj'
            run('original-protobuf-restore', ['dotnet','restore',proto,'--disable-parallel','-m:1','-p:NuGetAudit=false','-p:TargetFrameworks=net472','-p:TargetFramework=net472'], work)
            run('original-chat-build', ['dotnet','msbuild',source/'Common/Chat/Chat.csproj','-t:Build','-p:Configuration=Release','-m:1','-p:BuildInParallel=false','-p:TargetFramework=net472','-p:TargetFrameworkRootPath='+str(a.framework_root.resolve())+'/','-p:EnableSourceControlManagerQueries=false','-p:EnableSourceLink=false'], work)
            run('original-trading-build', ['dotnet','msbuild',source/'Common/Trading/Trading.csproj','-t:Build','-p:Configuration=Release','-m:1','-p:BuildInParallel=false','-p:TargetFramework=net472','-p:TargetFrameworkRootPath='+str(a.framework_root.resolve())+'/','-p:EnableSourceControlManagerQueries=false','-p:EnableSourceLink=false'], work)
            producer = work/'producer'; shutil.copytree(ROOT/'Tests/OriginalChatClientProbe', producer)
            run('original-peer-build', ['dotnet','build',producer/'OriginalChatClientProbe.csproj','-c','Release','-m:1','-p:NuGetAudit=false','-p:LegacyOriginalRoot='+str(source)], work)
            run('config', ['dotnet',a.config_writer.resolve(),'--write-trade-config' if a.trade else '--write-config',private/'server.conf'], private)
            peers = private/'clients'
            for phase in (['trade-initial','trade-restart'] + (['trade-mixed'] if a.modern_probe else []) if a.trade else ['initial','restart'] + (['mixed','structured-restart'] if a.modern_probe else [])):
                log_path = out/(phase+'-server.log')
                with log_path.open('w') as log:
                    host = subprocess.Popen(['dotnet',str(a.server.resolve())],cwd=private,stdin=subprocess.PIPE,stdout=log,stderr=subprocess.STDOUT,text=True)
                    try:
                        deadline=time.monotonic()+12;port=None
                        while time.monotonic()<deadline and host.poll() is None:
                            text=log_path.read_text(errors='replace');m=re.search(r'listening on .*:(\d+)',text)
                            if m:port=int(m.group(1));break
                            time.sleep(.05)
                        if port is None:raise RuntimeError('Published Server startup failed; see isolated log')
                        if phase in ('mixed','trade-mixed'):
                            with (out/'mixed-modern.log').open('w') as modern_log:
                                modern = subprocess.Popen(['dotnet', str(a.modern_probe.resolve()), '--p5-trade-mixed' if a.trade else '--p5-mixed', str(port), str(peers)], cwd=private, stdout=modern_log, stderr=subprocess.STDOUT)
                                try:
                                    run(phase+'-native', ['mono',producer/'bin/Release/net472/OriginalChatClientProbe.exe',port,peers,phase],private,60)
                                    modern.wait(timeout=20)
                                    result['checks'].append({'name':'mixed-modern', 'exit':modern.returncode})
                                    if modern.returncode: raise RuntimeError('Mixed modern protocol checks failed; see isolated log')
                                finally:
                                    if modern.poll() is None: modern.kill(); modern.wait(timeout=5)
                        else:
                            run(phase+'-native', ['mono',producer/'bin/Release/net472/OriginalChatClientProbe.exe',port,peers,phase],private,60)
                        host.stdin.write('stop\n');host.stdin.flush();host.wait(timeout=15)
                        if host.returncode:raise RuntimeError('Published Server stop failed')
                    finally:
                        if host.poll() is None:host.kill();host.wait(timeout=5)
                        if host.stdin:host.stdin.close()
            if a.trade:
                databases = list(private.rglob('trade-state.bin.sqlite3'))
                if len(databases) != 1: raise RuntimeError('Unique authoritative trade database not found')
                with sqlite3.connect('file:'+str(databases[0])+'?mode=ro', uri=True) as db:
                    ledger = db.execute("SELECT value FROM state WHERE id='trade-ledger-v1'").fetchone()[0]
                state = ET.fromstring(ledger)
                local = lambda e: e.tag.rsplit('}',1)[-1]
                pending = next(e for e in state if local(e)=='PendingCompletionNotifications')
                rows = [{local(field):field.text for field in entry} for entry in pending]
                expected = json.loads((peers/'trade-expected.json').read_text())
                offline = [row for row in rows if row.get('TradeId')==expected['cancelled'] and row.get('RecipientUuid')==expected['b']]
                if len(offline)!=1 or offline[0].get('DeliveryState')!='OutcomeUnknown' or offline[0].get('AckObserved')=='true':
                    raise RuntimeError('Original offline dispatch fabricated ACK, lost retained evidence, or remained unattempted')
                result['authoritativeInspection'] = {'method':'independent Python sqlite3 read-only + XML parse after server stop',
                    'ledgerSha256':hashlib.sha256(ledger).hexdigest(),'offlineOriginalOutcome':'OutcomeUnknown',
                    'offlineOriginalAckObserved':False,'rowsForOfflineDelivery':len(offline),'gameAssetsVerified':False}
            result['originalBinaryHashes']={str(f.relative_to(source)):digest(f) for f in source.rglob('*.dll') if 'bin' in f.parts}
            result['passed']=True
    except Exception as error:
        result['error']=str(error);print(result['error'],file=sys.stderr)
    finally:
        (out/'manifest.json').write_text(json.dumps(result,indent=2)+'\n')
    print(json.dumps({'passed':result['passed'],'output':str(out)}))
    return 0 if result['passed'] else 1

if __name__=='__main__':sys.exit(main())
