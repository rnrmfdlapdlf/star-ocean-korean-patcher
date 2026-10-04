"""Build and verify all Xbox cheat combinations from retained Korean payloads.

Development inputs live beside this project under work/xbox360. The distributed
patcher needs only the resulting xdelta files and the two bundled tools.
"""
import argparse
import hashlib
import json
import shutil
import struct
import subprocess
import sys
from datetime import datetime, timedelta, timezone
from pathlib import Path

PROJECT = Path(__file__).resolve().parents[1]
DEV = PROJECT.parent
INPUT = DEV / 'work/xbox360/cheats_all_discs_20261004'
WORK = DEV / 'work/xbox360/patcher_compact_20261004'
ASSETS = PROJECT / 'Assets/Xbox360'
ENCODER = DEV / 'tools/xdelta3-3.2.1/xdelta3-3.2.1-windows-x86_64/xdelta3.exe'
SITE, GETTER, HELPER = 0x827DB9A8, 0x825FEE90, 0x82AB2D6C

sys.path[:0] = [str(INPUT), str(DEV / 'work/python_deps'),
               str(DEV / 'work/xbox360/arts_name_audit_20261004')]
import pefile
import cheat_native as native
import verify_native
from prepare_xex import xex
from build_executables import u32, optional, identity, diff_runs
from audit import fixture, branch_target


def sha(data):
    return hashlib.sha256(data).hexdigest()


def hash_file(path):
    h = hashlib.sha256()
    with path.open('rb') as stream:
        while data := stream.read(8 * 1024 * 1024):
            h.update(data)
    return h.hexdigest()


def write_json(path, value):
    path.write_text(json.dumps(value, ensure_ascii=False, indent=2) + '\n', encoding='utf8')


def run(args):
    result = subprocess.run(list(map(str, args)), capture_output=True)
    if result.returncode:
        raise RuntimeError(Path(args[0]).name + ' failed: ' + str(result.returncode))


def owned_unlink(path):
    assert path.resolve().is_relative_to(WORK.resolve())
    path.unlink()


def encode(source, target, delta, decoded):
    delta.parent.mkdir(parents=True, exist_ok=True)
    run([ENCODER, '-e', '-f', '-D', '-a', '-A', '-S', 'none', '-B', '134217728',
         '-s', source, target, delta])
    run([ASSETS / 'tools/xdelta3.exe', '-d', '-f', '-D', '-R', '-s', source, delta, decoded])
    assert decoded.stat().st_size == target.stat().st_size
    assert hash_file(decoded) == hash_file(target)
    owned_unlink(decoded)


def install(source, mask):
    assert 0 <= mask <= 7
    p = pefile.PE(data=source, fast_load=True)
    changed = bytearray(source)
    ranges = []

    def replace(address, before, after, reason):
        off = address - native.LOAD
        assert len(before) == len(after) and source[off:off + len(before)] == before
        assert all(off + len(after) <= at or off >= at + len(data) for at, data, *_ in ranges)
        changed[off:off + len(after)] = after
        ranges.append((off, after, reason, before))

    additions, caves = [], []
    if mask & 1:
        code = native.movement()
        replace(native.MOVE_CAVE, bytes(len(code)), code, 'movement 2x')
        replace(native.MOVE_HOOK, native.packed(native.branch(native.MOVE_HOOK, native.MOVE_ORIGINAL, True)),
                native.packed(native.branch(native.MOVE_HOOK, native.MOVE_CAVE, True)), 'movement call')
        caves.append((native.MOVE_CAVE, code))
        additions.append((native.MOVE_CAVE, 0x40000000 | ((len(code) // 4) << 8)))
    if mask & 2:
        code = native.recovery()
        replace(native.RECOVERY_CAVE, bytes(len(code)), code, 'post-battle HP MP recovery')
        replace(native.RECOVERY_HOOK, native.packed(native.branch(native.RECOVERY_HOOK, native.TIMED_UPDATE, True)),
                native.packed(native.branch(native.RECOVERY_HOOK, native.RECOVERY_CAVE, True)), 'recovery call')
        caves.append((native.RECOVERY_CAVE, code))
        additions.append((native.RECOVERY_CAVE, 0x40000000 | ((len(code) // 4) << 8) | 4))
    if mask & 4:
        replace(native.SAVE_SITE, bytes.fromhex('54c6dffe'), bytes.fromhex('38c00000'), 'save-point menu argument')
    for address, code in caves:
        section = next(s for s in p.sections if s.VirtualAddress <= address - native.LOAD < s.VirtualAddress + s.SizeOfRawData)
        extent = address - native.LOAD + len(code) - section.VirtualAddress
        assert section.Characteristics & 0x20000000 and extent <= section.SizeOfRawData
        if extent > section.Misc_VirtualSize:
            field = section.get_field_absolute_offset('Misc_VirtualSize')
            replace(native.LOAD + field, struct.pack('<I', section.Misc_VirtualSize), struct.pack('<I', extent), 'executable virtual coverage')
    exception = p.OPTIONAL_HEADER.DATA_DIRECTORY[3]
    at, size = exception.VirtualAddress, exception.Size
    entries = list(struct.iter_unpack('>II', source[at:at + size]))
    assert entries == sorted(entries) and source[at + size:at + size + 16] == bytes(16)
    for address, code in caves:
        assert not any(a < address + len(code) and address < a + 4 * ((z >> 8) & 0x3FFFFF) for a, z in entries)
    if additions:
        extra = len(additions) * 8
        table = b''.join(struct.pack('>II', *v) for v in sorted(entries + additions))
        replace(native.LOAD + at, source[at:at + size + extra], table, 'sorted runtime function table')
        field = exception.get_field_absolute_offset('Size')
        replace(native.LOAD + field, struct.pack('<I', size), struct.pack('<I', size + extra), 'exception table size')
        section = next(s for s in p.sections if s.Name.rstrip(b'\0') == b'.pdata')
        assert section.VirtualAddress + section.Misc_VirtualSize == at + size
        field = section.get_field_absolute_offset('Misc_VirtualSize')
        replace(native.LOAD + field, struct.pack('<I', size), struct.pack('<I', size + extra), 'pdata virtual size')
    result = bytes(changed)
    for bit, hook, original, cave, code in [
        (1, native.MOVE_HOOK, native.MOVE_ORIGINAL, native.MOVE_CAVE, native.movement()),
        (2, native.RECOVERY_HOOK, native.TIMED_UPDATE, native.RECOVERY_CAVE, native.recovery())]:
        assert result[hook-native.LOAD:hook-native.LOAD+4] == native.packed(native.branch(hook, cave if mask & bit else original, True))
        assert result[cave-native.LOAD:cave-native.LOAD+len(code)] == (code if mask & bit else bytes(len(code)))
    assert result[native.SAVE_SITE-native.LOAD:native.SAVE_SITE-native.LOAD+4] == bytes.fromhex('38c00000' if mask & 4 else '54c6dffe')
    q = pefile.PE(data=result, fast_load=True)
    exception = q.OPTIONAL_HEADER.DATA_DIRECTORY[3]
    table = list(struct.iter_unpack('>II', result[exception.VirtualAddress:exception.VirtualAddress+exception.Size]))
    assert table == sorted(entries + additions)
    assert result[0xB96C10:0xB96C10+151*8] == source[0xB96C10:0xB96C10+151*8]
    assert branch_target(result, SITE) == HELPER
    return result, [dict(address=native.LOAD+at, hex=data.hex(), before=before.hex(), reason=reason)
                    for at, data, reason, before in ranges]


def encode_xex(input_stage, stage, expected, mask):
    source = (input_stage / 'korean.base').read_bytes()
    plain = bytearray((input_stage / 'korean.plain.xex').read_bytes())
    pe_at = u32(plain, 8)
    fmt = optional(plain)[0x3FF]
    size, encryption, compression = struct.unpack_from('>IHH', plain, fmt)
    assert encryption == 0 and compression == 1 and (size - 8) % 8 == 0
    chunks, virtual, physical, reconstructed = [], 0, pe_at, bytearray()
    for i in range((size - 8) // 8):
        data_size, zero_size = struct.unpack_from('>II', plain, fmt + 8 + i*8)
        chunks.append((virtual, physical, data_size))
        reconstructed += plain[physical:physical+data_size] + bytes(zero_size)
        virtual += data_size + zero_size
        physical += data_size
    assert reconstructed == source and physical == len(plain)
    for offset, data in diff_runs(source, expected):
        matches = [physical+offset-virtual for virtual, physical, n in chunks if virtual <= offset and offset+len(data) <= virtual+n]
        assert len(matches) == 1, ('modified virtual zero run', hex(offset))
        plain[matches[0]:matches[0]+len(data)] = data
    plain_file, target = stage / ('variant-%d.plain.xex' % mask), stage / ('variant-%d.xex' % mask)
    plain_file.write_bytes(plain)
    # Basic, unencrypted XEX keeps byte-local native changes byte-local in the
    # distributed option deltas. Normal compressed/encrypted XEX would expand
    # a few instructions into nearly a whole-file option delta.
    xex('-c', 'u', '-e', 'u', '-o', target, plain_file)
    decoded = stage / ('verified-%d.base' % mask)
    xex('-b', decoded, target)
    assert decoded.read_bytes() == expected
    assert identity(target.read_bytes())[0] == identity((input_stage / 'original.xex').read_bytes())[0]
    encoded = target.read_bytes()
    info = optional(encoded)[0x3FF]
    _, encryption, compression = struct.unpack_from('>IHH', encoded, info)
    assert encryption == 0 and compression == 1
    return target


def main():
    WORK.mkdir(parents=True, exist_ok=True)
    staged_assets = WORK / 'assets'
    staged_assets.mkdir(exist_ok=True)
    plan = json.loads((ASSETS / 'manifest.json').read_text('utf8'))
    resources = json.loads((INPUT / 'korean-resource-verification.json').read_text('utf8'))
    protected = {str(p): (p.stat().st_size, p.stat().st_mtime_ns) for p in DEV.glob('Star Ocean*.iso')}
    proofs = []
    for disc in plan['discs']:
        number = disc['disc']
        stage, input_stage = WORK / ('disc%d' % number), INPUT / ('disc%d' % number)
        stage.mkdir(exist_ok=True)
        baseline = bytearray((input_stage / 'baseline.base').read_bytes())
        assert baseline[SITE-native.LOAD:SITE-native.LOAD+4] == native.packed(native.branch(SITE, GETTER, True))
        baseline[SITE-native.LOAD:SITE-native.LOAD+4] = native.packed(native.branch(SITE, HELPER, True))
        baseline = bytes(baseline)
        (stage / 'baseline.base').write_bytes(baseline)
        names = fixture(baseline, True)
        disc['cheats'] = []
        for mask in range(8):
            result, blocks = install(baseline, mask)
            retained_native = DEV / 'work/xbox360/patcher_final_20261004' / ('disc%d' % number) / ('variant-%d.base' % mask)
            if retained_native.is_file():
                assert result == retained_native.read_bytes(), 'Native code must remain identical to the verified build'
            (stage / ('variant-%d.base' % mask)).write_bytes(result)
            write_json(stage / ('variant-%d-blocks.json' % mask), blocks)
            if mask == 7:
                previous = bytearray((input_stage / 'cheats.base').read_bytes())
                previous[SITE-native.LOAD:SITE-native.LOAD+4] = native.packed(native.branch(SITE, HELPER, True))
                assert result == previous
                (stage / 'cheats.base').write_bytes(result)
            target = encode_xex(input_stage, stage, result, mask)
            name = 'disc%d_default.xdelta' % number if mask == 0 else 'cheats/disc%d-%d.xdelta' % (number, mask)
            delta = staged_assets / name
            base_xex = stage / 'variant-0.xex'
            encode(input_stage / 'original.xex' if mask == 0 else base_xex, target, delta, stage / 'delta-roundtrip.xex')
            if mask != 0:
                assert delta.stat().st_size < 64 * 1024, 'Option delta unexpectedly contains a complete executable'
            row = dict(mask=mask, delta=name if mask != 0 else None, delta_sha256=hash_file(delta) if mask != 0 else None,
                       source_size=base_xex.stat().st_size, source_sha256=hash_file(base_xex),
                       target_size=target.stat().st_size, target_sha256=hash_file(target))
            disc['cheats'].append(row)
            proofs.append(dict(disc=number, mask=mask, native_sha256=sha(result), xex_sha256=row['target_sha256'],
                               native_roundtrip_exact=True, delta_roundtrip_exact=True, disc_identity_preserved=True,
                               option_delta_from_cheat_free_base=mask != 0, delta_bytes=delta.stat().st_size,
                               only_selected_cheats_present=True, sorted_exception_table_valid=True, arts_names=names))
            print('Disc', number, 'option mask', mask, 'XEX and delta verified.', flush=True)
        baseline_row = next(f for f in disc['files'] if f['path'] == 'default.xex')
        baseline_delta = staged_assets / ('disc%d_default.xdelta' % number)
        baseline_row.update(target_size=disc['cheats'][0]['target_size'], target_sha256=disc['cheats'][0]['target_sha256'],
                            delta_sha256=hash_file(baseline_delta))
    verify_native.OUT = WORK
    native_results = [verify_native.verify_disc(d) for d in (1, 2, 3)]
    write_json(WORK / 'native-test-results.json', native_results)
    write_json(WORK / 'variant-verification.json', proofs)
    for disc in plan['discs']:
        number = disc['disc']
        iso = DEV / ('Star Ocean - The Last Hope (Japan) (Disc %02d).iso' % number)
        assert iso.stat().st_size == disc['iso_size'] and hash_file(iso) == disc['iso_sha256']
        stage = WORK / ('disc%d' % number)
        expected = next(p for p in resources if p['disc'] == number)['files']
        for row in disc['files']:
            if row['path'] not in ('soz0.bin', 'soz1.bin'):
                continue
            retained_delta = ASSETS / row['delta']
            if (row['target_size'] == expected[row['path']]['size'] and row['target_sha256'] == expected[row['path']]['sha256']
                    and retained_delta.is_file() and hash_file(retained_delta) == row['delta_sha256']):
                shutil.copy2(retained_delta, staged_assets / row['delta'])
                print('Disc', number, row['path'], 'verified retained resource delta reused.', flush=True)
                continue
            print('Disc', number, row['path'], 'extracting original and encoding retained Korean resources.', flush=True)
            target = INPUT / ('disc%d/files' % number) / row['path']
            assert target.stat().st_size == expected[row['path']]['size'] and hash_file(target) == expected[row['path']]['sha256']
            source, decoded = stage / ('original-' + row['path']), stage / ('decoded-' + row['path'])
            h, remaining = hashlib.sha256(), row['size']
            with iso.open('rb') as src, source.open('wb') as dst:
                src.seek(row['offset'])
                while remaining:
                    data = src.read(min(remaining, 8 * 1024 * 1024))
                    assert data
                    dst.write(data)
                    h.update(data)
                    remaining -= len(data)
            assert h.hexdigest() == row['sha256']
            delta = staged_assets / row['delta']
            encode(source, target, delta, decoded)
            row.update(target_size=target.stat().st_size, target_sha256=expected[row['path']]['sha256'], delta_sha256=hash_file(delta))
            owned_unlink(source)
            print('Disc', number, row['path'], 'complete-file delta roundtrip verified.', flush=True)
    for tool in plan['tools']:
        source = ASSETS / tool['path']
        assert hash_file(source) == tool['sha256']
        dest = staged_assets / tool['path']
        dest.parent.mkdir(parents=True, exist_ok=True)
        shutil.copy2(source, dest)
    plan.update(schema=3, version='v261004', built_at=datetime.now(timezone(timedelta(hours=9))).isoformat(),
                arts_collection_names_corrected=True, retained_name_font_and_guide_fixes=True,
                cheat_options={'1':'이동속도 2배', '2':'전투 종료 후 HP·MP 자동 회복', '4':'세이브 제한 해제'},
                cheat_default_mask=0, cheat_delta_basis='cheat-free Korean default.xex',
                executable_storage='basic unencrypted XEX; identical native images')
    write_json(staged_assets / 'manifest.json', plan)
    assert all((Path(p).stat().st_size, Path(p).stat().st_mtime_ns) == stats for p, stats in protected.items())
    write_json(WORK / 'generation-verification.json', dict(passed=True, executable_variants=24,
               resource_deltas=6, all_delta_roundtrips_exact=True, all_nine_arts_names_correct=True,
               option_deltas_from_cheat_free_base=True,
               option_delta_bytes=sum(p.stat().st_size for p in (staged_assets/'cheats').glob('*.xdelta')),
               original_and_existing_result_isos_unchanged=True, native_results=native_results,
               live_game_runtime_verified=False))
    for source in staged_assets.rglob('*'):
        if source.is_file() and source.name != 'manifest.json':
            target = ASSETS / source.relative_to(staged_assets)
            target.parent.mkdir(parents=True, exist_ok=True)
            shutil.copy2(source, target)
    shutil.copy2(staged_assets / 'manifest.json', ASSETS / 'manifest.json')
    print('Final Xbox package assets installed: all 24 executable combinations and 6 resource deltas verified.', flush=True)


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--work', type=Path, default=WORK)
    args = parser.parse_args()
    WORK = args.work.resolve()
    main()
