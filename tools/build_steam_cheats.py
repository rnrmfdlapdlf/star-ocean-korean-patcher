"""Build verified Steam EXE option deltas; never write to an installed game."""
import sys, json, hashlib, subprocess, struct
from pathlib import Path
ROOT = Path(__file__).resolve().parents[1]
DEV = ROOT.parent
WORK = DEV / 'work/steam_cheat_options_20261004'
ASSETS = ROOT / 'Assets/cheats'
ENCODER = DEV / 'tools/xdelta3-3.2.1/xdelta3-3.2.1-windows-x86_64/xdelta3.exe'
DECODER = ENCODER.with_name('xdelta3decode.exe')
sys.path.insert(0, str(DEV / 'work/python_deps'))
sys.path.insert(0, str(DEV / 'work/steam_battle_recovery_20261004'))
import pefile
import apply_steam_battle_recovery as recovery

def sha(b): return hashlib.sha256(b).hexdigest()
def run(args): subprocess.run([str(x) for x in args],check=True,capture_output=True)
def encode(source,target,dest):
    run([ENCODER,'-e','-f','-D','-a','-A','-S','none','-B','134217728','-s',source,target,dest])
    output=WORK/'decoded.exe'
    run([DECODER,'-d','-f','-D','-R','-s',source,dest,output])
    assert output.read_bytes() == target.read_bytes(), dest

def main():
    WORK.mkdir(parents=True,exist_ok=True); ASSETS.mkdir(parents=True,exist_ok=True)
    base=(DEV/'work/steam_movement_2x_20261004/StarOceanTheLastHope.original.exe').read_bytes()
    assert sha(base)=='284b237259c73c4b02545a2e985eba739b3a68ea365384ebed6666ea2a01a4aa'
    pe=pefile.PE(data=base,fast_load=True)
    movement=json.loads((DEV/'work/steam_movement_2x_20261004/patch-record.json').read_text())
    groups={1:[],2:[],4:[(pe.get_offset_from_rva(0x7b6f0c),bytes.fromhex('448bcf'),bytes.fromhex('4531c9'),'save-point restriction')]}
    speed=bytearray(base)
    for p in movement['patches']:
        at=int(p['file_offset'],16); before=bytes.fromhex(p['before_hex']); after=bytes.fromhex(p['after_hex'])
        assert base[at:at+len(before)]==before
        groups[1].append((at,before,after,'movement x2'));speed[at:at+len(after)]=after
    assert sha(speed)==movement['patched_sha256']
    rp,stub,unwind,healed,changes,added=recovery.prepare(bytes(speed))
    groups[2]=changes
    native=recovery.native_test(rp,stub,unwind)
    all_regions=sorted((at,at+len(before),bit) for bit,rs in groups.items() for at,before,after,_ in rs)
    assert all(a[1]<=b[0] for a,b in zip(all_regions,all_regions[1:]))
    variants=[]; paths={}; proof=[]
    for mask in range(8):
        result=bytearray(base)
        for bit,regions in groups.items():
            if mask & bit:
                for at,before,after,label in regions:
                    assert len(before)==len(after) and result[at:at+len(before)]==before
                    result[at:at+len(after)]=after
        if mask==3: assert bytes(result)==healed
        image=pefile.PE(data=bytes(result),fast_load=True)
        assert image.OPTIONAL_HEADER.ImageBase==pe.OPTIONAL_HEADER.ImageBase
        ex=image.OPTIONAL_HEADER.DATA_DIRECTORY[3]
        entries=list(struct.iter_unpack('<III',image.get_data(ex.VirtualAddress,ex.Size)))
        assert entries==sorted(entries)
        assert (added in entries)==bool(mask & 2)
        for bit,regions in groups.items():
            for at,before,after,label in regions:
                assert result[at:at+len(before)]==(after if mask & bit else before)
        path=WORK/f'variant-{mask}.exe';path.write_bytes(result);paths[mask]=path
        variants.append({'mask':mask,'hash':sha(result),'forwardFile':None,'forwardHash':None,'reverseFile':None,'reverseHash':None})
        proof.append({'mask':mask,'sha256':sha(result),'length':len(result),'all_selected_and_unselected_regions_exact':True,'unwind_table_valid':True})
    for v in variants[1:]:
        mask=v['mask']; forward=ASSETS/f'{mask}.xdelta'; reverse=ASSETS/f'{mask}-original.xdelta'
        encode(paths[0],paths[mask],forward);encode(paths[mask],paths[0],reverse)
        v.update(forwardFile=f'cheats/{forward.name}',forwardHash=sha(forward.read_bytes()),reverseFile=f'cheats/{reverse.name}',reverseHash=sha(reverse.read_bytes()))
        print('Verified option mask',mask,'delta bytes',forward.stat().st_size,'reverse',reverse.stat().st_size,flush=True)
    catalog={'schema':1,'baseHash':sha(base),'length':len(base),'variants':variants}
    (ROOT/'Assets/SteamCheats.json').write_text(json.dumps(catalog,indent=2)+'\n',encoding='utf-8')
    # Compare genuinely composable region patches to the complete-variant approach.
    components=WORK/'component-comparison';components.mkdir(exist_ok=True)
    component_bytes=0; raw_bytes=0
    for bit,regions in groups.items():
        for i,(at,before,after,label) in enumerate(regions):
            a=components/'source';b=components/'target';d=components/f'{bit}-{i}.xdelta';a.write_bytes(before);b.write_bytes(after)
            encode(a,b,d);component_bytes+=d.stat().st_size;raw_bytes+=len(after)
    forward_bytes=sum((ROOT/'Assets'/v['forwardFile']).stat().st_size for v in variants[1:])
    reverse_bytes=sum((ROOT/'Assets'/v['reverseFile']).stat().st_size for v in variants[1:])
    report={'passed':True,'game_modified':False,'source_hash':sha(base),'variants':proof,'regions_independent':True,
            'native_recovery_tests':native,'comparison':{'seven_forward_variant_deltas_bytes':forward_bytes,
            'seven_reverse_variant_deltas_bytes':reverse_bytes,'component_range_deltas_bytes':component_bytes,
            'raw_replacement_ranges_bytes':raw_bytes},
            'choice':'Complete variants from the same unmodified Steam EXE; reverse deltas normalize recognized previous selections before selecting the next variant. All 8 combinations are exhaustively verified.'}
    (WORK/'generation-verification.json').write_text(json.dumps(report,indent=2)+'\n',encoding='utf-8')
    print(json.dumps(report['comparison']),flush=True)

if __name__=='__main__':main()
