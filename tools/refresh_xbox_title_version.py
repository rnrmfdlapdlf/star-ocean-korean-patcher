"""Xbox 360 타이틀 화면의 '한글패치 vYYMMDD' 표기를 빌드 날짜로 갱신한다.

- 그림은 원본 ISO의 타이틀(PACK 13, 멤버 1)에 기존 patch_title_xbox.py와 같은 방식으로 두 영역만 다시 그린다.
- 현재 Xbox 기준본(work/current_xbox_reference.json)의 디스크 1~3에서 해당 멤버를 제자리 교체한다(슬롯 크기 유지).
- 바뀐 디스크 파일만 원본 ISO 대비 전체 파일 차분을 다시 만들고, 배포용 xdelta로 역변환해 해시를 확인한다.
- 이미 오늘 날짜이면 아무것도 바꾸지 않는다. 원본 ISO와 게임 설치본은 읽기만 한다.
"""
import argparse, datetime, hashlib, json, mmap, struct, subprocess, sys
from pathlib import Path

sys.dont_write_bytecode = True


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument('--development-root', default=str(Path(__file__).resolve().parents[2]))
    ap.add_argument('--project', default=str(Path(__file__).resolve().parents[1]))
    args = ap.parse_args()
    root, project = Path(args.development_root), Path(args.project)
    sys.path[:0] = [str(root / 'work/xbox360/tools'), str(root / 'work/python_deps')]
    import so4x360 as xbox
    import slz3_xbox
    from asf_textures import pixels, swap16
    from package_probe import table
    from PIL import Image, ImageDraw, ImageFont

    load = lambda p: json.loads(Path(p).read_text('utf-8-sig'))
    sha = lambda d: hashlib.sha256(d).hexdigest()

    def file_hash(path):
        h = hashlib.sha256()
        with Path(path).open('rb') as s:
            while data := s.read(8 * 1024 * 1024): h.update(data)
        return h.hexdigest()

    def save(path, value):
        tmp = path.with_name(path.name + '.title.tmp'); tmp.write_text(json.dumps(value, ensure_ascii=False, indent=2) + '\n', 'utf-8'); tmp.replace(path)

    def run(cmd):
        r = subprocess.run(list(map(str, cmd)), capture_output=True)
        if r.returncode: raise RuntimeError(r.stderr.decode('utf-8', 'replace'))

    version = 'v' + datetime.datetime.now(datetime.timezone(datetime.timedelta(hours=9))).strftime('%y%m%d')
    assets = project / 'Assets/Xbox360'; manifest_path = assets / 'manifest.json'; manifest = load(manifest_path)
    current_ref = load(root / 'work/current_xbox_reference.json'); ref = root / current_ref['resourceReference']
    title_dir = root / 'work/xbox360/translation_port/title'; title_info = load(title_dir / 'build.json')
    if manifest.get('title_version') == version and title_info.get('version') == version:
        print('Xbox title already shows', version); return

    # 1) 원본 ISO 타이틀에 표기를 그린다(patch_title_xbox.py와 같은 렌더링·BC3 블록 규칙).
    def block_bc3(px):
        alphas = [255, 0, 218, 182, 145, 109, 72, 36]; colors = [255, 0, 170, 85]
        a = sum(min(range(8), key=lambda j: abs(alphas[j] - p[3])) << (i * 3) for i, p in enumerate(px))
        c = sum(min(range(4), key=lambda j: abs(colors[j] - p[0])) << (i * 2) for i, p in enumerate(px))
        return bytes([255, 0]) + a.to_bytes(6, 'little') + struct.pack('<HHI', 65535, 0, c)

    def paint(text, size, wh, center):
        im = Image.new('RGBA', wh); draw = ImageDraw.Draw(im); font = ImageFont.truetype(str(root / 'tools/Noto_Sans_KR/static/NotoSansKR-Medium.ttf'), size)
        for i, line in enumerate(text.splitlines()):
            b = font.getbbox(line, stroke_width=2); w, h = b[2] - b[0], b[3] - b[1]
            x = (wh[0] - w) // 2 if center else 4; y = (wh[1] - h) // 2 if center else 4 + i * 34
            assert x >= 0 and y + h <= wh[1]; draw.text((x - b[0], y - b[1]), line, font=font, fill='white', stroke_width=2, stroke_fill='black')
        return im

    iso = xbox.Iso(xbox.inventory()[0]); bundle = iso.package(13); m = xbox.members(bundle)[1]
    source = xbox.decode(bundle[m['offset']:m['offset'] + m['length']]); iso.close()
    assert sha(source) == title_info['source_sha256']
    new = bytearray(source); rects = [(800, 560, 1120, 624), (44, 936, 452, 1008)]
    canvases = [paint('스타 오션 4', 46, (320, 64), True), paint('한글패치 ' + version + '\nby Gideon', 27, (408, 72), False)]; changed = []
    for a in (20895216, 23129120):
        old, start, addresses, block = pixels(source, a, 3); assert old.size == (1920, 1080) and block == 16
        assert old.crop(rects[1]).getchannel('A').getbbox() is None
        for rect, canvas in zip(rects, canvases):
            for y in range(rect[1], rect[3], 4):
                for x in range(rect[0], rect[2], 4):
                    at = start + int(addresses[y // 4, x // 4]); raw = block_bc3(list(canvas.crop((x - rect[0], y - rect[1], x - rect[0] + 4, y - rect[1] + 4)).getdata()))
                    new[at:at + 16] = swap16(raw); changed.append((at, at + 16))
        after = pixels(new, a, 3)[0]; proof = after.copy()
        for r in rects: proof.paste(old.crop(r), r[:2])
        assert proof.tobytes() == old.tobytes(), 'title pixels outside the two rectangles changed'
        if a == 20895216:
            preview = Image.new('RGBA', after.size, '#102139'); preview.alpha_composite(after)
            preview.resize((960, 540), Image.Resampling.LANCZOS).convert('RGB').save(title_dir / 'title_preview.png')
    cursor = 0
    for a0, b0 in sorted(changed): assert new[cursor:a0] == source[cursor:a0]; cursor = b0
    assert new[cursor:] == source[cursor:] and len(new) == len(source)
    new = bytes(new); old_target = (title_dir / 'patched_asf.bin').read_bytes(); assert sha(old_target) == title_info['target_sha256']

    # 2) 기준본 디스크 1~3의 PACK 13 멤버 1을 교체한다.
    affected = set()
    for disc in (1, 2, 3):
        folder = ref / f'disc{disc}/files'; handles = [(folder / n).open('r+b') for n in ('soz0.bin', 'soz1.bin')]
        maps = [mmap.mmap(h.fileno(), 0, access=mmap.ACCESS_WRITE) for h in handles]
        try:
            toc_before = bytes(maps[0][:0xF000]); toc = table(toc_before)
            address = xbox.u32(toc, 4 * (0x1400 + 13)); ix = 1 if address & 0x10000000 else 0
            at = (address & 0x0FFFFFFF) * 2048; size = xbox.u32(toc, 4 * 13) * 2048
            package = bytes(maps[ix][at:at + size]); rows = xbox.members(package); row = rows[1]
            limit = rows[2]['offset'] if len(rows) > 2 else xbox.u32(package, 12)
            payload = package[row['offset']:row['offset'] + row['length']]
            current = sha(xbox.decode(payload))
            if current == sha(new):  # 이전 실행이 차분 단계에서 멈춘 경우: 그림은 이미 교체됨
                affected.add((disc, ('soz0.bin', 'soz1.bin')[ix])); continue
            assert current == title_info['target_sha256'], (disc, 'current title differs from recorded build')
            encoded = xbox.stored(new, payload) if payload[3] == 0 else slz3_xbox.encode(new, payload)
            assert len(encoded) <= limit - row['offset'] and xbox.decode(encoded) == new, (disc, 'title slot')
            target = bytearray(package); target[row['offset']:limit] = encoded + bytes(limit - row['offset'] - len(encoded)); target = bytes(target)
            assert len(target) == len(package) and target[:row['offset']] == package[:row['offset']] and target[limit:] == package[limit:]
            maps[ix][at:at + size] = target; maps[ix].flush(); assert bytes(maps[0][:0xF000]) == toc_before
            affected.add((disc, ('soz0.bin', 'soz1.bin')[ix]))
        finally:
            for mm in maps: mm.close()
            for h in handles: h.close()

    # 3) 바뀐 디스크 파일의 전체 차분을 다시 만들고 역변환으로 확인한다.
    encoder = root / 'tools/xdelta3-3.2.1/xdelta3-3.2.1-windows-x86_64/xdelta3.exe'; temp = ref / 'title-work'; temp.mkdir(exist_ok=True)
    for disc, name in sorted(affected):
        d = next(x for x in manifest['discs'] if x['disc'] == disc); f = next(x for x in d['files'] if x['path'] == name)
        iso_path = root / f'Star Ocean - The Last Hope (Japan) (Disc {disc:02d}).iso'; state = iso_path.stat()
        src_file, decoded, target = temp / 'original.bin', temp / 'decoded.bin', ref / f'disc{disc}/files' / name
        h = hashlib.sha256(); remaining = f['size']
        with iso_path.open('rb') as s, src_file.open('wb') as o:
            s.seek(f['offset'])
            while remaining:
                data = s.read(min(remaining, 8 * 1024 * 1024)); assert data; o.write(data); h.update(data); remaining -= len(data)
        assert h.hexdigest() == f['sha256']
        print('Encoding Xbox title delta: disc', disc, name, flush=True)
        delta = assets / f['delta']; tmp_delta = temp / f['delta']
        run([encoder, '-e', '-f', '-D', '-a', '-A', '-S', 'none', '-B', '134217728', '-s', src_file, target, tmp_delta])
        run([assets / 'tools/xdelta3.exe', '-d', '-f', '-D', '-R', '-s', src_file, tmp_delta, decoded])
        th = file_hash(target); assert decoded.stat().st_size == target.stat().st_size and file_hash(decoded) == th
        tmp_delta.replace(delta)
        f.update(target_sha256=th, target_size=target.stat().st_size, delta_sha256=file_hash(delta))
        for t in (src_file, decoded): t.unlink()
        assert iso_path.stat().st_size == state.st_size and iso_path.stat().st_mtime_ns == state.st_mtime_ns
    temp.rmdir()

    (title_dir / 'patched_asf.bin').write_bytes(new)
    title_info.update(target_sha256=sha(new), version=version); save(title_dir / 'build.json', title_info)
    manifest.update(version=version, title_version=version, built_at=datetime.datetime.now(datetime.timezone(datetime.timedelta(hours=9))).isoformat())
    save(manifest_path, manifest)
    current_ref['version'] = version; save(root / 'work/current_xbox_reference.json', current_ref)
    print('Xbox title updated and verified:', version, 'files', sorted(affected), flush=True)


if __name__ == '__main__':
    main()
