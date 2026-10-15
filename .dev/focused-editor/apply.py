"""Apply literal source edits only after validating both baseline and output hashes."""
import hashlib
import json
from pathlib import Path

updates = {}
for batch in range(6):
    entries = json.loads(Path(f'.dev/focused-editor/{batch}.json').read_text(encoding='utf-8-sig'))
    for entry in entries:
        p = Path(entry['path'])
        if p.is_absolute() or '..' in p.parts or p.as_posix() in updates:
            raise ValueError('Invalid or duplicate source path')
        if p.parts[0] not in ('src', 'tests') and p.as_posix() not in ('DESKTOP.md', 'CREATIVE_EDITOR_PROGRESS.md'):
            raise ValueError('Unexpected source path: ' + str(p))
        if entry['sha256'] is None:
            if p.exists(): raise ValueError('New file already exists: ' + str(p))
            text = ''
        else:
            text = p.read_text(encoding='utf-8-sig')
            if hashlib.sha256(text.encode('utf-8')).hexdigest() != entry['sha256']:
                raise ValueError('Baseline hash mismatch: ' + str(p))
        lines = text.splitlines(keepends=True)
        previous_end = 0
        for change in entry['edits']:
            a, b = change['start'], change['end']
            if not isinstance(a, int) or not isinstance(b, int) or not previous_end <= a <= b <= len(lines):
                raise ValueError('Invalid edit range: ' + str(p))
            previous_end = b
        for change in reversed(entry['edits']):
            lines[change['start']:change['end']] = change['text'].splitlines(keepends=True)
        result = ''.join(lines)
        if hashlib.sha256(result.encode('utf-8')).hexdigest() != entry['result']:
            raise ValueError('Output hash mismatch: ' + str(p))
        updates[p.as_posix()] = result
if len(updates) != 26: raise ValueError('Unexpected file count')
for name, result in updates.items():
    p = Path(name)
    p.parent.mkdir(parents=True, exist_ok=True)
    p.write_text(result, encoding='utf-8', newline='\n')
print('Applied 26 exact editor source and documentation updates.')
