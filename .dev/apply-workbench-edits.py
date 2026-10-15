"""Apply literal, checksum-guarded edits to the eight named editor source files."""
import hashlib
import json
from pathlib import Path

allowed = {
    'src/OurNotes.Director.Core/PreviewSoundtrack.cs',
    'src/OurNotes.Director.Core/RealtimePreviewSession.cs',
    'src/OurNotes.Director.WinUI/WindowsPreviewAudio.cs',
    'src/OurNotes.Director.WinUI/CreativeScenePage.cs',
    'src/OurNotes.Director.WinUI/CreativeScenePage.Workflow.cs',
    'src/OurNotes.Director.WinUI/HomeStudioPage.cs',
    'src/OurNotes.Director.WinUI/HomeStudioPage.Workflow.cs',
    'tests/OurNotes.Director.Desktop.Tests/CreativeExportTests.cs',
}
entries = []
for name in ('a', 'b'):
    entries.extend(json.loads(Path(f'.dev/workbench-edits-{name}.json').read_text(encoding='utf-8-sig')))
if len(entries) != len(allowed) or {entry['path'] for entry in entries} != allowed:
    raise ValueError('Unexpected editor source paths')
updates = {}
for entry in entries:
    path = Path(entry['path'])
    source = path.read_text(encoding='utf-8-sig').replace('\r\n', '\n')
    if hashlib.sha256(source.encode('utf-8')).hexdigest() != entry['sha256']:
        raise ValueError('Source changed before integration: ' + str(path))
    lines = source.splitlines(keepends=True)
    previous_end = 0
    for change in entry['edits']:
        start, end = change['start'], change['end']
        if not isinstance(start, int) or not isinstance(end, int) or not previous_end <= start <= end <= len(lines):
            raise ValueError('Invalid line range')
        if not isinstance(change['text'], str):
            raise ValueError('Replacement must be literal source text')
        previous_end = end
    for change in reversed(entry['edits']):
        lines[change['start']:change['end']] = change['text'].splitlines(keepends=True)
    updates[path] = ''.join(lines)
for path, source in updates.items():
    path.write_text(source, encoding='utf-8', newline='\n')
print(f'Applied {len(updates)} validated source updates')
