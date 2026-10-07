"""Update the PC title credit from authenticated staged data during builds.

The installed game and the translation reference are never written. Only the
credit rectangle in the title texture and its single xdelta operation change.
"""
import argparse
import copy
import hashlib
import json
import re
import struct
import subprocess
import sys
from datetime import datetime, timedelta, timezone
from pathlib import Path

sys.dont_write_bytecode = True
RECTS = [(1640, 960, 2248, 1088), (88, 1944, 904, 2096)]


def sha(data):
    return hashlib.sha256(data).hexdigest()


def load(path):
    return json.loads(path.read_text('utf-8-sig'))


def atomic_json(path, value):
    temporary = path.with_name(path.name + '.title.tmp')
    if temporary.exists():
        raise ValueError('Unfinished title metadata update: ' + str(temporary))
    temporary.write_text(json.dumps(value, ensure_ascii=False, indent=2) + '\n', 'utf-8')
    temporary.replace(path)


def run(arguments):
    result = subprocess.run(list(map(str, arguments)), capture_output=True)
    if result.returncode:
        raise RuntimeError('Title xdelta operation failed: ' + str(result.returncode))


def title_renderer(root, work):
    # Reuse only the established pure renderer; never import its game installer.
    from PIL import Image, ImageDraw, ImageFont
    source = (root/'tools/patch_title_style.py').read_text('utf-8')
    source = source[source.index('def bc3_grayscale'):source.index('\ndef prepare():')]
    blank = "assert old.crop(rectangles[1]).getchannel('A').getbbox() is None,'Credits would overwrite existing artwork'"
    loop = 'for rect,canvas in zip(rectangles,[title,credit]):'
    if blank not in source or loop not in source:
        raise ValueError('Established title renderer has changed; review its credit-only scope')
    source = source.replace(blank, 'pass # Replace only the existing patch credit')
    source = source.replace(loop, 'for rect,canvas in zip(rectangles[1:],[credit]):')

    class Parameters:
        FONT = root/'tools/Noto_Sans_KR/static/NotoSansKR-Medium.ttf'

        @staticmethod
        def u32(data, offset):
            return struct.unpack_from('<I', data, offset)[0]

    namespace = dict(Image=Image, ImageDraw=ImageDraw, ImageFont=ImageFont,
                     struct=struct, p=Parameters, OUT=work, RECTS=RECTS)
    exec(compile(source, '<verified title credit renderer>', 'exec'), namespace)
    return namespace['patch']


def credit_preview(data, path):
    from PIL import Image
    fma = struct.unpack_from('<I', data, 4)[0]
    buffer = fma + struct.unpack_from('<Q', data, fma+0x68)[0]
    descriptor = 0x8800
    width, height = struct.unpack_from('<HH', data, descriptor+40)
    guid = data[descriptor+64:descriptor+80]
    address = data.find(guid, fma)
    size, offset = struct.unpack_from('<QQ', data, address+16)
    assert (width, height) == (4096, 4096) and size == width*height
    image = Image.frombytes('RGBA', (width, height), data[buffer+offset:buffer+offset+size], 'bcn', 3)
    crop = image.crop(RECTS[1])
    background = Image.new('RGBA', crop.size, '#132247')
    background.alpha_composite(crop)
    background.convert('RGB').save(path)


def refresh(project, root, version, encoder):
    if not re.fullmatch(r'v\d{6}', version):
        raise ValueError('Invalid title version')
    project, root, encoder = project.resolve(), root.resolve(), encoder.resolve()
    if not project.is_relative_to(root) or not encoder.is_relative_to(root):
        raise ValueError('Build input outside the development directory')
    assets = project/'Assets'
    manifest = assets/'DeltaManifest.json'
    metadata = assets/'TitleVersion.json'
    plan = load(manifest)
    candidates = [(index, op) for index, op in enumerate(plan['operations']) if op['label']=='0015.pkg']
    if len(candidates)!=1:
        raise ValueError('Expected exactly one complete title package operation')
    index, operation = candidates[0]
    delta = (assets/operation['stageFile']).resolve()
    if not delta.is_relative_to(assets.resolve()) or sha(delta.read_bytes())!=operation['deltaHash']:
        raise ValueError('Title delta differs from the authenticated manifest')
    if metadata.exists():
        prior = load(metadata)
        if (prior.get('passed') and prior.get('version')==version and
            prior.get('displayedCredit')=='한글패치 '+version and prior.get('author')=='by Gideon' and
            prior.get('targetHash')==operation['targetHash'] and prior.get('deltaHash')==operation['deltaHash']):
            print('Verified title version: '+version, flush=True)
            return

    work = root/'work'/('title_version_'+datetime.now(timezone(timedelta(hours=9))).strftime('%Y%m%d'))
    work.mkdir(parents=True, exist_ok=True)
    sys.path[:0] = [str(root/'tools'), str(root/'work/python_deps')]
    import so4loc
    import slz3_encode
    original = root/'backup_before_korean_patch/original_2026-07-11'/operation['sourceFile']
    with original.open('rb') as stream:
        stream.seek(operation['sourceOffset'])
        source = stream.read(operation['sourceLength'])
    if len(source)!=operation['sourceLength'] or sha(source)!=operation['sourceHash']:
        raise ValueError('Original title package differs from the authenticated source')
    source_file, prior_file = work/'original-package.bin', work/'before-package.bin'
    source_file.write_bytes(source)
    decoder = encoder.with_name('xdelta3decode.exe')
    run([decoder,'-d','-f','-D','-R','-s',source_file,delta,prior_file])
    prior = prior_file.read_bytes()
    if len(prior)!=operation['targetLength'] or sha(prior)!=operation['targetHash']:
        raise ValueError('Title xdelta result differs from the authenticated target')
    archive = so4loc.parse_kcap(prior_file)
    member = archive.members[0]
    raw = so4loc.read_member(archive, 0)
    credit_preview(raw, work/'credit-before.png')
    config = dict(title='스타 오션 4', title_font_size=92, title_outline=4,
                  credits=['한글패치 '+version,'by Gideon'], credit_font_size=54, credit_outline=3)
    result = title_renderer(root,work)(raw,config)
    credit_preview(result, work/'credit-after.png')
    end = min([m.offset for m in archive.members if m.offset>member.offset]+[archive.logical_size])
    payload = slz3_encode.encode_reusing_chunks(result,member.slz,prior[member.offset:end])
    if len(payload)>end-member.offset:
        raise ValueError('Updated title exceeds its existing resource slot')
    target = prior[:member.offset]+payload.ljust(end-member.offset,b'\0')+prior[end:]
    assert len(target)==len(prior) and target[:member.offset]==prior[:member.offset] and target[end:]==prior[end:]
    assert so4loc.decompress_slz(payload)==result
    native_chunks = 0
    info = so4loc.parse_slz_header(payload,0,len(payload))
    cursor, produced = info.data_offset, 0
    while produced<info.uncompressed_size:
        length = struct.unpack_from('<H',payload,cursor)[0]
        extent = length or 65536
        expected = min(65536,info.uncompressed_size-produced)
        if info.mode==3 and length:
            assert slz3_encode.is_native_safe_chunk(payload[cursor+2:cursor+2+extent],expected)
            native_chunks += 1
        cursor += 2+extent
        produced += expected
    target_file, new_delta, verified_file = work/'after-package.bin',work/'new-title.xdelta',work/'verified-package.bin'
    target_file.write_bytes(target)
    run([encoder,'-e','-f','-D','-a','-A','-S','none','-s',source_file,target_file,new_delta])
    run([decoder,'-d','-f','-D','-R','-s',source_file,new_delta,verified_file])
    if verified_file.read_bytes()!=target:
        raise ValueError('New title delta round trip failed')

    # Preserve the previous inputs before the first update for this target version.
    for name, content in [('manifest-before-'+version+'.json',manifest.read_bytes()),
                          ('delta-before-'+version+'.xdelta',delta.read_bytes())]:
        backup = work/name
        if not backup.exists(): backup.write_bytes(content)
    replacement = copy.deepcopy(plan)
    updated = replacement['operations'][index]
    updated['targetHash'], updated['deltaHash'] = sha(target),sha(new_delta.read_bytes())
    replacement['version'] = version
    proof = dict(passed=True,version=version,label='0015.pkg',operation=index,
                 targetHash=updated['targetHash'],deltaHash=updated['deltaHash'],
                 previousTargetHash=operation['targetHash'],previousDeltaHash=operation['deltaHash'],
                 displayedCredit=config['credits'][0],author=config['credits'][1],
                 creditOnly=True,otherPackageMembersUnchanged=True,packageSizeAndOffsetsUnchanged=True,
                 nativeMode3ChunksVerified=native_chunks,deltaRoundTripExact=True,
                 preview=str(work/'credit-after.png'),installedGameModified=False)
    # Hash mismatches make an interrupted multi-file update fail closed.
    delta.write_bytes(new_delta.read_bytes())
    atomic_json(manifest,replacement)
    atomic_json(metadata,proof)
    atomic_json(work/('title-verification-'+version+'.json'),proof)
    print('Updated and verified title credit: '+config['credits'][0],flush=True)


def main():
    arguments = argparse.ArgumentParser()
    arguments.add_argument('--project',type=Path,required=True)
    arguments.add_argument('--development-root',type=Path,required=True)
    arguments.add_argument('--encoder',type=Path,required=True)
    arguments.add_argument('--version',default='v'+datetime.now(timezone(timedelta(hours=9))).strftime('%y%m%d'))
    args = arguments.parse_args()
    refresh(args.project,args.development_root,args.version,args.encoder)


if __name__=='__main__':
    main()
