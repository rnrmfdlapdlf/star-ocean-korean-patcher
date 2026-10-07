"""Check the built title, cache stability, and stale-version rejection."""
import argparse
import copy
import hashlib
import io
import json
import os
import subprocess
import sys
import zipfile
from pathlib import Path

sys.dont_write_bytecode = True
sha = lambda data: hashlib.sha256(data).hexdigest()


def load(path):
    return json.loads(path.read_text('utf-8-sig'))


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('project',type=Path)
    parser.add_argument('work',type=Path)
    args = parser.parse_args()
    project, work = args.project.resolve(),args.work.resolve()
    root = project.parent
    assert work.is_relative_to(root/'work')
    work.mkdir(parents=True,exist_ok=True)
    plan = load(project/'Assets/DeltaManifest.json')
    info = load(project/'Assets/TitleVersion.json')
    before = load(root/'work/dll_font_guard_20261007/before-package.json')
    changed = [i for i,(old,new) in enumerate(zip(before['operations'],plan['operations'])) if old!=new]
    assert changed==[info['operation']]
    old, new = before['operations'][changed[0]],plan['operations'][changed[0]]
    assert {key for key in old if old[key]!=new[key]}=={'targetHash','deltaHash'}
    assert plan['nativeHash']==sha((project/'Assets/wininet.dll').read_bytes())==load(root/'work/dll_font_guard_20261007/package-verification.json')['dll_sha256']
    for path,digest in before['xbox'].items(): assert sha((project/path).read_bytes())==digest
    for path,digest in before['readmes'].items(): assert sha(Path(path).read_bytes())==digest

    data = (project/'bin/Release/SO4KoreanPatch.data').read_bytes()
    with zipfile.ZipFile(io.BytesIO(data)) as archive:
        built = json.loads(archive.read('manifest.json'))
        assert built['version']==info['version'] and built['operations']==plan['operations']
        for operation in built['operations']:
            assert sha(archive.read(operation['stageFile']))==operation['deltaHash']
        delta = archive.read(new['stageFile'])
        assert sha(delta)==info['deltaHash']
        assert sha(archive.read('wininet.dll'))==built['nativeHash']==plan['nativeHash']
    generated = (project/'src/Generated/BuildInfo.cs').read_text('utf-8-sig')
    assert sha(data) in generated and built['version'] in generated and built['buildId'] in generated
    release = project/'bin'/('SO4KoreanPatcher.Xdelta-'+built['version']+'.zip')
    with zipfile.ZipFile(release) as archive:
        assert archive.read('SO4KoreanPatch.data')==data
        assert archive.read('SO4KoreanPatcher.exe')==(project/'bin/Release/SO4KoreanPatcher.exe').read_bytes()
        for path,digest in before['xbox'].items():
            assert sha(archive.read(path.replace('\\','/').removeprefix('Assets/')))==digest

    # Decode the actual packaged delta, rather than checking only a saved preview.
    packaged_delta, decoded = work/'packaged-title.xdelta',work/'packaged-title.bin'
    packaged_delta.write_bytes(delta)
    # The renderer work folder uses four-digit years.
    native_work = root/'work'/('title_version_20'+built['version'][1:])
    encoder = root/'tools/xdelta3-3.2.1/xdelta3-3.2.1-windows-x86_64/xdelta3.exe'
    result = subprocess.run([str(encoder.with_name('xdelta3decode.exe')),'-d','-f','-D','-R',
                             '-s',str(native_work/'original-package.bin'),str(packaged_delta),str(decoded)],capture_output=True)
    assert result.returncode==0
    assert sha(decoded.read_bytes())==new['targetHash']==info['targetHash']
    assert decoded.read_bytes()==(native_work/'after-package.bin').read_bytes()

    watched = [project/'Assets/DeltaManifest.json',project/'Assets/TitleVersion.json',project/'Assets'/new['stageFile']]
    snapshot = {str(path):(sha(path.read_bytes()),path.stat().st_mtime_ns) for path in watched}
    cached = subprocess.run([sys.executable,'-B',str(project/'tools/refresh_title_version.py'),
                             '--project',str(project),'--development-root',str(root),'--encoder',str(encoder)],capture_output=True)
    assert cached.returncode==0 and b'Verified title version:' in cached.stdout
    assert all(snapshot[str(path)]==(sha(path.read_bytes()),path.stat().st_mtime_ns) for path in watched)

    fixture = work/'rejected-title-inputs'
    (fixture/'Assets').mkdir(parents=True,exist_ok=True)
    (fixture/'Assets/DeltaManifest.json').write_text(json.dumps(plan),'utf-8')
    (fixture/'Assets/wininet.dll').write_bytes((project/'Assets/wininet.dll').read_bytes())
    environment = {key.upper():value for key,value in os.environ.items()}
    rejected = []
    for label in ['missing title record','old title version','wrong title hash','wrong delta hash','wrong displayed credit','wrong author']:
        title = copy.deepcopy(info)
        path = fixture/'Assets/TitleVersion.json'
        if label=='missing title record':
            if path.exists(): path.unlink()
        else:
            if label=='old title version': title['version']='v261004'
            elif label=='wrong title hash': title['targetHash']='0'*64
            elif label=='wrong delta hash': title['deltaHash']='0'*64
            elif label=='wrong displayed credit': title['displayedCredit']='한글패치 v261004'
            else: title['author']='other author'
            path.write_text(json.dumps(title),'utf-8')
        result = subprocess.run([str(project/'obj/BuildPackage.exe'),'data',str(fixture)],capture_output=True,env=environment)
        assert result.returncode==1 and not (fixture/'bin/Release/SO4KoreanPatch.data').exists()
        rejected.append(label)

    game = Path(r'D:\SteamLibrary\steamapps\common\STAR OCEAN - THE LAST HOPE - 4K & Full HD Remaster')
    journal = load(game/'SO4KoreanPatch.install.json')
    installed_title = next(op for op in journal['operations'] if op['label']=='0015.pkg')
    with (game/installed_title['targetFile']).open('rb') as stream:
        stream.seek(installed_title['targetOffset'])
        installed_hash = sha(stream.read(installed_title['targetLength']))
    assert installed_hash==installed_title['targetHash']
    report = dict(passed=True,version=built['version'],buildId=built['buildId'],
        displayedCredit=info['displayedCredit'],packageDataHash=sha(data),releaseZip=str(release),
        changedOperations=changed,otherOperationsUnchanged=len(plan['operations'])-1,
        xboxAssetsUnchanged=len(before['xbox']),readmesUnchanged=True,
        packagedTitleDeltaRoundtripExact=True,sameVersionBuildCachePreserved=True,invalidTitleInputsRejected=rejected,
        installedDllMatchesGuardBuild=sha((game/'wininet.dll').read_bytes())==plan['nativeHash'],
        installedTitleStillPreviousVersion=installed_hash==info['previousTargetHash'],
        installedGameModified=False,preview=info['preview'])
    (work/'build-title-verification.json').write_text(json.dumps(report,ensure_ascii=False,indent=2),'utf-8')
    print(json.dumps(report,ensure_ascii=False))


if __name__=='__main__':
    main()
